using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

public sealed class DeploymentService
{
    public async Task<DeploymentResult> InstallAsync(DeploymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var remote = RemoteExecutorFactory.Create(request.Remote);
        if (!await remote.TestConnectionAsync(ct)) return DeploymentResult.Fail("ETH001", "SSH connection failed");
        IServiceInstaller installer = request.OsType == ServerOsType.Windows
            ? new WindowsRemoteInstaller()
            : new LinuxRemoteInstaller();
        return await installer.InstallAsync(request, remote, ct);
    }

    public async Task<DeploymentResult> UninstallAsync(DeploymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var remote = RemoteExecutorFactory.Create(request.Remote);
        if (!await remote.TestConnectionAsync(ct)) return DeploymentResult.Fail("ETH001", "SSH connection failed");
        IServiceInstaller installer = request.OsType == ServerOsType.Windows
            ? new WindowsRemoteInstaller()
            : new LinuxRemoteInstaller();
        return await installer.UninstallAsync(request, remote, ct);
    }
}

internal static class DeploymentValidation
{
    public static void Validate(DeploymentRequest request, ServerOsType os, string installerRelative)
    {
        if (request.OsType != os) throw new HostException("ETH003", "Deployment OS does not match installer");
        if (!Directory.Exists(request.LocalPackageDirectory)) throw new HostException("ETH003", "Local package directory does not exist");
        if (!File.Exists(request.LocalProfilePath)) throw new HostException("ETH003", "Local network profile does not exist");
        if (!File.Exists(Path.Combine(request.LocalPackageDirectory, installerRelative.Replace('/', Path.DirectorySeparatorChar))))
            throw new HostException("ETH003", $"Package is missing {installerRelative}");
        ValidateRemotePath(request.RemoteInstallDirectory);
        ValidateRemotePath(request.RemoteProfilePath);
        ValidateRemotePath(request.RemoteStateDirectory);
        if (string.IsNullOrWhiteSpace(request.ServiceName) || request.ServiceName.Any(char.IsWhiteSpace))
            throw new HostException("ETH003", "Service name is invalid");
        _ = PackageName(request.LocalPackageDirectory);
    }

    public static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new HostException("ETH003", "Remote path is invalid");
    }

    public static string PackageName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
            throw new HostException("ETH003", "Package directory name must not contain whitespace");
        return name;
    }

    public static string CombineRemote(string root, string relative) =>
        root.TrimEnd('/', '\\') + "/" + relative.TrimStart('/', '\\');
}
