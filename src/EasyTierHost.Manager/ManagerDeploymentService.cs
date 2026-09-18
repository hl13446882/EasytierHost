using System.IO;
using System.Net;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;
using EasyTierHost.Deployment;

namespace EasyTierHost.Manager;

public sealed record ManagerDeploymentOptions
{
    public required ServerOsType OsType { get; init; }
    public required NodeRole Role { get; init; }
    public required string RemotePhysicalIp { get; init; }
    public int SshPort { get; init; } = 22;
    public required string Username { get; init; }
    public string? PrivateKeyPath { get; init; }
    public required string NetworkName { get; init; }
    public required string NetworkSecret { get; init; }
    public int ListenerPort { get; init; } = 11010;
    public string? SeedPhysicalIp { get; init; }
    public int? DedicatedIndex { get; init; }
    public required string LocalPackageDirectory { get; init; }
}

public sealed class ManagerDeploymentService
{
    public async Task<DeploymentResult> TestConnectionAsync(ManagerDeploymentOptions options, CancellationToken ct = default)
    {
        try
        {
            ValidateRemoteOptions(options);
            var credential = Credential(options);
            await using var remote = RemoteExecutorFactory.Create(credential);
            return await remote.TestConnectionAsync(ct)
                ? DeploymentResult.Ok("SSH connection succeeded")
                : DeploymentResult.Fail("ETH001", "SSH connection failed; use an SSH key or ssh-agent and verify host/port/user");
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return DeploymentResult.Fail("ETH001", "SSH connection preparation failed"); }
    }

    public async Task<DeploymentResult> InstallAsync(ManagerDeploymentOptions options, CancellationToken ct = default)
    {
        ValidateManagerOptions(options);
        var workspace = Path.Combine(Path.GetTempPath(), "easytier-host-manager-" + Guid.NewGuid().ToString("N"));
        await SecretProvider.SecureDirectoryAsync(workspace, ct);
        try
        {
            var localSecret = Path.Combine(workspace, "network.secret");
            await SecretProvider.WriteAsync(localSecret, options.NetworkSecret, ct);
            var profilePath = Path.Combine(workspace, "network.json");
            var profile = BuildProfile(options);
            await ConfigurationStore.SaveAtomicAsync(profilePath, profile, ct);

            var request = CreateRequest(options, profilePath);
            return await new DeploymentService().InstallAsync(request, ct);
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch { }
        }
    }

    public async Task<DeploymentResult> UninstallAsync(ManagerDeploymentOptions options, CancellationToken ct = default)
    {
        try
        {
            ValidateRemoteOptions(options);
            var placeholderProfile = Path.Combine(Path.GetTempPath(), "easytier-host-uninstall-unused.json");
            var request = CreateRequest(options, placeholderProfile);
            return await new DeploymentService().UninstallAsync(request, ct);
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return DeploymentResult.Fail("ETH402", "Remote uninstall preparation failed"); }
    }

    public async Task<DeploymentResult> DiagnosticsAsync(ManagerDeploymentOptions options, CancellationToken ct = default)
    {
        try
        {
            ValidateRemoteOptions(options);
            var layout = RemoteLayout.For(options.OsType);
            await using var remote = RemoteExecutorFactory.Create(Credential(options));
            var result = await remote.ExecuteAsync(layout.DiagnosticsCommand(options.Username), ct);
            if (!result.Success) return DeploymentResult.Fail("ETH402", "Remote diagnostics command failed");
            var output = result.StdOut.Trim();
            return DeploymentResult.Ok(string.IsNullOrWhiteSpace(output) ? "Diagnostics completed with no output" : output);
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return DeploymentResult.Fail("ETH402", "Remote diagnostics preparation failed"); }
    }

    private static DeploymentRequest CreateRequest(ManagerDeploymentOptions options, string localProfilePath)
    {
        var layout = RemoteLayout.For(options.OsType);
        return new DeploymentRequest
        {
            OsType = options.OsType,
            Role = options.Role,
            Remote = Credential(options),
            LocalPackageDirectory = string.IsNullOrWhiteSpace(options.LocalPackageDirectory) ? "." : Path.GetFullPath(options.LocalPackageDirectory),
            RemoteInstallDirectory = layout.InstallDirectory,
            LocalProfilePath = localProfilePath,
            RemoteProfilePath = layout.ProfilePath,
            RemoteStateDirectory = layout.StateDirectory,
            ServiceName = layout.ServiceName
        };
    }

    private static RemoteHostCredential Credential(ManagerDeploymentOptions options) => new()
    {
        Host = options.RemotePhysicalIp,
        Port = options.SshPort,
        Username = options.Username,
        PrivateKeyPath = string.IsNullOrWhiteSpace(options.PrivateKeyPath) ? null : options.PrivateKeyPath
    };

    private static NetworkProfile BuildProfile(ManagerDeploymentOptions options)
    {
        var profile = new NetworkProfile
        {
            NetworkName = options.NetworkName,
            SecretFile = "network.secret",
            Role = options.Role,
            SeedPhysicalIp = options.Role == NodeRole.Seed ? null : options.SeedPhysicalIp,
            Port = options.ListenerPort,
            DedicatedIndex = options.Role == NodeRole.Dedicated ? options.DedicatedIndex : null,
            CorePath = "easytier-core",
            CliPath = "easytier-cli",
            DeviceName = "easytierhost",
            EnableInternetGateway = false
        };
        NetworkProfileValidator.Validate(profile);
        return profile;
    }

    private static void ValidateRemoteOptions(ManagerDeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IPAddress.TryParse(options.RemotePhysicalIp, out var remote) || remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || OverlayAddressPlan.IsOverlay(remote) || IPAddress.IsLoopback(remote))
            throw new HostException("ETH003", "Remote physical IP must be a non-overlay IPv4 address");
        if (options.SshPort is < 1 or > 65535) throw new HostException("ETH003", "SSH port is out of range");
        if (string.IsNullOrWhiteSpace(options.Username)) throw new HostException("ETH003", "SSH username is required");
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath) && !File.Exists(Path.GetFullPath(options.PrivateKeyPath)))
            throw new HostException("ETH003", "SSH private key file does not exist");
    }

    private static void ValidateManagerOptions(ManagerDeploymentOptions options)
    {
        ValidateRemoteOptions(options);
        if (string.IsNullOrWhiteSpace(options.NetworkName)) throw new HostException("ETH003", "Network name is required");
        if (string.IsNullOrWhiteSpace(options.NetworkSecret)) throw new HostException("ETH003", "Network secret is required");
        if (options.NetworkSecret.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new HostException("ETH003", "Network secret must be a single line");
        if (options.ListenerPort is < 1 or > 65535) throw new HostException("ETH003", "Listener port is out of range");
        if (!Directory.Exists(options.LocalPackageDirectory)) throw new HostException("ETH003", "Local package directory does not exist");
        if (options.Role is NodeRole.Gateway or NodeRole.Dedicated or NodeRole.Client)
        {
            if (string.IsNullOrWhiteSpace(options.SeedPhysicalIp)) throw new HostException("ETH003", "Seed physical IP is required for non-Seed nodes");
        }
        if (options.Role == NodeRole.Dedicated && options.DedicatedIndex is not (>= 2 and <= 10))
            throw new HostException("ETH003", "Dedicated index must be 2..10");
        if (options.Role != NodeRole.Dedicated && options.DedicatedIndex is not null)
            throw new HostException("ETH003", "Dedicated index is only valid for Dedicated role");
    }

    private sealed record RemoteLayout(string InstallDirectory, string ProfilePath, string StateDirectory, string ServiceName)
    {
        public static RemoteLayout For(ServerOsType os) => os switch
        {
            ServerOsType.Windows => new("C:/Program Files/EasyTierHost", "C:/ProgramData/EasyTierHost/config/network.json", "C:/ProgramData/EasyTierHost/state", "EasyTierHost"),
            ServerOsType.Linux => new("/opt/easytier-host", "/etc/easytier-host/network.json", "/var/lib/easytier-host", "easytier-host"),
            _ => throw new HostException("ETH003", "Unknown server OS")
        };

        public string DiagnosticsCommand(string username) =>
            InstallDirectory.StartsWith("C:/", StringComparison.OrdinalIgnoreCase)
                ? $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& '{InstallDirectory}/easytier-host.exe' diagnostics '{ProfilePath}'\""
                : $"{(username.Equals("root", StringComparison.Ordinal) ? string.Empty : "sudo -n ")}'{InstallDirectory}/easytier-host' diagnostics '{ProfilePath}'";
    }
}
