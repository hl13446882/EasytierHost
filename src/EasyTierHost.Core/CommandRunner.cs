using System.Diagnostics;

namespace EasyTierHost.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error);
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null);
    Task<string> CheckedAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null);
}
public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    public async Task<string> CheckedAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, string? input = null)
    {
        var result = await RunAsync(executable, arguments, ct, input);
        // Do not echo command arguments or stderr, which may contain credentials.
        if (result.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {result.ExitCode})");
        return result.Output;
    }
}
