using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EasyTierHost.Client.Windows.Services;

public sealed record LocalCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Controls only the EasyTierHost Windows service. It never starts EasyTier Core directly and
/// never changes routes/DNS itself; those remain owned by EasyTierHost.Service.
/// </summary>
public sealed partial class EasyTierClientService(ClientRuntimePaths paths)
{
    private const string ServiceName = "EasyTierHost";

    [GeneratedRegex(@"STATE\s*:\s*\d+\s+([A-Z_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceStatePattern();

    public async Task InstallOrStartAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.HostExecutable))
            throw new InvalidOperationException($"Missing EasyTierHost service executable: {paths.HostExecutable}");
        if (!File.Exists(paths.InstallScript))
            throw new InvalidOperationException($"Missing Windows service installer: {paths.InstallScript}");

        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) shell = "powershell.exe";
        var result = await RunAsync(shell,
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", paths.InstallScript,
            "-InstallRoot", paths.PackageRoot,
            "-ProfilePath", paths.ProfilePath,
            "-StateDirectory", paths.StateDirectory,
            "-ServiceName", ServiceName
        ], TimeSpan.FromSeconds(60), ct);
        if (!result.Success)
            throw new InvalidOperationException($"EasyTierHost service installation failed (exit {result.ExitCode}).");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var current = await QueryStateAsync(ct);
        if (current is "NotInstalled" or "STOPPED") return;

        var result = await RunScAsync(["stop", ServiceName], TimeSpan.FromSeconds(15), ct);
        if (!result.Success && result.ExitCode != 1062)
            throw new InvalidOperationException($"Unable to stop {ServiceName} (exit {result.ExitCode}).");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await QueryStateAsync(ct) is "STOPPED" or "NotInstalled") return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"{ServiceName} did not stop within 35 seconds.");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var state = await QueryStateAsync(ct);
        if (state == "RUNNING") return;
        if (state == "NotInstalled")
        {
            await InstallOrStartAsync(ct);
            return;
        }
        var result = await RunScAsync(["start", ServiceName], TimeSpan.FromSeconds(15), ct);
        if (!result.Success) throw new InvalidOperationException($"Unable to start {ServiceName} (exit {result.ExitCode}).");
    }

    public async Task<string> QueryStateAsync(CancellationToken ct)
    {
        var result = await RunScAsync(["query", ServiceName], TimeSpan.FromSeconds(10), ct);
        if (!result.Success) return "NotInstalled";
        var match = ServiceStatePattern().Match(result.StdOut);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "Unknown";
    }

    public Task<LocalCommandResult> ReadyAsync(CancellationToken ct) =>
        RunHostAsync(["ready", paths.ProfilePath], TimeSpan.FromSeconds(10), ct);

    public Task<LocalCommandResult> DiagnosticsAsync(CancellationToken ct) =>
        RunHostAsync(["diagnostics", paths.ProfilePath], TimeSpan.FromSeconds(15), ct);

    private Task<LocalCommandResult> RunHostAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(paths.HostExecutable))
            throw new InvalidOperationException($"Missing EasyTierHost service executable: {paths.HostExecutable}");
        return RunAsync(paths.HostExecutable, args, timeout, ct);
    }

    private static Task<LocalCommandResult> RunScAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        return RunAsync(sc, args, timeout, ct);
    }

    private static async Task<LocalCommandResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Unable to start {Path.GetFileName(fileName)}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}
