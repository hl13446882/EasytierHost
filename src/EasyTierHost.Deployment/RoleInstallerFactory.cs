using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

public static class RoleInstallerFactory
{
    public static IServiceInstaller Wrap(NodeRole role, IServiceInstaller platformInstaller)
    {
        ArgumentNullException.ThrowIfNull(platformInstaller);
        return role switch
        {
            NodeRole.Seed => new SeedInstaller(platformInstaller),
            NodeRole.Gateway => new GatewayServerInstaller(platformInstaller),
            NodeRole.Dedicated => new DedicatedServerInstaller(platformInstaller),
            NodeRole.Client => platformInstaller,
            _ => throw new HostException("ETH003", "Unknown deployment role")
        };
    }
}
