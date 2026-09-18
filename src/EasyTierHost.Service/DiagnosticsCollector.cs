using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Network;

namespace EasyTierHost.Service;

public static class DiagnosticsCollector
{
    public const string BuildId = "0.2.0";
    public const string CoreBase = "2.6.4";

    public static async Task<HostDiagnosticsSnapshot> CollectAsync(
        NetworkProfile profile,
        string? stateDirectory,
        ICommandRunner runner,
        IRouteApi routes,
        CancellationToken ct = default)
    {
        RouteSnapshot? physical = null;
        IReadOnlyList<RouteMetricDiagnostics> metricView = [];
        IReadOnlyList<RouteEntry> currentRoutes = [];
        var captureErrors = new List<string>();

        try { physical = await routes.CaptureAsync(ct); }
        catch (Exception ex) { captureErrors.Add("Physical:" + ex.GetType().Name); }
        try { currentRoutes = await routes.ListAsync(ct); }
        catch (Exception ex) { captureErrors.Add("Routes:" + ex.GetType().Name); }
        try { metricView = await RouteDiagnostics.ReadAsync(routes, runner, ct); }
        catch (Exception ex) { captureErrors.Add("Metrics:" + ex.GetType().Name); }

        var runtime = await ReadRuntimeStatusAsync(stateDirectory, ct);
        var gatewayState = await ReadGatewayStateAsync(profile, stateDirectory, ct);
        var runtimeState = NormalizeRuntimeState(profile, runtime.State, gatewayState);
        var recentErrors = stateDirectory is null
            ? []
            : await new RuntimeErrorHistory(Path.Combine(stateDirectory, "recent-errors.json")).ReadAsync(ct);

        var overlayAdapter = FindOverlayAdapter(profile);
        string? overlayIp = overlayAdapter?.Ip;
        string? overlayInterface = overlayAdapter?.Name;
        var peerCount = 0;
        var observedEndpoints = new HashSet<IPAddress>();
        if (IPAddress.TryParse(profile.SeedPhysicalIp, out var seed) && IsUnderlayEndpoint(seed)) observedEndpoints.Add(seed);
        string? coreError = null;

        try
        {
            var reader = new CoreStatusReader(runner, profile);
            var node = await reader.ReadNodeAsync(ct);
            var peers = await reader.ReadPeersAsync(ct);
            peerCount = peers.Count(peer => peer.PeerId != node.PeerId);
            foreach (var endpoint in peers.SelectMany(peer => peer.Endpoints).Where(IsUnderlayEndpoint)) observedEndpoints.Add(endpoint);
            if (profile.Role != NodeRole.Seed && node.OverlayIp is not null)
            {
                overlayIp = node.OverlayIp;
                overlayInterface ??= profile.DeviceName;
            }
            else if (profile.Role == NodeRole.Seed)
            {
                // Seed is intentionally TUN-less. Never report an unrelated local 10.10/16 adapter as Seed Overlay.
                overlayIp = null;
                overlayInterface = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            coreError = ex.GetType().Name;
        }

        var protectedEndpoints = physical is null
            ? []
            : observedEndpoints
                .Where(endpoint => IsProtected(endpoint, physical, currentRoutes))
                .OrderBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)
                .Select(endpoint => endpoint.ToString())
                .ToArray();

        return new HostDiagnosticsSnapshot(
            BuildId,
            CoreBase,
            profile.SchemaVersion,
            profile.Role,
            DateTimeOffset.UtcNow,
            runtimeState,
            runtime.CorePid,
            physical?.PhysicalInterfaceName,
            physical?.PhysicalInterfaceIndex,
            physical?.PhysicalIpv4,
            physical?.PhysicalGateway,
            physical?.OriginalDnsServers ?? [],
            metricView,
            overlayInterface,
            overlayIp,
            peerCount,
            gatewayState,
            profile.SeedPhysicalIp,
            protectedEndpoints,
            recentErrors,
            captureErrors.Count == 0 ? null : string.Join(';', captureErrors),
            coreError);
    }

    public static string NormalizeRuntimeState(NetworkProfile profile, string runtimeState, string gatewayState)
    {
        if (profile.Role == NodeRole.Gateway && gatewayState == "GatewayReady") return "GatewayReady";
        if (profile.Role == NodeRole.Client && profile.EnableInternetGateway && gatewayState == "GatewayActive") return "ClientGatewayActive";
        return runtimeState;
    }

    private static (string Name, string Ip)? FindOverlayAdapter(NetworkProfile profile)
    {
        if (profile.Role == NodeRole.Seed) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.Name.Equals(profile.DeviceName, StringComparison.Ordinal)))
        {
            var address = nic.GetIPProperties().UnicastAddresses
                .Select(item => item.Address)
                .FirstOrDefault(OverlayAddressPlan.IsOverlay);
            if (address is not null) return (nic.Name, address.ToString());
        }
        return null;
    }

    private static bool IsUnderlayEndpoint(IPAddress ip) =>
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !OverlayAddressPlan.IsOverlay(ip)
        && !IPAddress.IsLoopback(ip)
        && ip.GetAddressBytes()[0] is > 0 and < 224;

    private static bool IsProtected(IPAddress endpoint, RouteSnapshot physical, IReadOnlyList<RouteEntry> routes)
    {
        var destination = endpoint + "/32";
        return routes.Any(route =>
            route.Destination == destination
            && route.InterfaceIndex == physical.PhysicalInterfaceIndex
            && (route.NextHop == physical.PhysicalGateway || route.NextHop == "0.0.0.0"));
    }

    private static async Task<(string State, int? CorePid)> ReadRuntimeStatusAsync(string? stateDirectory, CancellationToken ct)
    {
        if (stateDirectory is null) return ("StateDirectoryNotProvided", null);
        var path = Path.Combine(stateDirectory, "status.json");
        if (!File.Exists(path)) return ("NotStarted", null);
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? "Unknown" : "Unknown";
            int? pid = root.TryGetProperty("pid", out var pidElement) && pidElement.ValueKind == JsonValueKind.Number
                ? pidElement.GetInt32()
                : null;
            return (state, pid);
        }
        catch (JsonException) { return ("InvalidStatus", null); }
        catch (IOException) { return ("StatusUnavailable", null); }
    }

    private static async Task<string> ReadGatewayStateAsync(NetworkProfile profile, string? stateDirectory, CancellationToken ct)
    {
        if (profile.Role == NodeRole.Client && !profile.EnableInternetGateway) return "Disabled";
        if (profile.Role is not (NodeRole.Client or NodeRole.Gateway)) return "NotApplicable";
        if (stateDirectory is null) return "StateDirectoryNotProvided";
        var fileName = profile.Role == NodeRole.Client ? "client-gateway-status.json" : "gateway-status.json";
        var path = Path.Combine(stateDirectory, fileName);
        if (!File.Exists(path)) return "Pending";
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.TryGetProperty("state", out var state)
                ? state.GetString() ?? "Unknown"
                : "Unknown";
        }
        catch (JsonException) { return "InvalidStatus"; }
        catch (IOException) { return "StatusUnavailable"; }
    }
}