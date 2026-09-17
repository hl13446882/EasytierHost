using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Deployment;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task ExpectFailure(Func<Task> action, string message)
{
    try { await action(); }
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
    await ExpectFailure(() => manifest.ValidateAsync(root), "Tampered artifact was accepted");
}
finally
{
    Directory.Delete(root, true);
}

await using (var remote = new SshRemoteExecutor(credential))
{
    await ExpectFailure(() => remote.ExecuteAsync("echo should-not-run", CancellationToken.None), "Password authentication was passed to a child process");
}

Console.WriteLine("PASS deployment manifest and credential safety tests");
return 0;
