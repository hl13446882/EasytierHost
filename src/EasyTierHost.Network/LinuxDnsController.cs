using System.Net.NetworkInformation;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

public sealed class LinuxDnsController(ICommandRunner runner, Func<int, string?>? interfaceIdentity = null) : IDnsController
{
    private string? Identity(int index) => interfaceIdentity is not null ? interfaceIdentity(index) : NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == index)?.Id;
    public async Task<RouteSnapshot> CaptureAsync(RouteSnapshot snapshot, GatewayContext context, CancellationToken ct)
    {
        var identity = Identity(context.OverlayInterfaceIndex) ?? throw new HostException("ETH202", "Overlay interface missing");
        var state = await new ResolvedLink(runner).ReadAsync(context.OverlayInterfaceIndex, ct);
        // This first implementation owns an otherwise unused TUN resolver only. It never overwrites another VPN's DNS.
        if (state.Servers.Length != 0 || state.Domains.Length != 0) throw new HostException("ETH202", "Overlay already has DNS configuration");
        return snapshot with { DnsState = new("linux-resolved", context.OverlayInterfaceIndex, identity, state.Servers, state.Domains, state.DefaultRoute, Guid.NewGuid().ToString("N")) };
    }
    private static DnsClientSnapshot State(RouteSnapshot snapshot) => snapshot.DnsState is { Platform: "linux-resolved" } state ? state : throw new HostException("ETH302", "Missing resolved DNS snapshot");
    public async Task ApplyAsync(RouteSnapshot snapshot, CancellationToken ct)
    {
        var s = State(snapshot); if (Identity(s.InterfaceIndex) != s.InterfaceIdentity) throw new HostException("ETH202", "DNS interface changed");
        var existing = await new ResolvedLink(runner).ReadAsync(s.InterfaceIndex, ct);
        if (existing.Servers.Length != 0 || existing.Domains.Length != 0) throw new HostException("ETH202", "DNS configuration changed before activation");
        var index = s.InterfaceIndex.ToString();
        await runner.CheckedAsync("resolvectl", ["dns", index, OverlayAddressPlan.Gateway], ct);
        await runner.CheckedAsync("resolvectl", ["domain", index, "~."], ct);
        await runner.CheckedAsync("resolvectl", ["default-route", index, "yes"], ct);
        await runner.CheckedAsync("resolvectl", ["flush-caches"], ct);
    }
    public async Task RestoreAsync(RouteSnapshot snapshot, CancellationToken ct)
    {
        var s = State(snapshot); var identity = Identity(s.InterfaceIndex);
        if (identity is null) return; // A removed TUN has no remaining per-link resolver state.
        if (identity != s.InterfaceIdentity) throw new HostException("ETH302", "DNS interface index reused");
        var current = await new ResolvedLink(runner).ReadAsync(s.InterfaceIndex, ct);
        if (current.Servers.Any(ip => ip != OverlayAddressPlan.Gateway) || current.Domains.Any(d => d.Name != "." || !d.RoutingOnly)) throw new HostException("ETH302", "DNS configuration changed externally; refusing overwrite");
        var index = s.InterfaceIndex.ToString();
        await runner.CheckedAsync("resolvectl", ["domain", index, ""], ct);
        await runner.CheckedAsync("resolvectl", ["dns", index, ""], ct);
        await runner.CheckedAsync("resolvectl", ["default-route", index, s.DefaultRoute ? "yes" : "no"], ct);
        await runner.CheckedAsync("resolvectl", ["flush-caches"], ct);
    }
}
