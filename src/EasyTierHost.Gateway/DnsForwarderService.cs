using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using EasyTierHost.Core;

namespace EasyTierHost.Gateway;

/// <summary>Bounded UDP/TCP DNS forwarder, with transaction matching and per-query upstream sockets.</summary>
public sealed class DnsForwarderService(IPEndPoint listen, IReadOnlyList<IPEndPoint> upstreams)
{
    private readonly SemaphoreSlim capacity = new(128);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public async Task RunAsync(CancellationToken ct)
    {
        if (upstreams.Count == 0 || upstreams.Any(u => u.Equals(listen))) throw new ArgumentException("DNS requires non-recursive upstreams");
        using var udp = new UdpClient(listen);
        var tcp = new TcpListener(listen);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tcp.Start(128);
        ready.TrySetResult();
        try
        {
            var tasks = new[] { UdpLoopAsync(udp, stopping.Token), TcpLoopAsync(tcp, stopping.Token) };
            await Task.WhenAny(tasks);
            await stopping.CancelAsync();
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        finally { tcp.Stop(); }
    }
    private async Task UdpLoopAsync(UdpClient listener, CancellationToken ct)
    {
        var pending = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await listener.ReceiveAsync(ct);
                if (!await capacity.WaitAsync(0, ct)) continue;
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(HandleAsync());
                async Task HandleAsync()
                {
                    try
                    {
                        var response = await ResolveAsync(packet.Buffer, false, ct);
                        await listener.SendAsync(response, packet.RemoteEndPoint, ct);
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
                    finally { capacity.Release(); }
                }
            }
        }
        finally { await Task.WhenAll(pending); }
    }
    private async Task TcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        var pending = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (!await capacity.WaitAsync(0, ct)) { client.Dispose(); continue; }
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(HandleAsync(client, ct));
            }
        }
        finally { await Task.WhenAll(pending); }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var stream = client.GetStream();
                while (!timeout.IsCancellationRequested)
                {
                    var query = await DnsMessage.ReadFrameAsync(stream, timeout.Token);
                    var response = await ResolveAsync(query, true, timeout.Token);
                    await DnsMessage.WriteFrameAsync(stream, response, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
            finally { capacity.Release(); }
        }
    }
    public async Task<byte[]> ResolveAsync(byte[] query, bool tcp, CancellationToken ct)
    {
        if (query.Length < 12 || (query[2] & 0x80) != 0 || BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)) != 1) throw new IOException("Invalid DNS query");
        DnsMessage.Question(query, out _);
        var forwarded = query.ToArray();
        System.Security.Cryptography.RandomNumberGenerator.Fill(forwarded.AsSpan(0, 2));
        foreach (var upstream in upstreams)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var response = await DnsMessage.ExchangeAsync(upstream, forwarded, tcp, timeout.Token);
                if (DnsMessage.IsResponseTo(forwarded, response) && (response[3] & 15) is not (2 or 5))
                { response[0] = query[0]; response[1] = query[1]; return response; }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        }
        return DnsMessage.Failure(query);
    }
}
