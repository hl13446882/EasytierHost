using System.Diagnostics;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public sealed class EasyTierProcessManager : IEasyTierProcessManager
{
    private Process? process;
    private Task[] drains = [];
    public int? ProcessId => IsRunning ? process!.Id : null;
    public bool IsRunning => process is { HasExited: false };
    private string? verifiedCore;
    public async Task StartAsync(NetworkProfile profile, string configurationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsRunning) throw new InvalidOperationException("Core already running");
        if (verifiedCore != profile.CorePath)
        {
            var help = await new CommandRunner().CheckedAsync(profile.CorePath, ["--help"], ct);
            if (!help.Contains("--dhcp-start", StringComparison.Ordinal) || !help.Contains("--dhcp-network", StringComparison.Ordinal))
                throw new HostException("ETH003", "Core is missing the required DHCP range patch");
            verifiedCore = profile.CorePath;
        }
        process?.Dispose();
        var info = new ProcessStartInfo(profile.CorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--config-file"); info.ArgumentList.Add(configurationPath);
        info.ArgumentList.Add("--rpc-portal"); info.ArgumentList.Add($"127.0.0.1:{profile.RpcPort}");
        process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start EasyTier core");
        // Core can print the configuration including secrets. Consume but never persist raw output.
        drains = [DrainAsync(process.StandardOutput), DrainAsync(process.StandardError)];
    }
    private static async Task DrainAsync(StreamReader reader) { while (await reader.ReadLineAsync() is not null) { } }
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        var current = process ?? throw new InvalidOperationException("Core not started");
        await current.WaitForExitAsync(ct); await Task.WhenAll(drains); return current.ExitCode;
    }
    public async Task StopAsync(CancellationToken ct)
    {
        if (IsRunning) { process!.Kill(true); await process.WaitForExitAsync(ct); }
        await Task.WhenAll(drains);
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); process?.Dispose(); }
}
