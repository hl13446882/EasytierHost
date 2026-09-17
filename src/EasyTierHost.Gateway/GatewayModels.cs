using EasyTierHost.Abstractions;

namespace EasyTierHost.Gateway;

public sealed record GatewayAdapter(int Index, string Name, string Identity, string Address);
public sealed record ForwardingState(int Index, string Identity, bool Enabled);
public sealed record GatewayPlatformSnapshot(string ResourceName, string OwnerToken, string Platform,
    RouteSnapshot Physical, GatewayAdapter Overlay, bool GlobalForwardingEnabled, IReadOnlyList<ForwardingState> Forwarding);
public sealed record GatewayJournal(GatewayPlatformSnapshot Snapshot, bool ForwardingMayHaveChanged, bool NatMayExist);
public interface IGatewayPlatform
{
    string Platform { get; }
    Task<GatewayPlatformSnapshot> CaptureAsync(RouteSnapshot physical, GatewayAdapter overlay, CancellationToken ct);
    Task EnableForwardingAsync(GatewayPlatformSnapshot snapshot, CancellationToken ct);
    Task RestoreForwardingAsync(GatewayPlatformSnapshot snapshot, CancellationToken ct);
    Task CreateNatAsync(GatewayPlatformSnapshot snapshot, CancellationToken ct);
    Task RemoveNatAsync(GatewayPlatformSnapshot snapshot, CancellationToken ct);
    Task<bool> VerifyAsync(GatewayPlatformSnapshot snapshot, CancellationToken ct);
}
public interface IGatewayDnsRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task<bool> CheckAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
public enum GatewayServerState { Stopped, Preparing, DnsStarting, Ready, RollingBack, Faulted }
