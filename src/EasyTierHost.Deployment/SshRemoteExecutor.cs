using System.Diagnostics;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

/// <summary>
/// Uses the operating system OpenSSH client. Key/agent authentication is non-interactive and does
/// not expose credentials on a command line. Password authentication is intentionally not emulated.
/// </summary>
public sealed class SshRemoteExecutor(RemoteHostCredential credential) : IRemoteExecutor
{
    private readonly RemoteHostCredential _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteAsync(OperatingSystem.IsWindows() ? "cmd /c exit 0" : "true", ct);
            return result.Success;
        }
        catch (Exception ex) when (ex is HostException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task<RemoteCommandResult> ExecuteAsync(string command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        EnsureNonInteractiveAuthentication();
        var args = CommonSshArguments();
        args.Add($"{_credential.Username}@{_credential.Host}");
        args.Add(command);
        return RunAsync("ssh", args, ct);
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        EnsureNonInteractiveAuthentication();
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
            throw new FileNotFoundException("Deployment source does not exist", localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        var args = new List<string> { "-q", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10", "-P", _credential.Port.ToString() };
        AddIdentity(args);
        if (Directory.Exists(localPath)) args.Add("-r");
        args.Add(Path.GetFullPath(localPath));
        args.Add($"{_credential.Username}@{_credential.Host}:{remotePath}");
        var result = await RunAsync("scp", args, ct);
        if (!result.Success) throw new HostException("ETH001", "SCP upload failed");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EnsureNonInteractiveAuthentication()
    {
        if (!string.IsNullOrEmpty(_credential.Password))
            throw new HostException("ETH001", "Password authentication is not passed to child processes; configure an SSH key or ssh-agent");
        if (_credential.Port is < 1 or > 65535) throw new HostException("ETH003", "SSH port is out of range");
        if (string.IsNullOrWhiteSpace(_credential.Host) || string.IsNullOrWhiteSpace(_credential.Username))
            throw new HostException("ETH003", "SSH host and username are required");
    }

    private List<string> CommonSshArguments()
    {
        var args = new List<string> { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10", "-p", _credential.Port.ToString() };
        AddIdentity(args);
        return args;
    }

    private void AddIdentity(List<string> args)
    {
        if (string.IsNullOrWhiteSpace(_credential.PrivateKeyPath)) return;
        var key = Path.GetFullPath(_credential.PrivateKeyPath);
        if (!File.Exists(key)) throw new FileNotFoundException("SSH private key does not exist", key);
        args.Add("-i");
        args.Add(key);
    }

    private static async Task<RemoteCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new HostException("ETH001", $"Unable to start {fileName}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new HostException("ETH001", $"OpenSSH executable '{fileName}' was not found");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new RemoteCommandResult(process.ExitCode, await stdout, await stderr);
    }
}

public static class RemoteExecutorFactory
{
    public static IRemoteExecutor Create(RemoteHostCredential credential) => new SshRemoteExecutor(credential);
}
