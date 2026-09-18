using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
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

static async Task<GatewayJournal> ReadJournalAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return await JsonSerializer.DeserializeAsync<GatewayJournal>(stream, ConfigurationStore.Json)
        ?? throw new InvalidDataException("Gateway journal was empty");
}

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
var temp = Path.Combine(Path.GetTempPath(), "eth-linux-privileged-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var journalPath = Path.Combine(temp, "gateway-journal.json");

async Task<CommandResult> ClientPingAsync(int count, int timeoutSeconds) =>
    await runner.RunAsync("ip",
        ["netns", "exec", clientNamespace, "ping", "-c", count.ToString(), "-W", timeoutSeconds.ToString(), physicalGateway],
        CancellationToken.None);

try
{
    var controlPing = await ClientPingAsync(1, 1);
    Check(controlPing.ExitCode != 0, "Control packet unexpectedly succeeded before gateway activation");

    // Normal lifecycle: Start -> real nft/sysctl packet flow -> Stop -> full rollback.
    var normalDns = new FakeDnsRuntime();
    var normal = new GatewayBootstrapper(nat, normalDns, journalPath);
    await normal.StartAsync(physical, overlay, CancellationToken.None);
    Check(normal.State == GatewayServerState.Ready, "Gateway bootstrapper did not reach Ready");
    Check(normalDns.Active, "DNS runtime was not started");
    Check(File.Exists(journalPath), "Gateway journal was not persisted while active");
    var normalJournal = await ReadJournalAsync(journalPath);
    Check(await nat.VerifyAsync(normalJournal.Snapshot, CancellationToken.None), "Linux NAT verification failed after bootstrapper start");
    Check((await ClientPingAsync(2, 2)).ExitCode == 0, "Forwarded client packet did not traverse the gateway NAT path");

    await normal.StopAsync(CancellationToken.None);
    Check(normal.State == GatewayServerState.Stopped, "Gateway bootstrapper did not stop cleanly");
    Check(!normalDns.Active, "DNS runtime was not stopped");
    Check(!File.Exists(journalPath), "Gateway journal remained after normal stop");
    Check(!await nat.VerifyAsync(normalJournal.Snapshot, CancellationToken.None), "NAT resources remained after normal stop");
    var restoredForwarding = (await runner.CheckedAsync("sysctl", ["-n", "net.ipv4.ip_forward"], CancellationToken.None)).Trim();
    Check(restoredForwarding == (normalJournal.Snapshot.GlobalForwardingEnabled ? "1" : "0"), "IPv4 forwarding state was not restored after normal stop");
    Check((await ClientPingAsync(1, 1)).ExitCode != 0, "Client packet still traversed after normal rollback");

    // Crash-recovery lifecycle: leave the first coordinator active, then let a fresh coordinator
    // recover only from the durable journal, matching a new Host process after power/process loss.
    var crashedDns = new FakeDnsRuntime();
    var crashed = new GatewayBootstrapper(nat, crashedDns, journalPath);
    await crashed.StartAsync(physical, overlay, CancellationToken.None);
    Check(crashed.State == GatewayServerState.Ready && File.Exists(journalPath), "Crash-recovery setup did not become active");
    var crashJournal = await ReadJournalAsync(journalPath);
    Check((await ClientPingAsync(2, 2)).ExitCode == 0, "Crash-recovery setup did not forward traffic");

    var recoveryDns = new FakeDnsRuntime();
    var recovery = new GatewayBootstrapper(nat, recoveryDns, journalPath);
    await recovery.RecoverAsync(CancellationToken.None);
    Check(recovery.State == GatewayServerState.Stopped, "Fresh bootstrapper did not finish journal recovery");
    Check(!File.Exists(journalPath), "Gateway journal remained after crash recovery");
    Check(!await nat.VerifyAsync(crashJournal.Snapshot, CancellationToken.None), "NAT resources remained after crash recovery");
    restoredForwarding = (await runner.CheckedAsync("sysctl", ["-n", "net.ipv4.ip_forward"], CancellationToken.None)).Trim();
    Check(restoredForwarding == (crashJournal.Snapshot.GlobalForwardingEnabled ? "1" : "0"), "IPv4 forwarding state was not restored after crash recovery");
    Check((await ClientPingAsync(1, 1)).ExitCode != 0, "Client packet still traversed after crash recovery");
    await crashedDns.DisposeAsync();
}
finally
{
    if (File.Exists(journalPath)) File.Delete(journalPath);
    Directory.Delete(temp, recursive: true);
}

Console.WriteLine("PASS privileged Linux route, forwarding, nftables NAT, packet-flow and journal recovery test");
return 0;

sealed class FakeDnsRuntime : IGatewayDnsRuntime
{
    public bool Active { get; private set; }
    public Task StartAsync(CancellationToken ct) { Active = true; return Task.CompletedTask; }
    public Task<bool> CheckAsync(CancellationToken ct) => Task.FromResult(Active);
    public Task StopAsync(CancellationToken ct) { Active = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Active = false; return ValueTask.CompletedTask; }
}
