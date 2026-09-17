using System.Net;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public static class EasyTierArgumentBuilder
{
    public static IReadOnlyList<string> Build(NetworkProfile profile, string configurationPath, CoreLaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(configurationPath)) throw new HostException("ETH003", "Core configuration path required");
        var args = new List<string>
        {
            "--config-file", configurationPath,
            "--rpc-portal", $"127.0.0.1:{profile.RpcPort}"
        };
        if (!string.IsNullOrWhiteSpace(options.UnderlaySourceIpv4))
        {
            if (!IPAddress.TryParse(options.UnderlaySourceIpv4, out var ip)
                || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || IPAddress.IsLoopback(ip)
                || OverlayAddressPlan.IsOverlay(ip)
                || ip.GetAddressBytes()[0] is 0 or >= 224)
                throw new HostException("ETH301", "Invalid physical IPv4 for underlay binding");
            args.Add("--underlay-source-ipv4");
            args.Add(ip.ToString());
        }
        return args;
    }
}
