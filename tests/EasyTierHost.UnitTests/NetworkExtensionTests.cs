using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;
using EasyTierHost.Network;

static class NetworkExtensionTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> Cases =>
    [
        ("Node RPC parsing excludes plaintext configuration", NodeParsing),
        ("Peer RPC parsing extracts physical active endpoints", PeerParsing),
        ("DNS health rejects empty and truncated answer records", DnsAnswers),
        ("NAT health requires actual masquerade rule", NatRuleHealth),
        ("Resolved link JSON preserves IPv4/IPv6 and domains", ResolvedParsing),
        ("Linux DNS applies and restores only the selected link", LinuxDnsLifecycle),
        ("Linux DNS refuses foreign resolver changes", LinuxDnsForeignChange),
        ("Windows NRPT cleanup uses unique ownership token", WindowsDnsOwnership),
        ("Gateway upstreams exclude self and append optional fallback", GatewayUpstreams),
    ];
    private static void Check(bool condition) { if (!condition) throw new Exception("Network assertion failed"); }
    private static async Task Fails(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure"); }
    private static RouteSnapshot Snapshot => new("wan", 2, "192.0.2.5", "192.0.2.1", 10, [], [], ["127.0.0.53", "10.10.0.1", "192.0.2.53"]);
    private static Task NodeParsing()
    {
        var node = CoreStatusReader.ParseNode("""{"peer_id":42,"ipv4_addr":"10.10.0.1/16","inst_id":"f9d1d4f3-4452-42f8-9624-5bbd377a24fd","version":"2.6.4","config":"network_secret = 'do-not-store'"}""");
        Check(node.OverlayIp == "10.10.0.1" && node.PeerId == 42 && !JsonSerializer.Serialize(node).Contains("do-not-store")); return Task.CompletedTask;
    }
    private static Task PeerParsing()
    {
        var peers = CoreStatusReader.ParsePeers("""
            [{"route":{"peer_id":11,"ipv4_addr":{"address":{"addr":168427521},"network_length":16}},"peer":{"conns":[
            {"is_closed":false,"tunnel":{"remote_addr":{"url":"tcp://peer.example:11010"},"resolved_remote_addr":{"url":"tcp://192.0.2.10:11010"}}},
            {"is_closed":true,"tunnel":{"remote_addr":{"url":"udp://192.0.2.11:11010"}}},
            {"is_closed":false,"tunnel":{"remote_addr":{"url":"tcp://10.10.0.3:11010"}}}]}}]
            """);
        Check(peers.Count == 1 && peers[0].OverlayIp == "10.10.0.1" && peers[0].Endpoints.Single().ToString() == "192.0.2.10"); return Task.CompletedTask;
    }
    private static Task DnsAnswers()
    {
        var query = DnsMessage.CreateQuery("example.com"); query[2] |= 0x80; query[7] = 1;
        Check(!DnsMessage.HasAddressAnswer(query));
        byte[] answer = [0xc0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 10];
        var response = query.Concat(answer).ToArray(); Check(DnsMessage.HasAddressAnswer(response)); Check(!DnsMessage.HasAddressAnswer(response[..^1]));
        return Task.CompletedTask;
    }
    private static Task NatRuleHealth()
    {
        const string rule = """
            {"nftables":[{"rule":{"chain":"postrouting","comment":"EasyTierHost:123","expr":[
            {"match":{"op":"==","left":{"payload":{"protocol":"ip","field":"saddr"}},"right":{"prefix":{"addr":"10.10.0.0","len":16}}}},
            {"match":{"op":"==","left":{"meta":{"key":"oifname"}},"right":"wan"}},{"masquerade":null}]}}]}
            """;
        using var valid = JsonDocument.Parse(rule); using var foreign = JsonDocument.Parse(rule.Replace("\"wan\"", "\"other\""));
        using var empty = JsonDocument.Parse("{\"nftables\":[]}");
        var s = new GatewayPlatformSnapshot("eth_123", "123", "linux", Snapshot, new(7, "tun", "id", "10.10.0.1"), false, []);
        Check(LinuxNatManager.HasExpectedRule(valid.RootElement, s)); Check(!LinuxNatManager.HasExpectedRule(foreign.RootElement, s)); Check(!LinuxNatManager.HasExpectedRule(empty.RootElement, s)); return Task.CompletedTask;
    }
    private static Task ResolvedParsing()
    {
        using var dns = JsonDocument.Parse("[[2,[192,0,2,53]],[10,[32,1,13,184,0,0,0,0,0,0,0,0,0,0,0,53]]]");
        using var domains = JsonDocument.Parse("[[\"example.com\",false],[\".\",true]]"); using var def = JsonDocument.Parse("false");
        var result = ResolvedLink.Parse(dns.RootElement, domains.RootElement, def.RootElement);
        Check(result.Servers[0] == "192.0.2.53" && result.Servers[1] == "2001:db8::35" && result.Domains[1].RoutingOnly && !result.DefaultRoute); return Task.CompletedTask;
    }
    private static async Task LinuxDnsLifecycle()
    {
        var runner = new ResolvedRunner(); var controller = new LinuxDnsController(runner, _ => "tun-id");
        var snapshot = await controller.CaptureAsync(Snapshot, new(7, "10.10.0.11", []), default);
        await controller.ApplyAsync(snapshot, default); Check(runner.Server == "10.10.0.1" && runner.Domain == "~." && runner.Default);
        await controller.RestoreAsync(snapshot, default); Check(runner.Server == "" && runner.Domain == "" && !runner.Default);
        Check(runner.MutatedIndices.All(i => i == "7"));
    }
    private static async Task LinuxDnsForeignChange()
    {
        var runner = new ResolvedRunner(); var controller = new LinuxDnsController(runner, _ => "tun-id");
        var snapshot = await controller.CaptureAsync(Snapshot, new(7, "10.10.0.11", []), default);
        await controller.ApplyAsync(snapshot, default); runner.Server = "192.0.2.53";
        await Fails(() => controller.RestoreAsync(snapshot, default)); Check(runner.Server == "192.0.2.53");
    }
    private static async Task WindowsDnsOwnership()
    {
        var runner = new WindowsRunner(); var controller = new WindowsDnsController(runner);
        var captured = await controller.CaptureAsync(Snapshot, new(7, "10.10.0.11", []), default);
        await controller.ApplyAsync(captured, default); await controller.RestoreAsync(captured, default);
        Check(runner.Scripts[^1].Contains("Where-Object Comment -eq 'EasyTierHost:" + captured.DnsState!.OwnerToken + "'"));
        Check(!runner.Scripts.Any(s => s.Contains("Set-DnsClientServerAddress"))); // Physical DHCP resolver is preserved.
    }
    private static Task GatewayUpstreams()
    {
        var p = new NetworkProfile { NetworkName = "test", SecretFile = "test", Role = NodeRole.Gateway, AllowPublicDnsFallback = true };
        var servers = EasyTierHost.Service.GatewayCoordinator.ResolveUpstreams(p, Snapshot).Select(e => e.Address.ToString()).ToArray();
        Check(servers.SequenceEqual(["192.0.2.53", "1.1.1.1", "8.8.8.8"])); return Task.CompletedTask;
    }
    private sealed class WindowsRunner : ICommandRunner
    {
        public List<string> Scripts { get; } = [];
        public Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null) => throw new NotSupportedException();
        public Task<string> CheckedAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null)
        { Scripts.Add(System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Last()))); return Task.FromResult(""); }
    }
    private sealed class ResolvedRunner : ICommandRunner
    {
        public string Server = "", Domain = ""; public bool Default; public List<string> MutatedIndices { get; } = [];
        public Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null) => throw new NotSupportedException();
        public Task<string> CheckedAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null)
        {
            var a = arguments.ToArray();
            if (executable == "busctl")
            {
                return Task.FromResult(a[^1] switch
                {
                    "DNS" => "{\"data\":[" + (Server == "" ? "[]" : "[[2,[" + string.Join(',', System.Net.IPAddress.Parse(Server).GetAddressBytes()) + "]]]") + "]}",
                    "Domains" => "{\"data\":[" + (Domain == "" ? "[]" : "[[\".\",true]]") + "]}",
                    "DefaultRoute" => "{\"data\":[" + (Default ? "true" : "false") + "]}",
                    _ => throw new Exception("Unexpected property")
                });
            }
            if (a[0] == "flush-caches") return Task.FromResult("");
            MutatedIndices.Add(a[1]);
            switch (a[0]) { case "dns": Server = a[2]; break; case "domain": Domain = a[2]; break; case "default-route": Default = a[2] == "yes"; break; default: throw new Exception("Unexpected command"); }
            return Task.FromResult("");
        }
    }
}
