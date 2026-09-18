using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Client.Windows.Services;

public sealed record ClientConnectOptions(
    string SeedPhysicalIp,
    string NetworkName,
    string? NetworkSecret,
    bool EnableInternetGateway,
    int Port = 11010);

/// <summary>
/// Local client control plane. It prepares the protected profile/secret and delegates all
/// privileged network lifecycle to the EasyTierHost Windows service.
/// </summary>
public sealed class ClientBootstrapper(
    ClientRuntimePaths paths,
    EasyTierClientService service,
    ClientGatewayCoordinator gateway)
{
    public async Task<ClientStatusSnapshot> ConnectAsync(ClientConnectOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows client UI requires Windows.");
        if (string.IsNullOrWhiteSpace(options.NetworkName)) throw new HostException("ETH003", "Network name required");

        // Never rewrite the profile or DPAPI secret while LocalSystem may be reading them.
        await service.StopAsync(ct);
        var configDirectory = Path.GetDirectoryName(paths.ProfilePath)!;
        await SecretProvider.SecureDirectoryAsync(configDirectory, ct);
        await SecretProvider.SecureDirectoryAsync(paths.StateDirectory, ct);

        if (!string.IsNullOrEmpty(options.NetworkSecret))
            await ReplaceSecretAsync(options.NetworkSecret, ct);
        else if (!File.Exists(paths.SecretPath))
            throw new HostException("ETH003", "Network secret is required for the first connection");

        var profile = NetworkProfileBuilder.CreateClient(
            options.NetworkName,
            options.SeedPhysicalIp,
            "network.secret",
            "easytier-core",
            "easytier-cli",
            options.EnableInternetGateway,
            options.Port);
        await ConfigurationStore.SaveAtomicAsync(paths.ProfilePath, profile, ct);

        await service.InstallOrStartAsync(ct);
        var overlay = await gateway.WaitForOverlayAsync(TimeSpan.FromSeconds(60), ct);
        if (!overlay.OverlayReady)
        {
            // No overlay exists to preserve; stop cleanly so Host can finish rollback/recovery.
            await service.StopAsync(CancellationToken.None);
            throw new HostException("ETH201", "Client overlay did not become ready within 60 seconds");
        }

        // Internet-gateway failure is deliberately non-fatal: the Host keeps the overlay and
        // owns rollback to the physical default route/DNS.
        return options.EnableInternetGateway
            ? await gateway.WaitForInternetGatewayAsync(TimeSpan.FromSeconds(30), ct)
            : overlay;
    }

    public Task DisconnectAsync(CancellationToken ct) => service.StopAsync(ct);

    public async Task<ClientStatusSnapshot> ReconnectAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.ProfilePath)) throw new HostException("ETH003", "Client profile is not configured");
        await service.StopAsync(ct);
        await service.StartAsync(ct);
        var state = await gateway.WaitForOverlayAsync(TimeSpan.FromSeconds(60), ct);
        return state.InternetGatewayEnabled
            ? await gateway.WaitForInternetGatewayAsync(TimeSpan.FromSeconds(30), ct)
            : state;
    }

    public async Task<NetworkProfile?> LoadProfileAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.ProfilePath)) return null;
        return await ConfigurationStore.LoadAsync(paths.ProfilePath, ct);
    }

    private async Task ReplaceSecretAsync(string secret, CancellationToken ct)
    {
        var temporary = paths.SecretPath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await SecretProvider.WriteAsync(temporary, secret, ct);
            File.Move(temporary, paths.SecretPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
