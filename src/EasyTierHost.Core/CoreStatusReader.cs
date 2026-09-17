using System.Net;
using System.Text.Json;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public sealed record CoreNodeStatus(uint PeerId, Guid InstanceId, string? OverlayIp, string Version);
public sealed record CorePeerStatus(uint PeerId, string? OverlayIp, IReadOnlyList<IPAddress> Endpoints);
public sealed class CoreStatusReader(ICommandRunner runner, NetworkProfile profile)
{
    private async Task<string> QueryAsync(string command, bool verbose, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var args = new List<string> { "-p", $"127.0.0.1:{profile.RpcPort}", "--instance-name", "EasyTierHost", "-o", "json" };
        if (verbose) args.Add("-v"); args.Add(command);
        return await runner.CheckedAsync(profile.CliPath, args, timeout.Token);
    }
    public async Task<CoreNodeStatus> ReadNodeAsync(CancellationToken ct) => ParseNode(await QueryAsync("node", false, ct));
    public async Task<IReadOnlyList<CorePeerStatus>> ReadPeersAsync(CancellationToken ct) => ParsePeers(await QueryAsync("peer", true, ct));
    public static CoreNodeStatus ParseNode(string json)
    {
        // Node RPC includes the entire plaintext config. Never store or return that field.
        using var document = JsonDocument.Parse(json); var node = document.RootElement;
        if (node.ValueKind != JsonValueKind.Object) throw new HostException("ETH003", "Expected one EasyTierHost instance");
        var ipv4 = node.GetProperty("ipv4_addr").GetString()?.Split('/')[0];
        if (!IPAddress.TryParse(ipv4, out var ip) || ip.Equals(IPAddress.Any)) ipv4 = null;
        return new(node.GetProperty("peer_id").GetUInt32(), Guid.Parse(node.GetProperty("inst_id").GetString()!), ipv4, node.GetProperty("version").GetString()!);
    }
    public static IReadOnlyList<CorePeerStatus> ParsePeers(string json)
    {
        using var document = JsonDocument.Parse(json); var result = new List<CorePeerStatus>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("Invalid peer status response");
        foreach (var pair in document.RootElement.EnumerateArray())
        {
            if (!pair.TryGetProperty("route", out var route) || route.ValueKind != JsonValueKind.Object) continue;
            string? ipv4 = null;
            if (route.TryGetProperty("ipv4_addr", out var address) && address.ValueKind == JsonValueKind.Object)
            {
                var number = address.GetProperty("address").GetProperty("addr").GetUInt32();
                ipv4 = new IPAddress(new byte[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number }).ToString();
            }
            var endpoints = new HashSet<IPAddress>();
            if (pair.TryGetProperty("peer", out var peer) && peer.ValueKind == JsonValueKind.Object && peer.TryGetProperty("conns", out var connections))
                foreach (var connection in connections.EnumerateArray())
                {
                    if (connection.TryGetProperty("is_closed", out var closed) && closed.GetBoolean()) continue;
                    if (!connection.TryGetProperty("tunnel", out var tunnel) || tunnel.ValueKind != JsonValueKind.Object) continue;
                    foreach (var field in new[] { "resolved_remote_addr", "remote_addr" })
                        if (tunnel.TryGetProperty(field, out var endpoint) && endpoint.ValueKind == JsonValueKind.Object && endpoint.TryGetProperty("url", out var url)
                            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
                            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(ip) && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224)
                            endpoints.Add(ip);
                }
            result.Add(new(route.GetProperty("peer_id").GetUInt32(), ipv4, endpoints.ToArray()));
        }
        return result;
    }
}
