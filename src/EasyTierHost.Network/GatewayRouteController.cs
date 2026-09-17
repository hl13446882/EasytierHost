using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

public sealed record RouteJournal(RouteSnapshot Snapshot, List<RouteEntry> Owned, bool DnsMayHaveChanged);

/// <summary>Single route writer. Journal is persisted before every mutation, including DNS.</summary>
public sealed class GatewayRouteController(IRouteApi api, IDnsController dns, IGatewayProbe probe, string journalPath)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RouteJournal? journal;
    public GatewayState State { get; private set; } = GatewayState.PhysicalOnly;
    private Task SaveAsync(CancellationToken ct) => ConfigurationStore.SaveAtomicAsync(journalPath, journal!, ct);

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(journalPath))
            {
                journal = JsonSerializer.Deserialize<RouteJournal>(await File.ReadAllTextAsync(journalPath, ct), ConfigurationStore.Json) ?? throw new IOException("Invalid route journal");
                await RollbackInternalAsync();
            }
        }
        finally { gate.Release(); }
    }

    public async Task ActivateAsync(GatewayContext context, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (State == GatewayState.GatewayActive) return;
            if (journal is not null || File.Exists(journalPath)) throw new InvalidOperationException("Recover previous route transaction first");
            if (!OverlayAddressPlan.IsClient(System.Net.IPAddress.Parse(context.OverlayIp))) throw new HostException("ETH102", "Client address outside DHCP pool");
            if (!context.UnderlayProtectionVerified) throw new HostException("ETH301", "Complete underlay socket protection must be verified before gateway activation");
            if (context.OverlayInterfaceIndex <= 0 || context.Endpoints.Count == 0) throw new HostException("ETH301", "Missing overlay interface or underlay endpoints");
            State = GatewayState.Capturing;
            var snapshot = await api.CaptureAsync(ct);
            if (snapshot.PhysicalInterfaceIndex == context.OverlayInterfaceIndex) throw new HostException("ETH301", "Physical and overlay interfaces must differ");
            journal = new(snapshot, [], false);
            await SaveAsync(ct);
            foreach (var route in RoutePlanner.Protect(snapshot, context.Endpoints)) await AddOwnedAsync(route, ct);
            State = GatewayState.UnderlayProtected;
            var probeRoutes = RoutePlanner.Probe(context);
            if (context.Endpoints.Any(ip => probeRoutes.Any(r => r.Destination == ip + "/32"))) throw new HostException("ETH301", "Probe target conflicts with underlay endpoint");
            foreach (var route in probeRoutes) await AddOwnedAsync(route, ct);
            State = GatewayState.Probing;
            if (!await probe.CheckAsync(context, ct)) throw new HostException("ETH203", "Gateway probe failed");
            journal = journal with { DnsMayHaveChanged = true };
            await SaveAsync(ct);
            await dns.ApplyAsync(snapshot, ct);
            foreach (var route in RoutePlanner.Default(context)) await AddOwnedAsync(route, ct);
            foreach (var route in probeRoutes) await RemoveOwnedAsync(route, ct);
            State = GatewayState.GatewayActive;
        }
        catch
        {
            if (journal is not null) await RollbackInternalAsync();
            else State = GatewayState.PhysicalOnly;
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task AddOwnedAsync(RouteEntry route, CancellationToken ct)
    {
        var current = await api.ListAsync(ct);
        // Do not adopt a pre-existing route: rollback must not delete someone else's state.
        if (current.Any(r => r.Destination == route.Destination))
            throw new HostException("ETH301", $"Pre-existing route conflicts with {route.Destination}");
        journal!.Owned.Add(route);
        await SaveAsync(ct);
        await api.AddAsync(route, ct);
    }
    private async Task RemoveOwnedAsync(RouteEntry route, CancellationToken ct)
    {
        if (!journal!.Owned.Any(r => RoutePlanner.SameIdentity(r, route))) return;
        if ((await api.ListAsync(ct)).Any(r => RoutePlanner.SameIdentity(r, route))) await api.DeleteAsync(route, ct);
        journal.Owned.RemoveAll(r => RoutePlanner.SameIdentity(r, route));
        await SaveAsync(ct);
    }
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await RollbackInternalAsync(); }
        finally { gate.Release(); }
    }
    private async Task RollbackInternalAsync()
    {
        if (journal is null) return;
        State = GatewayState.RollingBack;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var errors = new List<Exception>();
        // Restore physical Internet even if DNS restore fails.
        foreach (var route in journal.Owned.ToArray().Reverse())
            try { await RemoveOwnedAsync(route, timeout.Token); } catch (Exception ex) { errors.Add(ex); }
        if (journal.DnsMayHaveChanged)
            try { await dns.RestoreAsync(journal.Snapshot, timeout.Token); journal = journal with { DnsMayHaveChanged = false }; await SaveAsync(timeout.Token); }
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) { State = GatewayState.Faulted; throw new AggregateException("ETH302: rollback incomplete; journal retained", errors); }
        File.Delete(journalPath);
        journal = null;
        State = GatewayState.PhysicalOnly;
    }
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (State != GatewayState.GatewayActive || journal is null) return;
            var physical = await api.CaptureAsync(ct);
            if (physical.PhysicalInterfaceIndex != journal.Snapshot.PhysicalInterfaceIndex || physical.PhysicalGateway != journal.Snapshot.PhysicalGateway || physical.PhysicalIpv4 != journal.Snapshot.PhysicalIpv4)
            { await RollbackInternalAsync(); return; }
            var actual = await api.ListAsync(ct);
            // Missing ownership or foreign replacements require a fresh probe, never overwrite them.
            if (journal.Owned.Any(owned => !actual.Any(r => RoutePlanner.SameIdentity(r, owned)))) await RollbackInternalAsync();
        }
        finally { gate.Release(); }
    }
}
