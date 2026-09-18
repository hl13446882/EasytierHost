using System.Net;
using System.Net.Sockets;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;

static class GatewayTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> Cases =>
    [
        ("Gateway startup is idempotent and stop restores owned state", Lifecycle),
        ("Gateway partial NAT failure rolls back forwarding and NAT", PartialNatFailure),
        ("Gateway DNS failure rolls back NAT and forwarding", DnsFailure),
        ("Gateway startup health failure is not marked ready", HealthFailure),
        ("Gateway crash recovery and retryable cleanup", Recovery),
        ("Gateway rejects missing TUN before platform writes", MissingTun),
        ("Gateway rejects cross-platform recovery journal", CrossPlatform),
        ("Linux NAT has precise prefix/interface and no ruleset flush", LinuxRules),
        ("Linux refuses removal of foreign table", LinuxOwnership),
        ("Linux NAT falls back to owned iptables rule", LinuxIptablesFallback),
        ("DNS question correlation and compression loop rejection", DnsValidation),
        ("DNS mismatched upstream falls back", DnsFallback),
        ("DNS forwarder real UDP/TCP listeners and stop", DnsListeners),
    ];
    private static void Assert(bool ok) { if (!ok) throw new Exception("Gateway assertion failed"); }
    private static async Task Fail(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure"); }
    private static RouteSnapshot Physical => new("wan0", 2, "192.0.2.2", "192.0.2.1", 25, [], [], ["192.0.2.53"]);
    private static GatewayAdapter Overlay => new(7, "easytierhost", "adapter-guid", "10.10.0.1");
    private static async Task Lifecycle()
    {
        using var f = new Fixture(); await f.Controller.StartAsync(Physical, Overlay); await f.Controller.StartAsync(Physical, Overlay);
        Assert(f.Controller.State == GatewayServerState.Ready && f.Platform.CreateCount == 1 && f.Dns.Active);
        await f.Controller.StopAsync(); await f.Controller.StopAsync();
        Assert(!f.Platform.Nat && !f.Platform.Forwarding && !f.Dns.Active && !File.Exists(f.Path));
    }
    private static async Task PartialNatFailure()
    {
        using var f = new Fixture(); f.Platform.FailCreate = true;
        await Fail(() => f.Controller.StartAsync(Physical, Overlay));
        Assert(!f.Platform.Nat && !f.Platform.Forwarding && !f.Dns.Active && !File.Exists(f.Path));
    }
    private static async Task DnsFailure()
    {
        using var f = new Fixture(); f.Dns.FailStart = true;
        await Fail(() => f.Controller.StartAsync(Physical, Overlay));
        Assert(!f.Platform.Nat && !f.Platform.Forwarding && !f.Dns.Active);
    }
    private static async Task HealthFailure()
    {
        using var f = new Fixture(); f.Dns.Healthy = false;
        await Fail(() => f.Controller.StartAsync(Physical, Overlay));
        Assert(f.Controller.State == GatewayServerState.Stopped && !f.Platform.Nat);
    }
    private static async Task Recovery()
    {
        using var f = new Fixture(); await f.Controller.StartAsync(Physical, Overlay); f.Platform.FailRemove = true;
        var recovered = new GatewayBootstrapper(f.Platform, f.Dns, f.Path);
        await Fail(() => recovered.RecoverAsync());
        Assert(File.Exists(f.Path) && !f.Platform.Forwarding && f.Platform.Nat && !f.Dns.Active);
        f.Platform.FailRemove = false; await recovered.RecoverAsync(); await recovered.RecoverAsync();
        Assert(!f.Platform.Nat && !File.Exists(f.Path));
    }
    private static async Task MissingTun()
    {
        using var f = new Fixture(); await Fail(() => f.Controller.StartAsync(Physical, Overlay with { Address = "10.10.0.2" }));
        Assert(f.Platform.CreateCount == 0 && !f.Platform.Forwarding && !File.Exists(f.Path));
    }
    private static async Task CrossPlatform()
    {
        using var f = new Fixture(); await f.Controller.StartAsync(Physical, Overlay);
        f.Platform.Platform = "other";
        await Fail(() => new GatewayBootstrapper(f.Platform, f.Dns, f.Path).RecoverAsync());
        Assert(f.Platform.Nat && f.Platform.Forwarding && File.Exists(f.Path));
        f.Platform.Platform = "fake"; await f.Controller.StopAsync();
    }
    private static Task LinuxRules()
    {
        var s = new GatewayPlatformSnapshot("eth_123", "123", "linux", Physical, Overlay, false, []);
        var script = LinuxNatManager.NatScript(s);
        Assert(script.Contains("ip saddr 10.10.0.0/16 oifname \"wan0\" masquerade") && !script.Contains("flush") && script.Contains("EasyTierHost:123"));
        try { LinuxNatManager.NatScript(s with { Physical = Physical with { PhysicalInterfaceName = "wan\";flush ruleset" } }); }
        catch (HostException) { return Task.CompletedTask; }
        throw new Exception("Accepted unsafe interface name");
    }
    private static async Task LinuxOwnership()
    {
        var runner = new FakeRunner();
        var platform = new LinuxNatManager(runner);
        await Fail(() => platform.RemoveNatAsync(new("eth_123", "123", "linux", Physical, Overlay, false, []), default));
        Assert(!runner.Commands.Any(a => a.Contains("delete")));
    }
    private static async Task LinuxIptablesFallback()
    {
        var runner = new FallbackRunner();
        var platform = new LinuxNatManager(runner);
        var snapshot = await platform.CaptureAsync(Physical, Overlay, default);
        Assert(snapshot.ResourceName.StartsWith("ipt_", StringComparison.Ordinal));
        var args = LinuxNatManager.IptablesRuleArguments(snapshot, "-A");
        Assert(args.Contains("10.10.0.0/16") && args.Contains("wan0") && args.Contains("EasyTierHost:" + snapshot.OwnerToken));
        await platform.EnableForwardingAsync(snapshot, default);
        await platform.CreateNatAsync(snapshot, default);
        Assert(runner.Rule && runner.Forwarding && await platform.VerifyAsync(snapshot, default));
        await platform.RemoveNatAsync(snapshot, default);
        await platform.RestoreForwardingAsync(snapshot, default);
        Assert(!runner.Rule && !runner.Forwarding);
    }
    private static Task DnsValidation()
    {
        var query = DnsMessage.CreateQuery("example.com"); var response = query.ToArray(); response[2] |= 0x80;
        Assert(DnsMessage.IsResponseTo(query, response)); response[13] = (byte)'x'; Assert(!DnsMessage.IsResponseTo(query, response));
        byte[] loop = [0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 12, 0, 1, 0, 1];
        try { DnsMessage.Question(loop, out _); } catch (IOException) { return Task.CompletedTask; }
        throw new Exception("Compression loop accepted");
    }
    private static async Task DnsFallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var wrong = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var good = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var badTask = Reply(wrong, true); var goodTask = Reply(good, false);
        var query = DnsMessage.CreateQuery("example.com");
        var f = new DnsForwarderService(new(IPAddress.Parse("10.10.0.1"), 53), [(IPEndPoint)wrong.Client.LocalEndPoint!, (IPEndPoint)good.Client.LocalEndPoint!]);
        var response = await f.ResolveAsync(query, false, timeout.Token);
        Assert(DnsMessage.IsResponseTo(query, response)); await Task.WhenAll(badTask, goodTask);
        async Task Reply(UdpClient server, bool mismatch)
        {
            var packet = await server.ReceiveAsync(timeout.Token); packet.Buffer[2] |= 0x80;
            if (mismatch) packet.Buffer[13] = (byte)'x';
            await server.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token);
        }
    }
    private static async Task DnsListeners()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tcpUpstream = new TcpListener(IPAddress.Loopback, 0);
        tcpUpstream.Start();
        var upstreamAddress = (IPEndPoint)tcpUpstream.LocalEndpoint;
        using var upstream = new UdpClient(upstreamAddress);
        var listen = FindDualProtocolLoopbackPort();
        var udpTask = Task.Run(async () => { var packet = await upstream.ReceiveAsync(timeout.Token); packet.Buffer[2] |= 0x80; await upstream.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token); });
        var tcpTask = Task.Run(async () => { using var client = await tcpUpstream.AcceptTcpClientAsync(timeout.Token); var q = await DnsMessage.ReadFrameAsync(client.GetStream(), timeout.Token); q[2] |= 0x80; await DnsMessage.WriteFrameAsync(client.GetStream(), q, timeout.Token); });
        await using var runtime = new GatewayDnsRuntime(listen, [upstreamAddress]);
        try
        {
            await runtime.StartAsync(timeout.Token);
            var query = DnsMessage.CreateQuery("example.com");
            Assert(DnsMessage.IsResponseTo(query, await DnsMessage.ExchangeAsync(listen, query, false, timeout.Token)));
            Assert(DnsMessage.IsResponseTo(query, await DnsMessage.ExchangeAsync(listen, query, true, timeout.Token)));
            await runtime.StopAsync(timeout.Token); await Task.WhenAll(udpTask, tcpTask);
            using var reboundUdp = new UdpClient(listen); var reboundTcp = new TcpListener(listen); reboundTcp.Start(); reboundTcp.Stop();
        }
        finally { tcpUpstream.Stop(); }
    }
    private static IPEndPoint FindDualProtocolLoopbackPort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var endpoint = (IPEndPoint)tcp.LocalEndpoint;
            try
            {
                using var udp = new UdpClient(endpoint);
                return endpoint;
            }
            catch (SocketException) { }
            finally { tcp.Stop(); }
        }
        throw new IOException("Unable to reserve a loopback port usable by both TCP and UDP");
    }
    private sealed class FakePlatform : IGatewayPlatform
    {
        public string Platform { get; set; } = "fake";
        public bool Forwarding, Nat, FailCreate, FailRemove;
        public int CreateCount;
        public Task<GatewayPlatformSnapshot> CaptureAsync(RouteSnapshot physical, GatewayAdapter overlay, CancellationToken ct) => Task.FromResult(new GatewayPlatformSnapshot("owned", "123", Platform, physical, overlay, false, []));
        public Task EnableForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct) { Forwarding = true; return Task.CompletedTask; }
        public Task RestoreForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct) { Forwarding = false; return Task.CompletedTask; }
        public Task CreateNatAsync(GatewayPlatformSnapshot s, CancellationToken ct) { Nat = true; CreateCount++; if (FailCreate) throw new IOException("partial failure"); return Task.CompletedTask; }
        public Task RemoveNatAsync(GatewayPlatformSnapshot s, CancellationToken ct) { if (FailRemove) throw new IOException("cleanup failure"); Nat = false; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(GatewayPlatformSnapshot s, CancellationToken ct) => Task.FromResult(Nat && Forwarding);
    }
    private sealed class FakeDns : IGatewayDnsRuntime
    {
        public bool Active, FailStart, Healthy = true;
        public Task StartAsync(CancellationToken ct) { Active = true; if (FailStart) throw new IOException("bind failure"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Active = false; return Task.CompletedTask; }
        public Task<bool> CheckAsync(CancellationToken ct) => Task.FromResult(Active && Healthy);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eth-gateway-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "gateway-journal.json");
        public FakePlatform Platform { get; } = new(); public FakeDns Dns { get; } = new();
        public GatewayBootstrapper Controller { get; }
        public Fixture() { Controller = new(Platform, Dns, Path); }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class FakeRunner : ICommandRunner
    {
        public List<string[]> Commands { get; } = [];
        public Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, CancellationToken ct = default, string? input = null) => Task.FromResult(new CommandResult(0, "", ""));
        public Task<string> CheckedAsync(string executable, IEnumerable<string> args, CancellationToken ct = default, string? input = null)
        {
            var a = args.ToArray(); Commands.Add(a);
            return Task.FromResult(a.Contains("tables") ? "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"eth_123\"}}]}" : "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"eth_123\",\"comment\":\"another-owner\"}}]}");
        }
    }
    private sealed class FallbackRunner : ICommandRunner
    {
        public bool Rule { get; private set; }
        public bool Forwarding { get; private set; }
        public Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, CancellationToken ct = default, string? input = null)
        {
            var a = args.ToArray();
            if (executable == "nft") return Task.FromResult(new CommandResult(1, "", "not available"));
            if (executable == "iptables" && a.Contains("-C")) return Task.FromResult(new CommandResult(Rule ? 0 : 1, "", ""));
            return Task.FromResult(new CommandResult(0, "", ""));
        }
        public Task<string> CheckedAsync(string executable, IEnumerable<string> args, CancellationToken ct = default, string? input = null)
        {
            var a = args.ToArray();
            if (executable == "sysctl")
            {
                if (a.Contains("-w")) Forwarding = a.Last().EndsWith("=1", StringComparison.Ordinal);
                return Task.FromResult(Forwarding ? "1" : "0");
            }
            if (executable == "iptables")
            {
                if (a.Contains("-A")) Rule = true;
                if (a.Contains("-D")) Rule = false;
                return Task.FromResult(string.Empty);
            }
            throw new InvalidOperationException("Unexpected command");
        }
    }
}
