using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

/// <summary>
/// Creates validated role profiles without exposing network secrets in the profile itself.
/// Runtime UIs/installers should store the secret with <see cref="SecretProvider"/> and only
/// persist the protected secret-file path here.
/// </summary>
public static class NetworkProfileBuilder
{
    public static NetworkProfile CreateClient(
        string networkName,
        string seedPhysicalIp,
        string secretFile,
        string corePath,
        string cliPath,
        bool enableInternetGateway,
        int port = 11010,
        int rpcPort = 15888,
        string deviceName = "easytierhost")
    {
        var profile = new NetworkProfile
        {
            SchemaVersion = 1,
            NetworkName = networkName.Trim(),
            SecretFile = secretFile,
            Role = NodeRole.Client,
            SeedPhysicalIp = seedPhysicalIp.Trim(),
            Port = port,
            CorePath = corePath,
            CliPath = cliPath,
            RpcPort = rpcPort,
            DeviceName = deviceName,
            EnableInternetGateway = enableInternetGateway
        };

        NetworkProfileValidator.Validate(profile);
        return profile;
    }
}
