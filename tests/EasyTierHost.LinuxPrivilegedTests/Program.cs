using System.Net;
using System.Net.NetworkInformation;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;
using EasyTierHost.Network;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static NetworkInterface GetNic(string name) =>
    NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name)
    ?? throw new InvalidOperationException($"Network interface not found: {name}");

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP Linux privileged network test");
    return 0;
}
if (args.Length != 4)
    throw new ArgumentException("Usage: <physical-if> <overlay-if> <client-netns> <physical-gateway>");

var physicalName = args[0];
var overlayName = args[1];
var clientNamespace = args[2];
var physicalGateway = args[3];
if (!IPAddress.TryParse(physicalGateway, out var gatewayIp) || gatewayIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
    throw new ArgumentException("Physical gateway must be IPv4");

var physicalNic = GetNic(physicalName);
var overlayNic = GetNic(overlayName);
var physicalIndex = physicalNic.GetIPProperties().GetIPv4Properties()?.Index
    ?? throw new InvalidOperationException("Physical interface has no IPv4 index");
var overlayIndex = overlayNic.GetIPProperties().GetIPv4Properties()?.Index
    ?? throw new InvalidOperationException("Overlay interface has no IPv4 index");
var physicalIpv4 = physicalNic.GetIPProperties().UnicastAddresses
    .Select(x => x.Address)
    .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(ip))
    ?? throw new InvalidOperationException("Physical interface has no usable IPv4 address");
var overlayIpv4 = overlayNic.GetIPProperties().UnicastAddresses
    .Select(x => x.Address)
    .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.ToString() == OverlayAddressPlan.Gateway)
    ?? throw new InvalidOperationException("Gateway overlay address is missing");

var runner = new CommandRunner();
var routeApi = new LinuxRouteApi(runner);
var initialRoutes = await routeApi.ListAsync(CancellationToken.None);
Check(initialRoutes.Any(r => r.Destination == "0.0.0.0/0" && r.InterfaceIndex == physicalIndex && r.NextHop == physicalGateway),
    "Expected namespace default route is missing");

var testRoute = new RouteEntry("203.0.113.10/32", physicalGateway, physicalIndex, 7);
var routeAdded = false;
try
{
    await routeApi.AddAsync(testRoute, CancellationToken.None);
    routeAdded = true;
    var afterAdd = await routeApi.ListAsync(CancellationToken.None);
    Check(afterAdd.Any(r => r.Destination == testRoute.Destination && r.InterfaceIndex == physicalIndex && r.NextHop == physicalGateway && r.Metric == 7),
        "LinuxRouteApi did not install the owned /32 route");
}
finally
{
    if (routeAdded) await routeApi.DeleteAsync(testRoute, CancellationToken.None);
}
var afterDelete = await routeApi.ListAsync(CancellationToken.None);
Check(!afterDelete.Any(r => r.Destination == testRoute.Destination && r.InterfaceIndex == physicalIndex && r.NextHop == physicalGateway),
    "LinuxRouteApi did not remove the owned /32 route");

var physical = new RouteSnapshot(
    physicalName,
    physicalIndex,
    physicalIpv4.ToString(),
    physicalGateway,
    0,
    initialRoutes.Where(r => r.Destination == "0.0.0.0/0").ToArray(),
    initialRoutes.Where(r => r.NextHop == "0.0.0.0").ToArray(),
    []);
var overlay = new GatewayAdapter(overlayIndex, overlayName, overlayNic.Id, overlayIpv4.ToString());
var nat = new LinuxNatManager(runner);
var platform = await nat.CaptureAsync(physical, overlay, CancellationToken.None);
var expectedForwarding = platform.GlobalForwardingEnabled ? "1" : "0";
var natCreated = false;
try
{
    await nat.EnableForwardingAsync(platform, CancellationToken.None);

    var controlPing = await runner.RunAsync("ip",
        ["netns", "exec", clientNamespace, "ping", "-c", "1", "-W", "1", physicalGateway],
        CancellationToken.None);
    Check(controlPing.ExitCode != 0, "Control packet unexpectedly succeeded before NAT was installed");

    await nat.CreateNatAsync(platform, CancellationToken.None);
    natCreated = true;
    Check(await nat.VerifyAsync(platform, CancellationToken.None), "Linux NAT verification failed after creation");

    var forwardedPing = await runner.RunAsync("ip",
        ["netns", "exec", clientNamespace, "ping", "-c", "2", "-W", "2", physicalGateway],
        CancellationToken.None);
    Check(forwardedPing.ExitCode == 0, "Forwarded client packet did not traverse the gateway NAT path");
}
finally
{
    if (natCreated) await nat.RemoveNatAsync(platform, CancellationToken.None);
    await nat.RestoreForwardingAsync(platform, CancellationToken.None);
}

var restoredForwarding = (await runner.CheckedAsync("sysctl", ["-n", "net.ipv4.ip_forward"], CancellationToken.None)).Trim();
Check(restoredForwarding == expectedForwarding, "IPv4 forwarding state was not restored");
Check(!await nat.VerifyAsync(platform, CancellationToken.None), "NAT resources remained after cleanup");

Console.WriteLine("PASS privileged Linux route, forwarding, nftables NAT and packet-flow test");
return 0;
