using System.Diagnostics;
using System.Net;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public sealed class EasyTierProcessManager : IEasyTierProcessManager
{
    private Process? process;
    private Task[] drains = [];
    private string? configuredUnderlaySourceIpv4;
    private string? verifiedCore;
    private bool verifiedUnderlayCapability;
    public int? ProcessId => IsRunning ? process!.Id : null;
    public bool IsRunning => process is { HasExited: false };
    public string? UnderlaySourceIpv4 => IsRunning ? configuredUnderlaySourceIpv4 : null;

    public async Task StartAsync(NetworkProfile profile, string configurationPath, CoreLaunchOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsRunning) throw new InvalidOperationException("Core already running");
        var args = EasyTierArgumentBuilder.Build(profile, configurationPath, options);
        if (verifiedCore != profile.CorePath)
        {
            var help = await new CommandRunner().CheckedAsync(profile.CorePath, ["--help"], ct);
            if (!help.Contains("--dhcp-start", StringComparison.Ordinal) || !help.Contains("--dhcp-network", StringComparison.Ordinal))
                throw new HostException("ETH003", "Core is missing the required DHCP range patch");
            verifiedUnderlayCapability = help.Contains("--underlay-source-ipv4", StringComparison.Ordinal);
            verifiedCore = profile.CorePath;
        }
        if (!string.IsNullOrWhiteSpace(options.UnderlaySourceIpv4) && !verifiedUnderlayCapability)
            throw new HostException("ETH301", "Core is missing the required underlay binding patch");

        process?.Dispose();
        configuredUnderlaySourceIpv4 = string.IsNullOrWhiteSpace(options.UnderlaySourceIpv4)
            ? null
            : IPAddress.Parse(options.UnderlaySourceIpv4).ToString();
        var info = new ProcessStartInfo(profile.CorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        process = new Process { StartInfo = info };
        if (!process.Start())
        {
            configuredUnderlaySourceIpv4 = null;
            throw new IOException("Could not start EasyTier core");
        }
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
        configuredUnderlaySourceIpv4 = null;
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); process?.Dispose(); }
}
