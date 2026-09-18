using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

/// <summary>
/// Role boundary for the dedicated Internet gateway at 10.10.0.1.
/// Runtime NAT/DNS/exit-node behavior is owned by the Gateway role in EasyTierHost.Service.
/// </summary>
public sealed class GatewayServerInstaller : IServiceInstaller
{
    private readonly IServiceInstaller _platformInstaller;

    public GatewayServerInstaller(IServiceInstaller platformInstaller)
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
        return request.Role == NodeRole.Gateway
            ? null
            : DeploymentResult.Fail("ETH003", "Gateway installer requires Gateway role");
    }
}
