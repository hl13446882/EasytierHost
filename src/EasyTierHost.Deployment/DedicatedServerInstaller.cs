using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

/// <summary>
/// Role boundary for static dedicated overlay servers 10.10.0.2 through 10.10.0.10.
/// These nodes never become Internet gateways, DNS forwarders, or exit nodes.
/// </summary>
public sealed class DedicatedServerInstaller : IServiceInstaller
{
    private readonly IServiceInstaller _platformInstaller;

    public DedicatedServerInstaller(IServiceInstaller platformInstaller)
    {
        _platformInstaller = platformInstaller ?? throw new ArgumentNullException(nameof(platformInstaller));
    }

    public Task<DeploymentResult> InstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        var error = Validate(request);
        return error is null
            ? _platformInstaller.InstallAsync(request, remote, ct)
            : Task.FromResult(error);
    }

    public Task<DeploymentResult> UninstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        var error = Validate(request);
        return error is null
            ? _platformInstaller.UninstallAsync(request, remote, ct)
            : Task.FromResult(error);
    }

    private static DeploymentResult? Validate(DeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Role != NodeRole.Dedicated)
            return DeploymentResult.Fail("ETH003", "Dedicated server installer requires Dedicated role");
        return null;
    }
}
