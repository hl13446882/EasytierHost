using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Deployment;

public sealed class DeploymentService
{
    public async Task<DeploymentResult> InstallAsync(DeploymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var integrity = await ValidatePackageIntegrityAsync(request.LocalPackageDirectory, ct);
        if (integrity is not null) return integrity;
        try { _ = await DeploymentValidation.ValidateProfileAsync(request, ct); }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return DeploymentResult.Fail("ETH003", "Deployment profile validation failed"); }

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

    private static async Task<DeploymentResult?> ValidatePackageIntegrityAsync(string packageRoot, CancellationToken ct)
    {
        try
        {
            var root = Path.GetFullPath(packageRoot);
            var manifestPath = Path.Combine(root, "artifact-manifest.json");
            if (!File.Exists(manifestPath)) return DeploymentResult.Fail("ETH403", "Package manifest is missing");
            var manifest = await ArtifactManifest.LoadAsync(manifestPath, ct);
            await manifest.ValidateAsync(root, ct);

            var expected = manifest.Files.Select(f => f.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFullPath(f).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expected.SetEquals(actual)) return DeploymentResult.Fail("ETH403", "Package contains unmanifested or missing files");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return DeploymentResult.Fail("ETH403", "Package integrity validation failed");
        }
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
        if (!IsSafeServiceName(request.ServiceName)) throw new HostException("ETH003", "Service name is invalid");
        _ = PackageName(request.LocalPackageDirectory);
    }

    public static async Task<NetworkProfile> ValidateProfileAsync(DeploymentRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.LocalProfilePath)) throw new HostException("ETH003", "Local network profile does not exist");
        var text = await File.ReadAllTextAsync(request.LocalProfilePath, ct);
        var profile = JsonSerializer.Deserialize<NetworkProfile>(text, ConfigurationStore.Json) ?? throw new HostException("ETH003", "Empty deployment profile");
        NetworkProfileValidator.Validate(profile);
        if (profile.Role != request.Role) throw new HostException("ETH003", $"Deployment role {request.Role} does not match profile role {profile.Role}");
        _ = NormalizeSecretRelativePath(profile.SecretFile);
        ValidatePortableExecutable(profile.CorePath, "Core");
        ValidatePortableExecutable(profile.CliPath, "CLI");
        return profile;
    }

    public static string NormalizeSecretRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new HostException("ETH003", "Secret file path is invalid");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/', StringComparison.Ordinal) || LooksLikeWindowsAbsolutePath(normalized))
            throw new HostException("ETH003", "Remote deployment requires a relative secret file path");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new HostException("ETH003", "Secret file path must stay inside the remote profile directory");
        return string.Join('/', segments);
    }

    public static string ResolveRemoteSecretPath(DeploymentRequest request, string secretRelativePath)
    {
        var directory = RemoteDirectory(request.RemoteProfilePath);
        var secret = CombineRemote(directory, NormalizeSecretRelativePath(secretRelativePath));
        if (secret.Equals(request.RemoteProfilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new HostException("ETH003", "Secret file cannot overwrite the network profile");
        return secret;
    }

    public static string RemoteDirectory(string path)
    {
        ValidateRemotePath(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        if (index <= 0) throw new HostException("ETH003", "Remote profile path must include a directory");
        return normalized[..index];
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

    private static bool IsSafeServiceName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    private static void ValidatePortableExecutable(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new HostException("ETH003", $"{label} path is invalid");
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/', StringComparison.Ordinal) || LooksLikeWindowsAbsolutePath(normalized) || normalized.Split('/').Any(s => s == ".."))
            throw new HostException("ETH003", $"Remote deployment requires a portable relative {label} path");
    }

    private static bool LooksLikeWindowsAbsolutePath(string value) =>
        value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
}
