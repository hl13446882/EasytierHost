using System.Text.Json;
using EasyTierHost.Core;

namespace EasyTierHost.Client.Windows.Services;

public sealed record ClientStatusSnapshot(
    string ServiceState,
    bool OverlayReady,
    string? OverlayIp,
    bool InternetGatewayEnabled,
    string GatewayState,
    string Message,
    DateTimeOffset ObservedUtc);

public sealed class ClientHealthMonitor(ClientRuntimePaths paths, EasyTierClientService service)
{
    private string GatewayStatusPath => Path.Combine(paths.StateDirectory, "client-gateway-status.json");

    public async Task<ClientStatusSnapshot> ReadAsync(CancellationToken ct)
    {
        var serviceState = await service.QueryStateAsync(ct);
        if (serviceState != "RUNNING")
            return new(serviceState, false, null, await GatewayEnabledAsync(ct), "Inactive", "未连接", DateTimeOffset.UtcNow);

        var gatewayEnabled = await GatewayEnabledAsync(ct);
        var ready = await service.ReadyAsync(ct);
        var overlayIp = ready.Success ? ReadString(ready.StdOut, "overlayIp") : null;
        var gatewayState = gatewayEnabled ? await ReadGatewayStateAsync(ct) : "Disabled";
        var message = !ready.Success
            ? "服务已启动，正在等待虚拟网地址/Peer"
            : gatewayEnabled && gatewayState != "GatewayActive"
                ? "虚拟局域网已连接；互联网网关尚未激活，物理网络保持可用"
                : gatewayEnabled
                    ? "虚拟局域网与互联网网关已连接"
                    : "虚拟局域网已连接（未接管互联网网关）";

        return new(serviceState, ready.Success, overlayIp, gatewayEnabled, gatewayState, message, DateTimeOffset.UtcNow);
    }

    private async Task<bool> GatewayEnabledAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.ProfilePath)) return false;
        try { return (await ConfigurationStore.LoadAsync(paths.ProfilePath, ct)).EnableInternetGateway; }
        catch { return false; }
    }

    private async Task<string> ReadGatewayStateAsync(CancellationToken ct)
    {
        if (!File.Exists(GatewayStatusPath)) return "Pending";
        try
        {
            await using var stream = File.OpenRead(GatewayStatusPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.TryGetProperty("state", out var state) ? state.GetString() ?? "Unknown" : "Unknown";
        }
        catch (IOException) { return "Pending"; }
        catch (JsonException) { return "Unknown"; }
    }

    private static string? ReadString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
