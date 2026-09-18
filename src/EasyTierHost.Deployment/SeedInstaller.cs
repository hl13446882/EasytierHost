using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

/// <summary>
/// Role boundary for Seed deployments. Platform-specific transport/service work stays in the
/// Windows/Linux installer while this guard prevents an accidental role crossover.
/// </summary>
public sealed class SeedInstaller : IServiceInstaller
{
    private readonly IServiceInstaller _platformInstaller;

    public SeedInstaller(IServiceInstaller platformInstaller)
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
        return request.Role == NodeRole.Seed
            ? null
            : DeploymentResult.Fail("ETH003", "Seed installer requires Seed role");
    }
}
