using System.Net;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Network;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Fails(Func<Task> action)
{
    try { await action(); }
    catch { return; }
    throw new Exception("Expected failure");
}

var profile = new NetworkProfile
{
    NetworkName = "integration",
    SecretFile = "integration.secret",
    Role = NodeRole.Client,
    SeedPhysicalIp = "192.0.2.10",
    RpcPort = 15888,
    EnableInternetGateway = true
};
var args = EasyTierArgumentBuilder.Build(profile, "C:/private/core.toml", new CoreLaunchOptions { UnderlaySourceIpv4 = "192.168.1.9" }).ToArray();
Check(args.Contains("--underlay-source-ipv4"), "Underlay argument missing");
Check(args[^1] == "192.168.1.9", "Underlay source was not preserved");
await Fails(() => Task.Run(() => EasyTierArgumentBuilder.Build(profile, "C:/private/core.toml", new CoreLaunchOptions { UnderlaySourceIpv4 = "10.10.0.11" })));

var dir = Path.Combine(Path.GetTempPath(), "eth-integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var api = new FakeApi();
    var dns = new FakeDns();
    var probe = new FakeProbe();
    var journal = Path.Combine(dir, "route-journal.json");
    var controller = new GatewayRouteController(api, dns, probe, journal);
    var context = new GatewayContext(20, "10.10.0.11", [IPAddress.Parse("192.0.2.10")], true);

    await controller.ActivateAsync(context);
    Check(controller.State == GatewayState.GatewayActive, "Gateway did not activate");
    Check(api.Routes.Any(r => r.Destination == "192.0.2.10/32"), "Initial endpoint protection missing");
    Check(api.Routes.Any(r => r.Destination == "0.0.0.0/1") && api.Routes.Any(r => r.Destination == "128.0.0.0/1"), "Split default routes missing");

    await controller.UpdateProtectionAsync([IPAddress.Parse("203.0.113.20")]);
    Check(api.Routes.Any(r => r.Destination == "203.0.113.20/32"), "New endpoint protection missing");
    Check(!api.Routes.Any(r => r.Destination == "192.0.2.10/32"), "Stale endpoint protection was not removed");
    Check(api.Routes.Any(r => r.Destination == "192.168.1.1/32"), "Physical gateway protection was removed");
    Check(controller.State == GatewayState.GatewayActive, "Dynamic endpoint update changed active state");

    await Fails(() => controller.UpdateProtectionAsync([IPAddress.Parse("1.1.1.1")]));
    Check(controller.State == GatewayState.PhysicalOnly, "Probe collision did not roll back");
    Check(api.Routes.SequenceEqual(api.Original), "Rollback did not restore original routes");
    Check(!dns.Active, "DNS was not restored after rollback");
    Check(!File.Exists(journal), "Route journal remained after successful rollback");
}
finally
{
    Directory.Delete(dir, true);
}

Console.WriteLine("PASS underlay/client route integration smoke tests");
return 0;

sealed class FakeApi : IRouteApi
{
    public List<RouteEntry> Original { get; } = [new("0.0.0.0/0", "192.168.1.1", 2, 50) { CreatedByEasyTierHost = false, OwnerTag = "OS" }];
    public List<RouteEntry> Routes { get; }
    public FakeApi() => Routes = Original.ToList();
    public Task<RouteSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(new RouteSnapshot("Ethernet", 2, "192.168.1.9", "192.168.1.1", 25, Original, [], ["192.168.1.1"]));
    public Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RouteEntry>>(Routes.ToArray());
    public Task AddAsync(RouteEntry route, CancellationToken ct) { Routes.Add(route); return Task.CompletedTask; }
    public Task DeleteAsync(RouteEntry route, CancellationToken ct) { Routes.RemoveAll(r => RoutePlanner.SameIdentity(r, route)); return Task.CompletedTask; }
}

sealed class FakeDns : IDnsController
{
    public bool Active { get; private set; }
    public Task ApplyAsync(RouteSnapshot snapshot, CancellationToken ct) { Active = true; return Task.CompletedTask; }
    public Task RestoreAsync(RouteSnapshot snapshot, CancellationToken ct) { Active = false; return Task.CompletedTask; }
}

sealed class FakeProbe : IGatewayProbe
{
    public Task<bool> CheckAsync(GatewayContext context, CancellationToken ct) => Task.FromResult(true);
}
