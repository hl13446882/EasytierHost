using System.IO;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Manager;

internal static class PackageDirectoryLocator
{
    public static string Locate(string rolePrefix, ServerOsType os)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rolePrefix);
        var suffix = os == ServerOsType.Windows ? "windows" : "linux";
        var folder = $"{rolePrefix}-{suffix}";
        foreach (var candidate in Candidates(folder))
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", folder));
    }

    public static bool IsManagedPath(string path, string rolePrefix)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.EndsWith($"/{rolePrefix}-linux", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith($"/{rolePrefix}-windows", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith($"publish/{rolePrefix}-", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"{rolePrefix}-linux", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"{rolePrefix}-windows", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Candidates(string folder)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(Path.GetFullPath(start)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                TryAdd(seen, Path.Combine(dir.FullName, folder));
                TryAdd(seen, Path.Combine(dir.FullName, "publish", folder));
                TryAdd(seen, Path.Combine(dir.FullName, "publish", "release", folder));
            }
        }

        foreach (var path in seen) yield return path;
    }

    private static void TryAdd(HashSet<string> seen, string path)
    {
        try { seen.Add(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
        }
    }
}
