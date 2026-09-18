using System.Net;
using System.Net.NetworkInformation;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Network;

namespace EasyTierHost.Service;

public sealed record ClientOverlayAdapter(int InterfaceIndex, string Name, string Identity, string Ip);

public sealed class ClientInternetCoordinator(IRouteApi routes, GatewayRouteController controller, IGatewayProbe probe, string stateDirectory)
{
    private string StatusPath => Path.Combine(stateDirectory, "client-gateway-status.json");

    public async Task RecoverAsync(CancellationToken ct)
    {
        await controller.RecoverAsync(ct);
        // Clear a stale GatewayActive marker before a new Core/TUN instance starts. The Windows
        // client UI uses this file as an observer only and must never mistake a previous run for
        // the current route transaction.
        await ConfigurationStore.SaveAtomicAsync(StatusPath, new
        {
            State = controller.State.ToString(),
            UpdatedUtc = DateTimeOffset.UtcNow
        }, ct);
    }

    public static ClientOverlayAdapter? FindAdapter(NetworkProfile profile)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Name == profile.DeviceName && n.OperationalStatus == OperationalStatus.Up))
        {
            var ipv4 = nic.GetIPProperties().GetIPv4Properties();
            if (ipv4 is null) continue;
            var address = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.PrefixLength == 16 && OverlayAddressPlan.IsClient(a.Address));
            if (address is not null) return new(ipv4.Index, nic.Name, nic.Id, address.Address.ToString());
        }
        return null;
    }

    private static bool SamePhysical(RouteSnapshot left, RouteSnapshot right) =>
        left.PhysicalInterfaceIndex == right.PhysicalInterfaceIndex
        && left.PhysicalIpv4 == right.PhysicalIpv4
        && left.PhysicalGateway == right.PhysicalGateway;

    private static IReadOnlyCollection<IPAddress> ActiveEndpoints(NetworkProfile profile, IEnumerable<CorePeerStatus> peers)
    {
        var result = new HashSet<IPAddress>();
        if (IPAddress.TryParse(profile.SeedPhysicalIp, out var seed)) result.Add(seed);
        foreach (var endpoint in peers.SelectMany(p => p.Endpoints)) result.Add(endpoint);
        return result.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !OverlayAddressPlan.IsOverlay(ip) && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224).ToArray();
    }

    public async Task RunAsync(NetworkProfile profile, IEasyTierProcessManager process, Guid instanceId, RouteSnapshot launchPhysical, CancellationToken ct)
    {
        if (profile.Role != NodeRole.Client || !profile.EnableInternetGateway) throw new HostException("ETH301", "Internet coordinator requires an enabled Client profile");
        if (process.UnderlaySourceIpv4 != launchPhysical.PhysicalIpv4) throw new HostException("ETH301", "Core underlay binding does not match the captured physical IPv4");

        await ConfigurationStore.SaveAtomicAsync(StatusPath, new
        {
            State = "Starting",
            Physical = launchPhysical.PhysicalInterfaceName,
            PhysicalIpv4 = launchPhysical.PhysicalIpv4,
            UpdatedUtc = DateTimeOffset.UtcNow
        }, ct);

        var endpointProtector = new UnderlayRouteProtector { GracePeriod = TimeSpan.FromSeconds(120) };
        ClientOverlayAdapter? overlay = null;
        CoreNodeStatus? node = null;
        IReadOnlyList<CorePeerStatus> peers = [];
        var reader = new CoreStatusReader(new CommandRunner(), profile);
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            ready.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                while (true)
                {
                    if (!process.IsRunning) throw new HostException("ETH201", "Core exited before client TUN was ready");
                    var currentPhysical = await routes.CaptureAsync(ready.Token);
                    if (!SamePhysical(currentPhysical, launchPhysical)) throw new HostException("ETH301", "Physical network changed before gateway activation");
                    overlay = FindAdapter(profile);
                    if (overlay is not null)
                    {
                        try
                        {
                            node = await reader.ReadNodeAsync(ready.Token);
                            if (node.InstanceId == instanceId && node.OverlayIp == overlay.Ip && IPAddress.TryParse(node.OverlayIp, out var overlayIp) && OverlayAddressPlan.IsClient(overlayIp))
                            {
                                peers = await reader.ReadPeersAsync(ready.Token);
                                if (peers.Count > 0) break;
                            }
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.Text.Json.JsonException)
                        { ready.Token.ThrowIfCancellationRequested(); }
                    }
                    await Task.Delay(250, ready.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HostException("ETH201", "Client TUN readiness timed out"); }
        }

        var active = ActiveEndpoints(profile, peers);
        var protectedEndpoints = endpointProtector.Update(active);
        var context = new GatewayContext(overlay!.InterfaceIndex, overlay.Ip, protectedEndpoints.ToArray(), process.UnderlaySourceIpv4 == launchPhysical.PhysicalIpv4);
        try
        {
            await controller.ActivateAsync(context, ct);
            await ConfigurationStore.SaveAtomicAsync(StatusPath, new
            {
                State = "GatewayActive",
                Physical = launchPhysical.PhysicalInterfaceName,
                PhysicalIpv4 = launchPhysical.PhysicalIpv4,
                Overlay = overlay.Name,
                OverlayIp = overlay.Ip,
                UpdatedUtc = DateTimeOffset.UtcNow
            }, ct);
            Console.WriteLine("GatewayActive: client IPv4 default route and DNS committed");

            var lastProbe = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (!process.IsRunning) throw new HostException("ETH201", "Core stopped while Internet gateway was active");
                if (process.UnderlaySourceIpv4 != launchPhysical.PhysicalIpv4) throw new HostException("ETH301", "Core underlay binding changed");
                var currentPhysical = await routes.CaptureAsync(ct);
                if (!SamePhysical(currentPhysical, launchPhysical))
                {
                    await controller.RollbackAsync(CancellationToken.None);
                    throw new HostException("ETH301", "Physical network changed; gateway routes rolled back for Core restart");
                }
                var currentOverlay = FindAdapter(profile);
                if (currentOverlay is null || currentOverlay.Identity != overlay.Identity || currentOverlay.Ip != overlay.Ip)
                {
                    await controller.RollbackAsync(CancellationToken.None);
                    throw new HostException("ETH201", "Overlay interface changed; gateway routes rolled back");
                }
                node = await reader.ReadNodeAsync(ct);
                if (node.InstanceId != instanceId || node.OverlayIp != overlay.Ip) throw new HostException("ETH201", "Client Core instance changed");
                peers = await reader.ReadPeersAsync(ct);
                if (peers.Any(peer => peer.PeerId != node.PeerId && peer.OverlayIp == overlay.Ip)) throw new HostException("ETH102", "Duplicate client address detected");

                protectedEndpoints = endpointProtector.Update(ActiveEndpoints(profile, peers));
                await controller.UpdateProtectionAsync(protectedEndpoints, ct);
                await controller.ReconcileAsync(ct);
                if (controller.State != GatewayState.GatewayActive) throw new HostException("ETH301", "Gateway route ownership changed; network rolled back");

                if (DateTimeOffset.UtcNow - lastProbe >= TimeSpan.FromSeconds(30))
                {
                    var healthContext = new GatewayContext(overlay.InterfaceIndex, overlay.Ip, protectedEndpoints.ToArray(), true);
                    if (!await probe.CheckAsync(healthContext, ct)) throw new HostException("ETH203", "Active Internet gateway health probe failed");
                    lastProbe = DateTimeOffset.UtcNow;
                }
            }
        }
        finally
        {
            await controller.RollbackAsync(CancellationToken.None);
            await ConfigurationStore.SaveAtomicAsync(StatusPath, new { State = controller.State.ToString(), UpdatedUtc = DateTimeOffset.UtcNow });
        }
    }
}
