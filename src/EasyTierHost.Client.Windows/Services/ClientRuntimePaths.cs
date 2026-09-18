namespace EasyTierHost.Client.Windows.Services;

public sealed record ClientRuntimePaths(
    string PackageRoot,
    string ProfilePath,
    string SecretPath,
    string StateDirectory,
    string HostExecutable,
    string InstallScript)
{
    public static ClientRuntimePaths Discover()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var parent = Directory.GetParent(baseDirectory)?.FullName;
        var packageRoot = File.Exists(Path.Combine(baseDirectory, "easytier-host.exe"))
            ? baseDirectory
            : parent is not null && File.Exists(Path.Combine(parent, "easytier-host.exe"))
                ? parent
                : baseDirectory;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var configDirectory = Path.Combine(programData, "EasyTierHost", "config");
        var stateDirectory = Path.Combine(programData, "EasyTierHost", "state");
        return new(
            packageRoot,
            Path.Combine(configDirectory, "network.json"),
            Path.Combine(configDirectory, "network.secret"),
            stateDirectory,
            Path.Combine(packageRoot, "easytier-host.exe"),
            Path.Combine(packageRoot, "scripts", "windows", "install-service.ps1"));
    }
}
