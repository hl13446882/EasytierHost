namespace EasyTierHost.Abstractions;

public interface IRouteApi
{
    Task<RouteSnapshot> CaptureAsync(CancellationToken ct);
    Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct);
    Task AddAsync(RouteEntry route, CancellationToken ct);
    Task DeleteAsync(RouteEntry route, CancellationToken ct);
}
public interface IDnsController
{
    Task<RouteSnapshot> CaptureAsync(RouteSnapshot snapshot, GatewayContext context, CancellationToken ct) => Task.FromResult(snapshot);
    Task ApplyAsync(RouteSnapshot snapshot, CancellationToken ct);
    Task RestoreAsync(RouteSnapshot snapshot, CancellationToken ct);
}
public interface IGatewayProbe
{
    Task<bool> CheckAsync(GatewayContext context, CancellationToken ct);
}
public interface IEasyTierProcessManager : IAsyncDisposable
{
    int? ProcessId { get; }
    bool IsRunning { get; }
    string? UnderlaySourceIpv4 { get; }
    Task StartAsync(NetworkProfile profile, string configurationPath, CoreLaunchOptions options, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<int> WaitForExitAsync(CancellationToken ct);
}
