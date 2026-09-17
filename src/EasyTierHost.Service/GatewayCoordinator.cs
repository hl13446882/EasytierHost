using System.Net;
using System.Net.NetworkInformation;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;

namespace EasyTierHost.Service;

public sealed class GatewayCoordinator(IRouteApi routes, IGatewayPlatform platform, string stateDirectory)
{
    private string JournalPath => Path.Combine(stateDirectory, "gateway-journal.json");
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var dns = new GatewayDnsRuntime(new(IPAddress.Parse(OverlayAddressPlan.Gateway), 53), []);
        await new GatewayBootstrapper(platform, dns, JournalPath).RecoverAsync(ct);
    }
    public static GatewayAdapter? FindAdapter(NetworkProfile profile)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Name == profile.DeviceName && n.OperationalStatus == OperationalStatus.Up).ToArray();
        foreach (var nic in nics)
        {
            var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.ToString() == OverlayAddressPlan.Gateway && a.PrefixLength == 16);
            if (ip is not null) return new(nic.GetIPProperties().GetIPv4Properties().Index, nic.Name, nic.Id, ip.Address.ToString());
        }
        return null;
    }
    public static IPEndPoint[] ResolveUpstreams(NetworkProfile profile, RouteSnapshot physical)
    {
        var addresses = profile.DnsUpstreams.Length > 0 ? profile.DnsUpstreams : physical.OriginalDnsServers.ToArray();
        var upstreams = addresses.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .Where(ip => ip is not null && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(ip) && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224)
            .Select(ip => new IPEndPoint(ip!, 53)).Distinct().ToList();
        if (profile.AllowPublicDnsFallback)
            foreach (var ip in new[] { "1.1.1.1", "8.8.8.8" }) { var endpoint = new IPEndPoint(IPAddress.Parse(ip), 53); if (!upstreams.Contains(endpoint)) upstreams.Add(endpoint); }
        if (upstreams.Count == 0) throw new HostException("ETH202", "No physical DNS upstream configured");
        return upstreams.ToArray();
    }
    public async Task RunAsync(NetworkProfile profile, IEasyTierProcessManager process, CancellationToken ct)
    {
        GatewayAdapter? overlay = null;
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            ready.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                while ((overlay = FindAdapter(profile)) is null)
                {
                    if (!process.IsRunning) throw new HostException("ETH201", "Core exited before gateway TUN was ready");
                    await Task.Delay(250, ready.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HostException("ETH201", "Gateway TUN readiness timed out"); }
        }
        var physical = await routes.CaptureAsync(ct);
        await using var dns = new GatewayDnsRuntime(new(IPAddress.Parse(OverlayAddressPlan.Gateway), 53), ResolveUpstreams(profile, physical));
        var bootstrapper = new GatewayBootstrapper(platform, dns, JournalPath);
        try
        {
            await bootstrapper.StartAsync(physical, overlay!, ct);
            await ConfigurationStore.SaveAtomicAsync(Path.Combine(stateDirectory, "gateway-status.json"), new { State = "GatewayReady", Physical = physical.PhysicalInterfaceName, Overlay = overlay!.Name, UpdatedUtc = DateTimeOffset.UtcNow }, ct);
            Console.WriteLine("GatewayReady: forwarding, NAT and UDP/TCP DNS verified");
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                var current = await routes.CaptureAsync(ct);
                if (!process.IsRunning || FindAdapter(profile)?.Identity != overlay.Identity || current.PhysicalInterfaceIndex != physical.PhysicalInterfaceIndex || current.PhysicalIpv4 != physical.PhysicalIpv4 || current.PhysicalGateway != physical.PhysicalGateway)
                    throw new HostException("ETH201", "Gateway network changed; rebuilding gateway");
                if (!await bootstrapper.CheckAsync(ct)) throw new HostException("ETH202", "Gateway health check failed");
            }
        }
        finally
        {
            try { await bootstrapper.StopAsync(); }
            finally { await ConfigurationStore.SaveAtomicAsync(Path.Combine(stateDirectory, "gateway-status.json"), new { State = bootstrapper.State.ToString(), UpdatedUtc = DateTimeOffset.UtcNow }); }
        }
    }
}
