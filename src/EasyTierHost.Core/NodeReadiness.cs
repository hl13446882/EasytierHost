using System.Net;
using System.Net.Sockets;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public sealed record NodeReadinessResult(bool Ready, string Reason, string? OverlayIp = null);

/// <summary>
/// Performs the address-level readiness checks shared by local diagnostics and remote deployment.
/// This deliberately does not claim Internet/NAT/DNS readiness; those have separate runtime health state.
/// </summary>
public static class NodeReadiness
{
    public static NodeReadinessResult Evaluate(NetworkProfile profile, IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(addresses);

        var overlay = addresses
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && OverlayAddressPlan.IsOverlay(ip))
            .Select(ip => ip.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ip => ip, StringComparer.Ordinal)
            .ToArray();

        if (profile.Role == NodeRole.Seed)
        {
            return overlay.Length == 0
                ? new(true, "Seed has no overlay TUN address")
                : new(false, "Seed must not own an overlay TUN address", overlay[0]);
        }

        if (overlay.Length != 1)
            return new(false, $"Expected exactly one overlay address, found {overlay.Length}", overlay.FirstOrDefault());

        var actual = IPAddress.Parse(overlay[0]);
        return profile.Role switch
        {
            NodeRole.Gateway when overlay[0] == OverlayAddressPlan.Gateway
                => new(true, "Gateway overlay address ready", overlay[0]),
            NodeRole.Gateway
                => new(false, $"Gateway must own {OverlayAddressPlan.Gateway}", overlay[0]),

            NodeRole.Dedicated when profile.DedicatedIndex is >= 2 and <= 10 && overlay[0] == $"10.10.0.{profile.DedicatedIndex}"
                => new(true, "Dedicated server overlay address ready", overlay[0]),
            NodeRole.Dedicated
                => new(false, $"Dedicated server must own 10.10.0.{profile.DedicatedIndex}", overlay[0]),

            NodeRole.Client when OverlayAddressPlan.IsClient(actual)
                => new(true, "Client DHCP overlay address ready", overlay[0]),
            NodeRole.Client
                => new(false, "Client overlay address is outside the DHCP client range", overlay[0]),

            _ => new(false, "Unsupported node role", overlay[0])
        };
    }
}
