using System.Net;
using System.Text.Json.Serialization;

namespace EasyTierHost.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<NodeRole>))]
public enum NodeRole { Seed, Gateway, Dedicated, Client }
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
public sealed class HostException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
