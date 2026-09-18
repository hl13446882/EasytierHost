using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Gateway;

/// <summary>Journals privileged gateway changes before applying them. No routes or system DNS are changed here.</summary>
public sealed class GatewayBootstrapper(IGatewayPlatform platform, IGatewayDnsRuntime dns, string journalPath)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GatewayJournal? journal;
    public GatewayServerState State { get; private set; } = GatewayServerState.Stopped;
    private Task SaveAsync(CancellationToken ct) => ConfigurationStore.SaveAtomicAsync(journalPath, journal!, ct);
    private static async Task Step(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not HostException and not OperationCanceledException)
        {
            throw new HostException("ETH202", $"{name} failed");
        }
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(journalPath)) return;
            journal = JsonSerializer.Deserialize<GatewayJournal>(await File.ReadAllTextAsync(journalPath, ct), ConfigurationStore.Json)
                ?? throw new HostException("ETH302", "Invalid gateway journal");
            if (journal.Snapshot.Platform != platform.Platform) throw new HostException("ETH302", "Gateway journal belongs to another operating system");
            await RollbackInternalAsync();
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(RouteSnapshot physical, GatewayAdapter overlay, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (State == GatewayServerState.Ready) return;
            if (journal is not null || File.Exists(journalPath)) throw new HostException("ETH302", "Recover gateway journal before activation");
            if (overlay.Address != OverlayAddressPlan.Gateway || overlay.Index <= 0 || overlay.Index == physical.PhysicalInterfaceIndex)
                throw new HostException("ETH201", "Gateway TUN must be ready on a separate interface");
            State = GatewayServerState.Preparing;
            try { journal = new(await platform.CaptureAsync(physical, overlay, ct), false, false); }
            catch (Exception ex) when (ex is not HostException and not OperationCanceledException) { throw new HostException("ETH202", "gateway capture failed"); }
            await SaveAsync(ct);
            journal = journal with { ForwardingMayHaveChanged = true };
            await SaveAsync(ct);
            await Step("forwarding", () => platform.EnableForwardingAsync(journal.Snapshot, ct));
            journal = journal with { NatMayExist = true };
            await SaveAsync(ct);
            await Step("WinNAT", () => platform.CreateNatAsync(journal.Snapshot, ct));
            State = GatewayServerState.DnsStarting;
            await Step("gateway DNS", () => dns.StartAsync(ct));
            try
            {
                if (!await platform.VerifyAsync(journal.Snapshot, ct) || !await dns.CheckAsync(ct))
                    throw new HostException("ETH202", "Gateway NAT/forwarding/DNS health check failed");
            }
            catch (Exception ex) when (ex is not HostException and not OperationCanceledException) { throw new HostException("ETH202", "gateway verify failed"); }
            State = GatewayServerState.Ready;
        }
        catch
        {
            if (journal is not null) await RollbackInternalAsync();
            else State = GatewayServerState.Stopped;
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return State == GatewayServerState.Ready && journal is not null && await platform.VerifyAsync(journal.Snapshot, ct) && await dns.CheckAsync(ct); }
        finally { gate.Release(); }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await RollbackInternalAsync(); }
        finally { gate.Release(); }
    }
    private async Task RollbackInternalAsync()
    {
        State = GatewayServerState.RollingBack;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var errors = new List<Exception>();
        try { await dns.StopAsync(timeout.Token); } catch (Exception ex) { errors.Add(ex); }
        if (journal is not null)
        {
            if (journal.NatMayExist)
                try
                {
                    await platform.RemoveNatAsync(journal.Snapshot, timeout.Token);
                    journal = journal with { NatMayExist = false };
                    await SaveAsync(timeout.Token);
                }
                catch (Exception ex) { errors.Add(ex); }
            // Attempt forwarding restore even if NAT/DNS cleanup failed.
            if (journal.ForwardingMayHaveChanged)
                try
                {
                    await platform.RestoreForwardingAsync(journal.Snapshot, timeout.Token);
                    journal = journal with { ForwardingMayHaveChanged = false };
                    await SaveAsync(timeout.Token);
                }
                catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) { State = GatewayServerState.Faulted; throw new AggregateException("ETH302: gateway rollback incomplete; journal retained", errors); }
        if (File.Exists(journalPath)) File.Delete(journalPath);
        journal = null;
        State = GatewayServerState.Stopped;
    }
}
