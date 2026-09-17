using System.Security.Cryptography;
using System.Text.Json;

namespace EasyTierHost.Deployment;

public sealed record ArtifactEntry(string Path, long Length, string Sha256);

public sealed record ArtifactManifest(IReadOnlyList<ArtifactEntry> Files)
{
    public static async Task<ArtifactManifest> CreateAsync(string root, CancellationToken ct = default)
    {
        root = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = new List<ArtifactEntry>(files.Length);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            entries.Add(new(relative, new FileInfo(file).Length, await HashAsync(file, ct)));
        }
        return new ArtifactManifest(entries);
    }

    public async Task ValidateAsync(string root, CancellationToken ct = default)
    {
        root = System.IO.Path.GetFullPath(root);
        foreach (var entry in Files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            if (!full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Manifest path escapes root: {entry.Path}");
            if (!File.Exists(full)) throw new FileNotFoundException($"Manifest file missing: {entry.Path}", full);
            var info = new FileInfo(full);
            if (info.Length != entry.Length) throw new InvalidDataException($"Length mismatch: {entry.Path}");
            var hash = await HashAsync(full, ct);
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA256 mismatch: {entry.Path}");
        }
    }

    public static async Task SaveAsync(string path, ArtifactManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public static async Task<ArtifactManifest> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ArtifactManifest>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Artifact manifest is empty");
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class ChecksumValidator
{
    public static Task ValidateAsync(string root, ArtifactManifest manifest, CancellationToken ct = default) => manifest.ValidateAsync(root, ct);
}
