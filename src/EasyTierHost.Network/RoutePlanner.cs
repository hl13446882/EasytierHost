using System.Net;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Network;

public static class RoutePlanner
{
    public static RouteEntry[] Protect(RouteSnapshot snapshot, IEnumerable<IPAddress> endpoints) => endpoints
        .Append(IPAddress.Parse(snapshot.PhysicalGateway)).Distinct()
        .Select(ip =>
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || OverlayAddressPlan.IsOverlay(ip) || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] is 0 or >= 224)
                throw new HostException("ETH003", "Invalid underlay endpoint");
            return new RouteEntry(ip + "/32", ip.ToString() == snapshot.PhysicalGateway ? "0.0.0.0" : snapshot.PhysicalGateway, snapshot.PhysicalInterfaceIndex, 1);
        }).ToArray();
    public static RouteEntry[] Probe(GatewayContext context) => [new("1.1.1.1/32", OverlayAddressPlan.Gateway, context.OverlayInterfaceIndex, 1), new("8.8.8.8/32", OverlayAddressPlan.Gateway, context.OverlayInterfaceIndex, 1)];
    // Split defaults preserve all original defaults and outrank their combined metrics.
    public static RouteEntry[] Default(GatewayContext context) => [new("0.0.0.0/1", OverlayAddressPlan.Gateway, context.OverlayInterfaceIndex, 1), new("128.0.0.0/1", OverlayAddressPlan.Gateway, context.OverlayInterfaceIndex, 1)];
    public static bool SameIdentity(RouteEntry a, RouteEntry b) => a.Destination == b.Destination && a.NextHop == b.NextHop && a.InterfaceIndex == b.InterfaceIndex && a.Metric == b.Metric;
}

public sealed class UnderlayRouteProtector(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<IPAddress, DateTimeOffset> endpoints = [];
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(120);
    public IReadOnlyCollection<IPAddress> Update(IEnumerable<IPAddress> active)
    {
        var now = time.GetUtcNow();
        foreach (var ip in active) endpoints[ip] = now;
        foreach (var ip in endpoints.Where(p => now - p.Value >= GracePeriod).Select(p => p.Key).ToArray()) endpoints.Remove(ip);
        return endpoints.Keys.ToArray();
    }
}
