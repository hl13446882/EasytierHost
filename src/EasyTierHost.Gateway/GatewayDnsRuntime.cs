using System.Net;
using EasyTierHost.Core;

namespace EasyTierHost.Gateway;

public sealed class GatewayDnsRuntime(IPEndPoint listen, IReadOnlyList<IPEndPoint> upstreams) : IGatewayDnsRuntime
{
    private CancellationTokenSource? stopping;
    private Task? running;
    public async Task StartAsync(CancellationToken ct)
    {
        if (running is { IsCompleted: false }) return;
        var forwarder = new DnsForwarderService(listen, upstreams);
        stopping?.Dispose(); stopping = new CancellationTokenSource();
        running = forwarder.RunAsync(stopping.Token);
        var completed = await Task.WhenAny(forwarder.Ready, running).WaitAsync(ct);
        await completed;
        if (running.IsCompleted) throw new IOException("DNS forwarder exited before ready");
    }
    public async Task<bool> CheckAsync(CancellationToken ct) => running is { IsCompleted: false }
        && await DnsMessage.ProbeAsync(listen, false, ct) && await DnsMessage.ProbeAsync(listen, true, ct);
    public async Task StopAsync(CancellationToken ct)
    {
        if (stopping is not null) await stopping.CancelAsync();
        if (running is not null)
        {
            try { await running.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            // A failed DNS task must not prevent network cleanup, but startup/health sees its failure.
            catch (Exception) when (running.IsFaulted) { }
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); stopping?.Dispose(); }
}
