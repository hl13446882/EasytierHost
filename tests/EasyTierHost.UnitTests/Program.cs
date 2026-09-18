using System.Net;
using System.Net.Sockets;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;
using EasyTierHost.Network;

var tests = new (string Name, Func<Task> Test)[]
{
    ("Role configuration", Roles),
    ("Reject reserved client IPs", AddressPool),
    ("Role-aware node readiness", Readiness),
    ("Reject invalid profiles", InvalidProfiles),
    ("Gateway commit and exact rollback", CommitRollback),
    ("Probe failure restores network", ProbeFailure),
    ("DNS partial failure restores DNS and routes", DnsFailure),
    ("Route mutation failure is recovered", RouteFailure),
    ("Crash journal recovery is idempotent", CrashRecovery),
    ("Foreign routes are not adopted or removed", ForeignRoutes),
    ("Rollback failure retains recovery journal", RollbackFailure),
    ("Underlay verification required", UnderlayRequired),
    ("Interface changes roll back gateway", InterfaceChange),
    ("Concurrent activate is serialized", ConcurrentActivate),
    ("Endpoint grace period", EndpointGrace),
    ("Linux host route identity normalization", LinuxRouteIdentity),
    ("DNS forwarding over real UDP and TCP sockets", DnsForwarding),
    ("Protected secret round trip", SecretRoundTrip),
};
var failures = 0;
var allTests = tests.Concat(GatewayTests.Cases).Concat(NetworkExtensionTests.Cases).ToArray();
foreach (var (name, test) in allTests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{allTests.Length - failures}/{allTests.Length} passed");
return failures == 0 ? 0 : 1;

static void Check(bool ok, string reason = "Assertion failed") { if (!ok) throw new Exception(reason); }
static async Task Throws(Func<Task> task) { try { await task(); } catch { return; } throw new Exception("Expected exception"); }
static NetworkProfile Profile(NodeRole role) => new() { NetworkName = "company\"overlay", SecretFile = "network.secret", Role = role, SeedPhysicalIp = "192.0.2.10", DedicatedIndex = role == NodeRole.Dedicated ? 3 : null };
static Task Roles()
{
    foreach (var role in Enum.GetValues<NodeRole>())
    {
        var config = EasyTierConfigBuilder.Build(Profile(role), "secret\"\\value");
        Check(config.Contains("network_name = \"company\\\"overlay\""));
        if (role == NodeRole.Seed) Check(!config.Contains("ipv4 =") && !config.Contains("exit_nodes") && config.Contains("no_tun = true"));
        if (role == NodeRole.Gateway) Check(config.Contains("ipv4 = \"10.10.0.1/16\"") && config.Contains("enable_exit_node = true"));
        if (role == NodeRole.Dedicated) Check(config.Contains("ipv4 = \"10.10.0.3/16\"") && config.Contains("enable_exit_node = false"));
        if (role == NodeRole.Client) Check(config.Contains("[dhcp_range]") && config.Contains("dhcp = true") && config.Contains("10.10.0.11"));
    }
    Check(EasyTierConfigBuilder.Build(Profile(NodeRole.Seed) with { NetworkName = "测试🌐" }, "secret").Contains("测试🌐"));
    return Task.CompletedTask;
}
static Task AddressPool()
{
    foreach (var ip in new[] { "10.10.0.0", "10.10.0.1", "10.10.0.10", "10.10.255.255", "10.11.0.11", "::1" }) Check(!OverlayAddressPlan.IsClient(IPAddress.Parse(ip)));
    foreach (var ip in new[] { "10.10.0.11", "10.10.1.0", "10.10.255.254" }) Check(OverlayAddressPlan.IsClient(IPAddress.Parse(ip)));
    return Task.CompletedTask;
}
static Task Readiness()
{
    static IPAddress Ip(string value) => IPAddress.Parse(value);
    Check(NodeReadiness.Evaluate(Profile(NodeRole.Seed), []).Ready, "Seed without TUN should be ready");
    Check(!NodeReadiness.Evaluate(Profile(NodeRole.Seed), [Ip("10.10.0.11")]).Ready, "Seed owning overlay IP was accepted");

    Check(NodeReadiness.Evaluate(Profile(NodeRole.Gateway), [Ip("10.10.0.1")]).Ready, "Gateway .1 was rejected");
    Check(!NodeReadiness.Evaluate(Profile(NodeRole.Gateway), [Ip("10.10.0.2")]).Ready, "Gateway with wrong IP was accepted");

    Check(NodeReadiness.Evaluate(Profile(NodeRole.Dedicated), [Ip("10.10.0.3")]).Ready, "Dedicated expected IP was rejected");
    Check(!NodeReadiness.Evaluate(Profile(NodeRole.Dedicated), [Ip("10.10.0.4")]).Ready, "Dedicated wrong IP was accepted");

    Check(NodeReadiness.Evaluate(Profile(NodeRole.Client), [Ip("10.10.0.11")]).Ready, "Client DHCP start was rejected");
    Check(NodeReadiness.Evaluate(Profile(NodeRole.Client), [Ip("10.10.255.254")]).Ready, "Client DHCP end was rejected");
    Check(!NodeReadiness.Evaluate(Profile(NodeRole.Client), [Ip("10.10.0.2")]).Ready, "Client reserved IP was accepted");
    Check(!NodeReadiness.Evaluate(Profile(NodeRole.Client), [Ip("10.10.0.11"), Ip("10.10.0.12")]).Ready, "Multiple overlay IPs were accepted");
    return Task.CompletedTask;
}
static async Task InvalidProfiles()
{
    foreach (var profile in new[] { Profile(NodeRole.Dedicated) with { DedicatedIndex = 1 }, Profile(NodeRole.Client) with { SeedPhysicalIp = "10.10.0.1" }, Profile(NodeRole.Client) with { DeviceName = "a';rm" }, Profile(NodeRole.Gateway) with { DnsUpstreams = ["10.10.0.1"] }, Profile(NodeRole.Client) with { Role = (NodeRole)99 } })
        await Throws(() => { NetworkProfileValidator.Validate(profile); return Task.CompletedTask; });
}
static GatewayContext Context(bool protect = true) => new(20, "10.10.0.11", [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("203.0.113.2")], protect);
static async Task CommitRollback()
{
    using var f = new Fixture(); await f.Controller.ActivateAsync(Context());
    Check(f.Controller.State == GatewayState.GatewayActive && f.Dns.Active);
    Check(f.Api.Routes.Any(r => r.Destination == "0.0.0.0/1"));
    Check(!f.Api.Routes.Any(r => r.Destination is "1.1.1.1/32" or "8.8.8.8/32"));
    await f.Controller.RollbackAsync(); Check(f.Api.Routes.SequenceEqual(f.Original) && !f.Dns.Active && !File.Exists(f.Journal));
    await f.Controller.RollbackAsync();
}
static async Task ProbeFailure()
{
    using var f = new Fixture(); f.Probe.Success = false;
    await Throws(() => f.Controller.ActivateAsync(Context())); Check(f.Api.Routes.SequenceEqual(f.Original)); Check(f.Controller.State == GatewayState.PhysicalOnly);
}
static async Task DnsFailure()
{
    using var f = new Fixture(); f.Dns.FailApply = true;
    await Throws(() => f.Controller.ActivateAsync(Context())); Check(!f.Dns.Active && f.Api.Routes.SequenceEqual(f.Original));
}
static async Task RouteFailure()
{
    using var f = new Fixture(); f.Api.FailAddDestination = "128.0.0.0/1";
    await Throws(() => f.Controller.ActivateAsync(Context())); Check(f.Api.Routes.SequenceEqual(f.Original) && !f.Dns.Active);
}
static async Task CrashRecovery()
{
    using var f = new Fixture(); await f.Controller.ActivateAsync(Context());
    var restarted = new GatewayRouteController(f.Api, f.Dns, f.Probe, f.Journal);
    await restarted.RecoverAsync(); await restarted.RecoverAsync(); Check(f.Api.Routes.SequenceEqual(f.Original) && !f.Dns.Active);
}
static async Task ForeignRoutes()
{
    using var f = new Fixture(); var other = new RouteEntry("192.0.2.10/32", "192.168.1.1", 2, 20) { CreatedByEasyTierHost = false, OwnerTag = "OtherVPN" }; f.Api.Routes.Add(other);
    await Throws(() => f.Controller.ActivateAsync(Context())); Check(f.Api.Routes.Contains(other)); Check(f.Api.Routes.Count == f.Original.Count + 1);
}
static async Task RollbackFailure()
{
    using var f = new Fixture(); await f.Controller.ActivateAsync(Context()); f.Api.FailDelete = true;
    await Throws(() => f.Controller.RollbackAsync()); Check(f.Controller.State == GatewayState.Faulted && File.Exists(f.Journal));
    f.Api.FailDelete = false; await f.Controller.RollbackAsync(); Check(f.Api.Routes.SequenceEqual(f.Original));
}
static async Task UnderlayRequired()
{
    using var f = new Fixture(); await Throws(() => f.Controller.ActivateAsync(Context(false))); Check(f.Api.Routes.SequenceEqual(f.Original)); Check(!File.Exists(f.Journal));
}
static async Task InterfaceChange()
{
    using var f = new Fixture(); await f.Controller.ActivateAsync(Context()); f.Api.Snapshot = f.Api.Snapshot with { PhysicalInterfaceIndex = 3 };
    await f.Controller.ReconcileAsync(); Check(f.Controller.State == GatewayState.PhysicalOnly && f.Api.Routes.SequenceEqual(f.Original));
}
static async Task ConcurrentActivate()
{
    using var f = new Fixture(); await Task.WhenAll(f.Controller.ActivateAsync(Context()), f.Controller.ActivateAsync(Context()));
    Check(f.Api.Routes.Count(r => r.Destination == "0.0.0.0/1") == 1); await f.Controller.RollbackAsync();
}
static Task EndpointGrace()
{
    var clock = new FakeTime(); var cache = new UnderlayRouteProtector(clock); var endpoint = IPAddress.Parse("192.0.2.1");
    Check(cache.Update([endpoint]).Count == 1); clock.Now += TimeSpan.FromSeconds(119); Check(cache.Update([]).Count == 1);
    clock.Now += TimeSpan.FromSeconds(1); Check(cache.Update([]).Count == 0); return Task.CompletedTask;
}
static Task LinuxRouteIdentity()
{
    Check(LinuxRouteApi.NormalizeDestination("1.1.1.1") == "1.1.1.1/32");
    Check(LinuxRouteApi.NormalizeDestination("default") == "0.0.0.0/0");
    Check(LinuxRouteApi.NormalizeDestination("10.10.0.0/16") == "10.10.0.0/16");
    return Task.CompletedTask;
}
static async Task DnsForwarding()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var upstream = (IPEndPoint)udp.Client.LocalEndPoint!;
    var tcp = new TcpListener(upstream); tcp.Start();
    byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, (byte)'a', 0, 0, 1, 0, 1];
    var udpServer = Task.Run(async () => { var packet = await udp.ReceiveAsync(timeout.Token); packet.Buffer[2] |= 0x80; await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token); });
    var tcpServer = Task.Run(async () => { using var client = await tcp.AcceptTcpClientAsync(timeout.Token); var stream = client.GetStream(); byte[] frame = new byte[query.Length + 2]; await stream.ReadExactlyAsync(frame, timeout.Token); frame[4] |= 0x80; await stream.WriteAsync(frame, timeout.Token); });
    try
    {
        var forwarder = new DnsForwarderService(new(IPAddress.Parse("10.10.0.1"), 53), [upstream]);
        var u = await forwarder.ResolveAsync(query, false, timeout.Token); var t = await forwarder.ResolveAsync(query, true, timeout.Token);
        Check(u[0] == 0x12 && (u[2] & 0x80) != 0 && t.SequenceEqual(u)); await Task.WhenAll(udpServer, tcpServer);
    }
    finally { tcp.Stop(); }
}
static async Task SecretRoundTrip()
{
    var dir = Path.Combine(Path.GetTempPath(), "eth-secret-" + Guid.NewGuid().ToString("N"));
    await SecretProvider.SecureDirectoryAsync(dir);
    try { var path = Path.Combine(dir, "test.secret"); await SecretProvider.WriteAsync(path, "sensitive-value"); Check(await SecretProvider.ReadAsync(path) == "sensitive-value"); if (OperatingSystem.IsWindows()) Check(!System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("sensitive-value")); }
    finally { Directory.Delete(dir, true); }
}
sealed class FakeTime : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
sealed class FakeApi : IRouteApi
{
    public List<RouteEntry> Routes { get; } = [new("0.0.0.0/0", "192.168.1.1", 2, 50) { CreatedByEasyTierHost = false, OwnerTag = "OS" }];
    public RouteSnapshot Snapshot { get; set; } = new("Ethernet", 2, "192.168.1.9", "192.168.1.1", 25, [], [], ["192.168.1.1"]);
    public string? FailAddDestination; public bool FailDelete;
    public Task<RouteSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(Snapshot);
    public Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RouteEntry>>(Routes.ToArray());
    public Task AddAsync(RouteEntry r, CancellationToken ct) { if (r.Destination == FailAddDestination) throw new IOException("injected add failure"); Routes.Add(r); return Task.CompletedTask; }
    public Task DeleteAsync(RouteEntry r, CancellationToken ct) { if (FailDelete) throw new IOException("injected delete failure"); Routes.RemoveAll(x => RoutePlanner.SameIdentity(x, r)); return Task.CompletedTask; }
}
sealed class FakeDns : IDnsController
{
    public bool Active, FailApply;
    public Task ApplyAsync(RouteSnapshot s, CancellationToken ct) { Active = true; if (FailApply) throw new IOException("injected DNS failure"); return Task.CompletedTask; }
    public Task RestoreAsync(RouteSnapshot s, CancellationToken ct) { Active = false; return Task.CompletedTask; }
}
sealed class FakeProbe : IGatewayProbe { public bool Success = true; public Task<bool> CheckAsync(GatewayContext context, CancellationToken ct) => Task.FromResult(Success); }
sealed class Fixture : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "eth-tests-" + Guid.NewGuid().ToString("N"));
    public FakeApi Api { get; } = new(); public FakeDns Dns { get; } = new(); public FakeProbe Probe { get; } = new();
    public string Journal => Path.Combine(dir, "route-journal.json");
    public GatewayRouteController Controller { get; }
    public List<RouteEntry> Original { get; }
    public Fixture() { Original = Api.Routes.ToList(); Controller = new(Api, Dns, Probe, Journal); }
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
