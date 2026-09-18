using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Deployment;

/// <summary>
/// Creates a short-lived, ACL-restricted plaintext copy of the network secret for SCP transport.
/// The remote installer immediately converts it to the target platform's native secret format by
/// invoking easiertier-host set-secret over SSH. The plaintext is never added to a command line.
/// </summary>
internal sealed class DeploymentProfileMaterial : IAsyncDisposable
{
    private readonly string _temporaryDirectory;

    private DeploymentProfileMaterial(NetworkProfile profile, string secretRelativePath, string localSecretPath, string temporaryDirectory)
    {
        Profile = profile;
        SecretRelativePath = secretRelativePath;
        LocalSecretPath = localSecretPath;
        _temporaryDirectory = temporaryDirectory;
    }

    public NetworkProfile Profile { get; }
    public string SecretRelativePath { get; }
    public string LocalSecretPath { get; }

    public static async Task<DeploymentProfileMaterial> CreateAsync(DeploymentRequest request, CancellationToken ct)
    {
        var raw = await DeploymentValidation.ValidateProfileAsync(request, ct);
        var loaded = await ConfigurationStore.LoadAsync(request.LocalProfilePath, ct);
        var secret = await SecretProvider.ReadAsync(loaded.SecretFile, ct);
        if (string.IsNullOrWhiteSpace(secret)) throw new HostException("ETH003", "Network secret is empty");
        if (secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new HostException("ETH003", "Network secret must be a single line for remote provisioning");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "easytier-host-deploy-secret-" + Guid.NewGuid().ToString("N"));
        await SecretProvider.SecureDirectoryAsync(temporaryDirectory, ct);
        var localSecretPath = Path.Combine(temporaryDirectory, "network-secret.plain");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(localSecretPath, secret, ct);
            }
            else
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                };
                await using var stream = new FileStream(localSecretPath, options);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(secret.AsMemory(), ct);
            }
            return new DeploymentProfileMaterial(loaded, DeploymentValidation.NormalizeSecretRelativePath(raw.SecretFile), localSecretPath, temporaryDirectory);
        }
        catch
        {
            TryDelete(temporaryDirectory);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(_temporaryDirectory);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort; secure ACL/mode still protects an undeleted staging file */ }
    }
}

public static class DeploymentProfileRules
{
    public static string NormalizeSecretRelativePath(string path) => DeploymentValidation.NormalizeSecretRelativePath(path);
}
