using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Client.Windows.Services;
using EasyTierHost.Core;

namespace EasyTierHost.Client.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClientBootstrapper bootstrapper;
    private readonly ClientHealthMonitor health;
    private readonly EasyTierClientService service;
    private string seedPhysicalIp = string.Empty;
    private string networkName = "company-overlay";
    private bool enableInternetGateway = true;
    private bool isBusy;
    private string serviceState = "Unknown";
    private string overlayIp = "-";
    private string gatewayState = "-";
    private string statusMessage = "尚未检查";
    private string details = string.Empty;

    public MainViewModel(ClientBootstrapper bootstrapper, ClientHealthMonitor health, EasyTierClientService service)
    {
        this.bootstrapper = bootstrapper;
        this.health = health;
        this.service = service;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SeedPhysicalIp { get => seedPhysicalIp; set => Set(ref seedPhysicalIp, value); }
    public string NetworkName { get => networkName; set => Set(ref networkName, value); }
    public bool EnableInternetGateway { get => enableInternetGateway; set => Set(ref enableInternetGateway, value); }
    public bool IsBusy { get => isBusy; private set => Set(ref isBusy, value); }
    public string ServiceState { get => serviceState; private set => Set(ref serviceState, value); }
    public string OverlayIp { get => overlayIp; private set => Set(ref overlayIp, value); }
    public string GatewayState { get => gatewayState; private set => Set(ref gatewayState, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string Details { get => details; private set => Set(ref details, value); }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var profile = await bootstrapper.LoadProfileAsync(ct);
            if (profile is not null)
            {
                SeedPhysicalIp = profile.SeedPhysicalIp ?? string.Empty;
                NetworkName = profile.NetworkName;
                EnableInternetGateway = profile.EnableInternetGateway;
            }
        }
        catch (Exception ex) { Details = SafeMessage(ex); }
        await RefreshAsync(ct);
    }

    public Task ConnectAsync(string? secret, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        var state = await bootstrapper.ConnectAsync(new(
            SeedPhysicalIp.Trim(), NetworkName.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret,
            EnableInternetGateway), ct);
        Apply(state);
    });

    public Task DisconnectAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        await bootstrapper.DisconnectAsync(ct);
        Apply(await health.ReadAsync(ct));
    });

    public Task ReconnectAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        Apply(await bootstrapper.ReconnectAsync(ct));
    });

    public Task RefreshAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        Apply(await health.ReadAsync(ct));
    }, clearDetails: false);

    public Task DiagnosticsAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        var result = await service.DiagnosticsAsync(ct);
        Details = result.Success ? FormatDiagnostics(result.StdOut) : $"diagnostics exit {result.ExitCode}";
    }, clearDetails: false);

    public static string FormatDiagnostics(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<HostDiagnosticsSnapshot>(json, ConfigurationStore.Json);
            if (snapshot is null) return json.Trim();

            var text = new StringBuilder();
            text.AppendLine($"Build: {snapshot.BuildId} / EasyTier {snapshot.CoreBase}");
            text.AppendLine($"运行状态: {snapshot.RuntimeState}    Core PID: {snapshot.CorePid?.ToString() ?? "-"}");
            text.AppendLine($"物理网络: {snapshot.PhysicalInterface ?? "-"}  {snapshot.PhysicalIpv4 ?? "-"}  网关 {snapshot.PhysicalGateway ?? "-"}");
            text.AppendLine($"虚拟网络: {snapshot.OverlayInterface ?? "-"}  {snapshot.OverlayIp ?? "-"}  Peers {snapshot.PeerCount}");
            text.AppendLine($"Internet Gateway: {snapshot.GatewayState}");
            text.AppendLine($"Seed: {snapshot.SeedPhysicalIp ?? "-"}");

            if (snapshot.ProtectedEndpoints.Count > 0)
                text.AppendLine("物理保护端点: " + string.Join(", ", snapshot.ProtectedEndpoints));
            if (snapshot.PhysicalDnsServers.Count > 0)
                text.AppendLine("物理 DNS: " + string.Join(", ", snapshot.PhysicalDnsServers));

            var defaultRoutes = snapshot.RouteMetrics
                .Where(route => route.Destination is "0.0.0.0/0" or "0.0.0.0/1" or "128.0.0.0/1")
                .OrderBy(route => route.Destination, StringComparer.Ordinal)
                .ThenBy(route => route.TotalMetric)
                .ToArray();
            if (defaultRoutes.Length > 0)
            {
                text.AppendLine("路由:");
                foreach (var route in defaultRoutes)
                    text.AppendLine($"  {route.Destination} -> {route.NextHop}  if={route.InterfaceIndex}  metric={route.RouteMetric}+{route.InterfaceMetric}={route.TotalMetric}");
            }

            if (snapshot.RecentErrors.Count > 0)
            {
                text.AppendLine("最近错误:");
                foreach (var error in snapshot.RecentErrors.TakeLast(5))
                    text.AppendLine($"  {error.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {error.Code}  {error.Message}");
            }
            if (snapshot.CaptureError is not null) text.AppendLine("网络采集: " + snapshot.CaptureError);
            if (snapshot.CoreError is not null) text.AppendLine("Core RPC: " + snapshot.CoreError);
            return text.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private async Task ExecuteAsync(Func<Task> action, bool clearDetails = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (clearDetails) Details = string.Empty;
        try { await action(); }
        catch (Exception ex)
        {
            StatusMessage = "操作失败";
            Details = SafeMessage(ex);
            try { Apply(await health.ReadAsync(CancellationToken.None)); } catch { }
        }
        finally { IsBusy = false; }
    }

    private void Apply(ClientStatusSnapshot state)
    {
        ServiceState = state.ServiceState;
        OverlayIp = state.OverlayIp ?? "-";
        GatewayState = state.GatewayState;
        StatusMessage = state.Message;
    }

    private static string SafeMessage(Exception ex) => ex is HostException
        ? ex.Message
        : $"{ex.GetType().Name}: operation failed";

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
