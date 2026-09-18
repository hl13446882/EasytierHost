namespace EasyTierHost.Client.Windows.Services;

/// <summary>
/// UI-side readiness observer only. Route/DNS ownership stays inside EasyTierHost.Service.
/// A failed Internet gateway does not disconnect the overlay.
/// </summary>
public sealed class ClientGatewayCoordinator(ClientHealthMonitor health)
{
    public async Task<ClientStatusSnapshot> WaitForOverlayAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ClientStatusSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await health.ReadAsync(ct);
            if (last.OverlayReady) return last;
            if (last.ServiceState is "STOPPED" or "NotInstalled") break;
            await Task.Delay(500, ct);
        }
        return last ?? await health.ReadAsync(ct);
    }

    public async Task<ClientStatusSnapshot> WaitForInternetGatewayAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ClientStatusSnapshot last = await health.ReadAsync(ct);
        while (DateTimeOffset.UtcNow < deadline && last.OverlayReady && last.InternetGatewayEnabled && last.GatewayState != "GatewayActive")
        {
            await Task.Delay(750, ct);
            last = await health.ReadAsync(ct);
        }
        return last;
    }
}
