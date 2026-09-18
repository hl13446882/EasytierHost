using System.Net;
using System.Text.Json.Serialization;

namespace EasyTierHost.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<NodeRole>))]
public enum NodeRole { Seed, Gateway, Dedicated, Client }
[JsonConverter(typeof(JsonStringEnumConverter<ServerOsType>))]
public enum ServerOsType { Windows, Linux }
public enum GatewayState { PhysicalOnly, Capturing, UnderlayProtected, Probing, GatewayActive, RollingBack, Faulted }
public sealed record NetworkProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string NetworkName { get; init; }
    // A separate ACL-protected secret file; never serialize the secret into profiles or logs.
    public required string SecretFile { get; init; }
    public NodeRole Role { get; init; } = NodeRole.Client;
    public string? SeedPhysicalIp { get; init; }
    public int Port { get; init; } = 11010;
    public int? DedicatedIndex { get; init; }
    public string CorePath { get; init; } = "easytier-core";
    public string CliPath { get; init; } = "easytier-cli";
    public int RpcPort { get; init; } = 15888;
    public string DeviceName { get; init; } = "easytierhost";
    public bool EnableInternetGateway { get; init; }
    public string[] DnsUpstreams { get; init; } = [];
    public bool AllowPublicDnsFallback { get; init; }
}

/// <summary>Runtime-only Core launch data. Physical binding is discovered by Host and is never user configuration.</summary>
public sealed record CoreLaunchOptions
{
    public string? UnderlaySourceIpv4 { get; init; }
}

/// <summary>SSH credentials are runtime-only. Password is deliberately excluded from JSON and ToString output.</summary>
public sealed class RemoteHostCredential
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? PrivateKeyPath { get; init; }
    [JsonIgnore] public string? Password { get; init; }
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

public sealed record DeploymentRequest
{
    public required ServerOsType OsType { get; init; }
    public required NodeRole Role { get; init; }
    public required RemoteHostCredential Remote { get; init; }
    public required string LocalPackageDirectory { get; init; }
    public required string RemoteInstallDirectory { get; init; }
    public required string LocalProfilePath { get; init; }
    public required string RemoteProfilePath { get; init; }
    public required string RemoteStateDirectory { get; init; }
    public string ServiceName { get; init; } = "EasyTierHost";
}

public sealed record DeploymentResult(bool Success, string Code, string Message)
{
    public static DeploymentResult Ok(string message = "OK") => new(true, "OK", message);
    public static DeploymentResult Fail(string code, string message) => new(false, code, message);
}

public sealed record RemoteCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class OverlayAddressPlan
{
    public const string Cidr = "10.10.0.0/16";
    public const string Gateway = "10.10.0.1";
    public const string DhcpStart = "10.10.0.11";
    public const string DhcpEnd = "10.10.255.254";
    public static uint Number(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new ArgumentException("IPv4 required");
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }
    public static bool IsOverlay(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (Number(ip) >> 16) == 0x0a0a;
    public static bool IsClient(IPAddress ip) => IsOverlay(ip) && Number(ip) >= Number(IPAddress.Parse(DhcpStart)) && Number(ip) <= Number(IPAddress.Parse(DhcpEnd));
}
public sealed record RouteEntry(string Destination, string NextHop, int InterfaceIndex, int Metric)
{
    public string OwnerTag { get; init; } = "EasyTierHost";
    public bool CreatedByEasyTierHost { get; init; } = true;
}
public sealed record RouteSnapshot(string PhysicalInterfaceName, int PhysicalInterfaceIndex,
    string PhysicalIpv4, string PhysicalGateway, int InterfaceMetric,
    IReadOnlyList<RouteEntry> DefaultRoutes, IReadOnlyList<RouteEntry> LocalLanRoutes,
    IReadOnlyList<string> OriginalDnsServers)
{
    public DnsClientSnapshot? DnsState { get; init; }
}
public sealed record DnsDomain(string Name, bool RoutingOnly);
public sealed record DnsClientSnapshot(string Platform, int InterfaceIndex, string InterfaceIdentity,
    IReadOnlyList<string> Servers, IReadOnlyList<DnsDomain> Domains, bool DefaultRoute, string OwnerToken);
public sealed record GatewayContext(int OverlayInterfaceIndex, string OverlayIp, IReadOnlyList<IPAddress> Endpoints,
    bool UnderlayProtectionVerified = false);
public sealed record HealthStatus(bool UnderlayOk, bool SeedReachable, bool OverlayOk, bool GatewayReachable, bool DnsOk, bool InternetOk);

/// <summary>Route metric view used only for diagnostics; it does not participate in route ownership equality.</summary>
public sealed record RouteMetricDiagnostics(string Destination, string NextHop, int InterfaceIndex,
    int RouteMetric, int InterfaceMetric, int TotalMetric);

/// <summary>Sanitized runtime error persisted in the Host state directory. It must never contain credentials or network secrets.</summary>
public sealed record RuntimeErrorRecord(DateTimeOffset TimestampUtc, string Code, string Message);

/// <summary>Stable diagnostics contract consumed by the Windows/Linux client tools and multi-node acceptance scripts.</summary>
public sealed record HostDiagnosticsSnapshot(
    string BuildId,
    string CoreBase,
    int Schema,
    NodeRole Role,
    DateTimeOffset ObservedUtc,
    string RuntimeState,
    int? CorePid,
    string? PhysicalInterface,
    int? PhysicalInterfaceIndex,
    string? PhysicalIpv4,
    string? PhysicalGateway,
    IReadOnlyList<string> PhysicalDnsServers,
    IReadOnlyList<RouteMetricDiagnostics> RouteMetrics,
    string? OverlayInterface,
    string? OverlayIp,
    int PeerCount,
    string GatewayState,
    string? SeedPhysicalIp,
    IReadOnlyList<string> ProtectedEndpoints,
    IReadOnlyList<RuntimeErrorRecord> RecentErrors,
    string? CaptureError,
    string? CoreError);

public sealed class HostException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}