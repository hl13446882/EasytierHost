using System.Net;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Service;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

var temp = Path.Combine(Path.GetTempPath(), "eth-diagnostics-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    await ConfigurationStore.SaveAtomicAsync(Path.Combine(temp, "status.json"), new
    {
        BuildId = "0.2.0",
        Role = "Client",
        Pid = 1234,
        State = "ClientGatewayStarting"
    });
    await ConfigurationStore.SaveAtomicAsync(Path.Combine(temp, "client-gateway-status.json"), new
    {
        State = "GatewayActive"
    });

    var history = new RuntimeErrorHistory(Path.Combine(temp, "recent-errors.json"));
    await history.AppendAsync(new InvalidOperationException("must-never-be-persisted"));
    await history.AppendAsync(new HostException("ETH203", "Internet probe failed"));
    var stored = await history.ReadAsync();
    Check(stored.Count == 2, "runtime error history count changed");
    Check(stored[0].Code == "UNEXPECTED" && stored[0].Message == nameof(InvalidOperationException), "unexpected exception was not sanitized");
    Check(stored[1].Code == "ETH203" && stored[1].Message == "Internet probe failed", "HostException diagnostics changed");

    var profile = new NetworkProfile
    {
        NetworkName = "diagnostics-test",
        SecretFile = "must-never-appear.secret",
        Role = NodeRole.Client,
        SeedPhysicalIp = "192.0.2.10",
        CorePath = "fake-core",
        CliPath = "fake-cli",
        DeviceName = "easytierhost",
        EnableInternetGateway = true
    };
    var api = new FakeRouteApi();
    var snapshot = await DiagnosticsCollector.CollectAsync(profile, temp, new FakeRunner(), api);
    Check(snapshot.BuildId == "0.2.0" && snapshot.CoreBase == "2.6.4", "build identity missing");
    Check(snapshot.RuntimeState == "ClientGatewayStarting" && snapshot.CorePid == 1234, "runtime status was not read");
    Check(snapshot.GatewayState == "GatewayActive", "gateway status was not read");
    Check(snapshot.PhysicalIpv4 == "192.168.1.20" && snapshot.PhysicalGateway == "192.168.1.1", "physical network missing");
    Check(snapshot.OverlayIp == "10.10.0.11", "Core overlay address missing");
    Check(snapshot.ProtectedEndpoints.SequenceEqual(["192.0.2.10"]), "Seed protection route was not recognized");
    Check(snapshot.RouteMetrics.Any(route => route.Destination == "0.0.0.0/0" && route.TotalMetric == 35), "route metric diagnostics missing");
    Check(snapshot.RecentErrors.Count == 2, "recent errors missing from diagnostics");

    var json = JsonSerializer.Serialize(snapshot, ConfigurationStore.Json);
    Check(!json.Contains("must-never-be-persisted", StringComparison.Ordinal), "unexpected exception message leaked into diagnostics");
    Check(!json.Contains("must-never-appear.secret", StringComparison.Ordinal), "secret file path leaked into diagnostics");
    Console.WriteLine("PASS structured diagnostics, route metrics, protected endpoints and sanitized error history");
    return 0;
}
finally
{
    Directory.Delete(temp, true);
}

sealed class FakeRouteApi : IRouteApi
{
    private readonly RouteEntry defaultRoute = new("0.0.0.0/0", "192.168.1.1", 7, 10) { CreatedByEasyTierHost = false, OwnerTag = "" };
    private readonly RouteEntry protectedSeed = new("192.0.2.10/32", "192.168.1.1", 7, 1);

    public Task<RouteSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(new RouteSnapshot(
        "Ethernet",
        7,
        "192.168.1.20",
        "192.168.1.1",
        25,
        [defaultRoute],
        [new RouteEntry("192.168.1.0/24", "0.0.0.0", 7, 0) { CreatedByEasyTierHost = false, OwnerTag = "" }],
        ["192.168.1.1"]));

    public Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RouteEntry>>([defaultRoute, protectedSeed]);

    public Task AddAsync(RouteEntry route, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(RouteEntry route, CancellationToken ct) => throw new NotSupportedException();
}

sealed class FakeRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null) =>
        Task.FromResult(new CommandResult(0, Result(executable, arguments), string.Empty));

    public Task<string> CheckedAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null) =>
        Task.FromResult(Result(executable, arguments));

    private static string Result(string executable, IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        if (executable.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            return "[{\"Destination\":\"0.0.0.0/0\",\"NextHop\":\"192.168.1.1\",\"InterfaceIndex\":7,\"RouteMetric\":10,\"InterfaceMetric\":25,\"TotalMetric\":35}]";
        if (args.Contains("node", StringComparer.Ordinal))
            return "{\"peer_id\":1,\"inst_id\":\"11111111-1111-1111-1111-111111111111\",\"ipv4_addr\":\"10.10.0.11/16\",\"version\":\"2.6.4\"}";
        if (args.Contains("peer", StringComparer.Ordinal)) return "[]";
        throw new InvalidOperationException("Unexpected fake command");
    }
}
