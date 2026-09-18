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
            ValidateManagerOptions(options, requirePackage: false);
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
        ValidateManagerOptions(options, requirePackage: true);
        var workspace = Path.Combine(Path.GetTempPath(), "easytier-host-manager-" + Guid.NewGuid().ToString("N"));
        await SecretProvider.SecureDirectoryAsync(workspace, ct);
        try
        {
            var localSecret = Path.Combine(workspace, "network.secret");
            await SecretProvider.WriteAsync(localSecret, options.NetworkSecret, ct);
            var profilePath = Path.Combine(workspace, "network.json");
            var profile = BuildProfile(options);
            await ConfigurationStore.SaveAtomicAsync(profilePath, profile, ct);

            var layout = RemoteLayout.For(options.OsType);
            var request = new DeploymentRequest
            {
                OsType = options.OsType,
                Role = options.Role,
                Remote = Credential(options),
                LocalPackageDirectory = Path.GetFullPath(options.LocalPackageDirectory),
                RemoteInstallDirectory = layout.InstallDirectory,
                LocalProfilePath = profilePath,
                RemoteProfilePath = layout.ProfilePath,
                RemoteStateDirectory = layout.StateDirectory,
                ServiceName = layout.ServiceName
            };
            return await new DeploymentService().InstallAsync(request, ct);
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch { }
        }
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

    private static void ValidateManagerOptions(ManagerDeploymentOptions options, bool requirePackage)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RemotePhysicalIp)) throw new HostException("ETH003", "Remote physical IP is required");
        if (options.SshPort is < 1 or > 65535) throw new HostException("ETH003", "SSH port is out of range");
        if (string.IsNullOrWhiteSpace(options.Username)) throw new HostException("ETH003", "SSH username is required");
        if (string.IsNullOrWhiteSpace(options.NetworkName)) throw new HostException("ETH003", "Network name is required");
        if (string.IsNullOrWhiteSpace(options.NetworkSecret)) throw new HostException("ETH003", "Network secret is required");
        if (options.ListenerPort is < 1 or > 65535) throw new HostException("ETH003", "Listener port is out of range");
        if (requirePackage && !Directory.Exists(options.LocalPackageDirectory)) throw new HostException("ETH003", "Local package directory does not exist");
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
    }
}
