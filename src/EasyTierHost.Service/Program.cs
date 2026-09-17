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
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = OperatingSystem.IsLinux() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
        try
        {
            if (args.Length == 0 || args[0] == "help")
            {
                Console.WriteLine("EasyTierHost 0.2.0\n  validate <network.json>\n  configure <network.json> <output.toml>\n  set-secret <secret-file>  (reads a secret from stdin)\n  run <network.json> <state-directory>\n  status <network.json>\n  diagnostics <network.json>\n  dns <network.json>\n\nInternet default-route activation is disabled pending complete underlay protection.");
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
    private static string ReadPassword()
    {
        Console.Write("Network secret: "); var secret = new System.Text.StringBuilder();
        while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; } else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar); }
        Console.WriteLine(); return secret.ToString();
    }
    private static async Task WriteConfigAsync(NetworkProfile profile, string path, CancellationToken ct)
    {
        var secret = await SecretProvider.ReadAsync(profile.SecretFile, ct);
        var contents = EasyTierConfigBuilder.Build(profile, secret);
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
        if (p.EnableInternetGateway) throw new HostException("ETH301", "Internet gateway activation requires the unfinished underlay protection audit; overlay-only mode is available");
        await SecretProvider.SecureDirectoryAsync(state, ct);
        using var lease = new FileStream(Path.Combine(state, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var config = Path.Combine(state, "core.toml");
        if (File.Exists(Path.Combine(state, "route-journal.json"))) throw new HostException("ETH302", "A route recovery journal exists; recover it before starting");
        var gateway = new GatewayCoordinator(RouteApi(new CommandRunner()), OperatingSystem.IsWindows() ? new WindowsNatManager(new CommandRunner()) : new LinuxNatManager(new CommandRunner()), state);
        await gateway.RecoverAsync(ct);
        await WriteConfigAsync(p, config, ct);
        await using var manager = new EasyTierProcessManager();
        var failures = new Queue<DateTimeOffset>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task? gatewayTask = null;
                int code;
                try
                {
                    await manager.StartAsync(p, config, ct);
                    await ConfigurationStore.SaveAtomicAsync(Path.Combine(state, "status.json"), new { BuildId = "0.2.0", Role = p.Role.ToString(), Pid = manager.ProcessId, StartedUtc = DateTimeOffset.UtcNow, State = p.Role == NodeRole.Gateway ? "GatewayStarting" : "OverlayOnly" }, ct);
                    Console.WriteLine($"Core started, PID {manager.ProcessId}, role {p.Role}");
                    var exitTask = manager.WaitForExitAsync(iteration.Token);
                    if (p.Role == NodeRole.Gateway)
                    {
                        gatewayTask = gateway.RunAsync(p, manager, iteration.Token);
                        await await Task.WhenAny(exitTask, gatewayTask);
                    }
                    code = await exitTask;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine(ex is HostException host ? host.Message : $"Core/gateway failure: {ex.GetType().Name}");
                    code = -1;
                }
                finally
                {
                    await iteration.CancelAsync();
                    try
                    {
                        if (gatewayTask is not null)
                            try { await gatewayTask; } catch (Exception ex) when (ex is not AggregateException) { }
                    }
                    finally { await manager.StopAsync(CancellationToken.None); }
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
    private static async Task DiagnosticsAsync(NetworkProfile p, CancellationToken ct)
    {
        var runner = new CommandRunner();
        RouteSnapshot? physical = null; string? captureError = null;
        try { physical = await RouteApi(runner).CaptureAsync(ct); } catch (Exception ex) { captureError = ex.GetType().Name; }
        var overlay = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(OverlayAddressPlan.IsOverlay).Select(a => a.ToString()).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { BuildId = "0.2.0", CoreBase = "2.6.4", Schema = p.SchemaVersion, Role = p.Role.ToString(), p.SeedPhysicalIp, Physical = physical, OverlayAddresses = overlay, CaptureError = captureError, GatewayState = "DisabledPendingUnderlayAudit" }, ConfigurationStore.Json));
    }
    private static async Task RunDnsAsync(NetworkProfile p, CancellationToken ct)
    {
        if (!NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.ToString() == OverlayAddressPlan.Gateway)) throw new HostException("ETH201", "Gateway TUN address is not ready");
        var upstreams = p.DnsUpstreams;
        if (upstreams.Length == 0) upstreams = (await RouteApi(new CommandRunner()).CaptureAsync(ct)).OriginalDnsServers.Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(ip) && !IPAddress.IsLoopback(ip)).ToArray();
        if (upstreams.Length == 0 && p.AllowPublicDnsFallback) upstreams = ["1.1.1.1", "8.8.8.8"];
        if (upstreams.Length == 0) throw new HostException("ETH202", "No physical DNS upstream configured");
        await new DnsForwarderService(new(IPAddress.Parse(OverlayAddressPlan.Gateway), 53), upstreams.Select(ip => new IPEndPoint(IPAddress.Parse(ip), 53)).ToArray()).RunAsync(ct);
    }
}
