using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Gateway;
using EasyTierHost.Network;

namespace EasyTierHost.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.Length == 3 && args[0].Equals("service", StringComparison.OrdinalIgnoreCase))
            return WindowsServiceHost.Run("EasyTierHost", ct => RunServiceAsync(args[1], args[2], ct));

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = OperatingSystem.IsLinux() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
        try
        {
            if (args.Length == 0 || args[0] == "help")
            {
                Console.WriteLine("EasyTierHost 0.2.0\n  validate <network.json>\n  configure <network.json> <output.toml>\n  set-secret <secret-file>  (reads a secret from stdin)\n  run <network.json> <state-directory>\n  service <network.json> <state-directory>  (Windows SCM only)\n  status <network.json>\n  diagnostics <network.json>\n  dns <network.json>\n\nClient Internet activation is experimental and only runs when enableInternetGateway=true; Host binds Core underlay sockets to the captured physical IPv4 before route takeover.");
                return 0;
            }
            if (args[0] == "set-secret" && args.Length == 2)
            {
                var secret = Console.IsInputRedirected ? await Console.In.ReadLineAsync(stop.Token) : ReadPassword();
                if (string.IsNullOrWhiteSpace(secret)) throw new HostException("ETH003", "Secret is empty");
                var path = Path.GetFullPath(args[1]);
                if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("Create a private secret directory first");
                await SecretProvider.WriteAsync(path, secret, stop.Token);
                Console.WriteLine("Secret saved."); return 0;
            }
            if (args.Length < 2) throw new HostException("ETH003", "Profile path required");
            var p = await ConfigurationStore.LoadAsync(args[1], stop.Token);
            switch (args[0])
            {
                case "validate": Console.WriteLine($"Valid: {p.Role}, schema {p.SchemaVersion}"); return 0;
                case "configure" when args.Length == 3:
                    await WriteConfigAsync(p, args[2], stop.Token); Console.WriteLine("Configuration written to private file."); return 0;
                case "run" when args.Length == 3:
                    await RunAsync(p, Path.GetFullPath(args[2]), stop.Token); return 0;
                case "status":
                    Console.WriteLine(await new CommandRunner().CheckedAsync(p.CliPath, ["-p", $"127.0.0.1:{p.RpcPort}", "-o", "json", "peer"], stop.Token)); return 0;
                case "diagnostics": await DiagnosticsAsync(p, stop.Token); return 0;
                case "dns":
                    if (p.Role != NodeRole.Gateway) throw new HostException("ETH003", "DNS forwarder requires Gateway role");
                    await RunDnsAsync(p, stop.Token); return 0;
                default: throw new HostException("ETH003", "Unknown command or incorrect arguments; use help");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            // Unexpected errors may include credentials/remote output: retain only their type.
            Console.Error.WriteLine(ex is HostException ? ex.Message : $"Failure: {ex.GetType().Name}"); return 1;
        }
    }

    private static async Task<int> RunServiceAsync(string profilePath, string stateDirectory, CancellationToken ct)
    {
        try
        {
            var profile = await ConfigurationStore.LoadAsync(profilePath, ct);
            await RunAsync(profile, Path.GetFullPath(stateDirectory), ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch
        {
            // Runtime state/journals contain the actionable status without risking secret-bearing exception text.
            return 1;
        }
    }

    private static string ReadPassword()
    {
        Console.Write("Network secret: "); var secret = new System.Text.StringBuilder();
        while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; } else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar); }
        Console.WriteLine(); return secret.ToString();
    }
    private static async Task WriteConfigAsync(NetworkProfile profile, string path, CancellationToken ct, Guid? instanceId = null)
    {
        var secret = await SecretProvider.ReadAsync(profile.SecretFile, ct);
        var contents = EasyTierConfigBuilder.Build(profile, secret, instanceId);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Restrict a dedicated directory, never an arbitrary existing parent.
        if (!Directory.Exists(folder)) await SecretProvider.SecureDirectoryAsync(folder, ct);
        if (OperatingSystem.IsWindows())
        {
            var tempDir = Path.Combine(folder, ".private-" + Guid.NewGuid().ToString("N"));
            await SecretProvider.SecureDirectoryAsync(tempDir, ct);
            try { var temp = Path.Combine(tempDir, "core.toml"); await File.WriteAllTextAsync(temp, contents, ct); File.Move(temp, path, true); }
            finally { Directory.Delete(tempDir); }
        }
        else
        {
            var temp = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(contents.AsMemory(), ct);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static async Task RunAsync(NetworkProfile p, string state, CancellationToken ct)
    {
        await SecretProvider.SecureDirectoryAsync(state, ct);
        using var lease = new FileStream(Path.Combine(state, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var config = Path.Combine(state, "core.toml");
        var runner = new CommandRunner();
        var routeApi = RouteApi(runner);
        var probe = new GatewayProbe();
        var routeController = new GatewayRouteController(routeApi, DnsController(runner), probe, Path.Combine(state, "route-journal.json"));
        var clientInternet = new ClientInternetCoordinator(routeApi, routeController, probe, state);
        var gateway = new GatewayCoordinator(routeApi, OperatingSystem.IsWindows() ? new WindowsNatManager(runner) : new LinuxNatManager(runner), state);
        await clientInternet.RecoverAsync(ct);
        await gateway.RecoverAsync(ct);
        var instanceId = Guid.NewGuid();
        await WriteConfigAsync(p, config, ct, instanceId);
        await using var manager = new EasyTierProcessManager();
        var failures = new Queue<DateTimeOffset>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task? roleTask = null;
                int code;
                try
                {
                    await clientInternet.RecoverAsync(ct);
                    RouteSnapshot? launchPhysical = null;
                    var launch = new CoreLaunchOptions();
                    if (p.Role == NodeRole.Client && p.EnableInternetGateway)
                    {
                        launchPhysical = await routeApi.CaptureAsync(ct);
                        launch = new CoreLaunchOptions { UnderlaySourceIpv4 = launchPhysical.PhysicalIpv4 };
                    }
                    await manager.StartAsync(p, config, launch, ct);
                    var stateName = p.Role == NodeRole.Gateway ? "GatewayStarting" : p.Role == NodeRole.Client && p.EnableInternetGateway ? "ClientGatewayStarting" : "OverlayOnly";
                    await ConfigurationStore.SaveAtomicAsync(Path.Combine(state, "status.json"), new { BuildId = "0.2.0", Role = p.Role.ToString(), Pid = manager.ProcessId, StartedUtc = DateTimeOffset.UtcNow, State = stateName }, ct);
                    Console.WriteLine($"Core started, PID {manager.ProcessId}, role {p.Role}");
                    var exitTask = manager.WaitForExitAsync(iteration.Token);
                    if (p.Role == NodeRole.Gateway)
                        roleTask = gateway.RunAsync(p, manager, instanceId, iteration.Token);
                    else if (p.Role == NodeRole.Client && p.EnableInternetGateway)
                        roleTask = clientInternet.RunAsync(p, manager, instanceId, launchPhysical!, iteration.Token);

                    if (roleTask is null)
                    {
                        code = await exitTask;
                    }
                    else
                    {
                        var completed = await Task.WhenAny(exitTask, roleTask);
                        if (completed == roleTask)
                        {
                            await roleTask;
                            throw new HostException("ETH201", "Role coordinator stopped unexpectedly");
                        }
                        code = await exitTask;
                        await iteration.CancelAsync();
                        try { await roleTask; }
                        catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine(ex is HostException host ? host.Message : $"Core/network failure: {ex.GetType().Name}");
                    code = -1;
                }
                finally
                {
                    await iteration.CancelAsync();
                    await manager.StopAsync(CancellationToken.None);
                }
                var now = DateTimeOffset.UtcNow; failures.Enqueue(now);
                while (failures.Count > 0 && now - failures.Peek() > TimeSpan.FromMinutes(2)) failures.Dequeue();
                Console.Error.WriteLine($"Core exited ({code}); restart count {failures.Count}/2min");
                if (failures.Count > 5) throw new HostException("ETH101", "Repeated core failures: Degraded");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            if (File.Exists(config)) File.Delete(config);
            await ConfigurationStore.SaveAtomicAsync(Path.Combine(state, "status.json"), new { BuildId = "0.2.0", Role = p.Role.ToString(), State = "Stopped" });
        }
    }
    private static IRouteApi RouteApi(CommandRunner runner) => OperatingSystem.IsWindows() ? new WindowsRouteApi(runner) : new LinuxRouteApi(runner);
    private static IDnsController DnsController(CommandRunner runner) => OperatingSystem.IsWindows() ? new WindowsDnsController(runner) : new LinuxDnsController(runner);
    private static async Task DiagnosticsAsync(NetworkProfile p, CancellationToken ct)
    {
        var runner = new CommandRunner();
        RouteSnapshot? physical = null; string? captureError = null;
        try { physical = await RouteApi(runner).CaptureAsync(ct); } catch (Exception ex) { captureError = ex.GetType().Name; }
        var overlay = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(OverlayAddressPlan.IsOverlay).Select(a => a.ToString()).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { BuildId = "0.2.0", CoreBase = "2.6.4", Schema = p.SchemaVersion, Role = p.Role.ToString(), p.SeedPhysicalIp, Physical = physical, OverlayAddresses = overlay, CaptureError = captureError, GatewayState = p.Role == NodeRole.Gateway ? "UseGatewayStatusFileForRuntimeState" : p.Role == NodeRole.Client && p.EnableInternetGateway ? "UseClientGatewayStatusFileForRuntimeState" : "OverlayOnly" }, ConfigurationStore.Json));
    }
    private static async Task RunDnsAsync(NetworkProfile p, CancellationToken ct)
    {
        if (!NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.ToString() == OverlayAddressPlan.Gateway)) throw new HostException("ETH201", "Gateway TUN address is not ready");
        var physical = await RouteApi(new CommandRunner()).CaptureAsync(ct);
        var upstreams = GatewayCoordinator.ResolveUpstreams(p, physical);
        await new DnsForwarderService(new(IPAddress.Parse(OverlayAddressPlan.Gateway), 53), upstreams).RunAsync(ct);
    }
}
