using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Deployment;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task ExpectAsyncFailure(Func<Task> action, string message)
{
    try { await action(); }
    catch { return; }
    throw new Exception(message);
}

static void ExpectSyncFailure(Action action, string message)
{
    try { action(); }
    catch { return; }
    throw new Exception(message);
}

var credential = new RemoteHostCredential
{
    Host = "192.0.2.10",
    Username = "administrator",
    Password = "never-serialize-this"
};
var json = JsonSerializer.Serialize(credential);
Check(!json.Contains("never-serialize-this", StringComparison.Ordinal), "Remote password leaked into JSON");
Check(!credential.ToString().Contains("never-serialize-this", StringComparison.Ordinal), "Remote password leaked into ToString");

var root = Path.Combine(Path.GetTempPath(), "eth-deployment-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Directory.CreateDirectory(Path.Combine(root, "nested"));
    await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");
    await File.WriteAllTextAsync(Path.Combine(root, "nested", "b.txt"), "beta");
    var manifest = await ArtifactManifest.CreateAsync(root);
    Check(manifest.Files.Count == 2, "Unexpected artifact count");
    await manifest.ValidateAsync(root);

    var manifestPath = Path.Combine(root, "manifest.json");
    await ArtifactManifest.SaveAsync(manifestPath, manifest);
    var loaded = await ArtifactManifest.LoadAsync(manifestPath);
    Check(loaded.Files.Count == 2, "Manifest round trip changed file count");

    await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "tampered");
    await ExpectAsyncFailure(() => manifest.ValidateAsync(root), "Tampered artifact was accepted");
}
finally
{
    Directory.Delete(root, true);
}

Check(DeploymentProfileRules.NormalizeSecretRelativePath("network.secret") == "network.secret", "Simple relative secret path changed");
Check(DeploymentProfileRules.NormalizeSecretRelativePath("private\\network.secret") == "private/network.secret", "Nested secret path was not normalized");
ExpectSyncFailure(() => DeploymentProfileRules.NormalizeSecretRelativePath("../network.secret"), "Parent traversal secret path was accepted");
ExpectSyncFailure(() => DeploymentProfileRules.NormalizeSecretRelativePath("/etc/easytier/network.secret"), "Unix absolute secret path was accepted");
ExpectSyncFailure(() => DeploymentProfileRules.NormalizeSecretRelativePath("C:\\ProgramData\\network.secret"), "Windows absolute secret path was accepted");

var profileRoot = Path.Combine(Path.GetTempPath(), "eth-deployment-profile-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profileRoot);
try
{
    var profilePath = Path.Combine(profileRoot, "network.json");
    var profile = new NetworkProfile
    {
        NetworkName = "deployment-test",
        SecretFile = "network.secret",
        Role = NodeRole.Seed,
        CorePath = "easytier-core",
        CliPath = "easytier-cli"
    };
    await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(profile));
    var request = new DeploymentRequest
    {
        OsType = ServerOsType.Windows,
        Role = NodeRole.Seed,
        Remote = credential,
        LocalPackageDirectory = profileRoot,
        RemoteInstallDirectory = "C:/Program Files/EasyTierHost",
        LocalProfilePath = profilePath,
        RemoteProfilePath = "C:/ProgramData/EasyTierHost/network.json",
        RemoteStateDirectory = "C:/ProgramData/EasyTierHost/state"
    };
    var validated = await DeploymentProfileRules.ValidateAsync(request);
    Check(validated.Role == NodeRole.Seed, "Matching deployment role was changed");
    await ExpectAsyncFailure(async () => { _ = await DeploymentProfileRules.ValidateAsync(request with { Role = NodeRole.Gateway }); }, "Role/profile mismatch was accepted");

    var nonPortable = profile with { SecretFile = "C:/ProgramData/EasyTierHost/network.secret" };
    await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(nonPortable));
    await ExpectAsyncFailure(async () => { _ = await DeploymentProfileRules.ValidateAsync(request); }, "Absolute secret path was accepted for remote deployment");
}
finally
{
    Directory.Delete(profileRoot, true);
}

await using (var remote = new SshRemoteExecutor(credential))
{
    await ExpectAsyncFailure(() => remote.ExecuteAsync("echo should-not-run", CancellationToken.None), "Password authentication was passed to a child process");
}

Console.WriteLine("PASS deployment manifest, profile portability and credential safety tests");
return 0;
