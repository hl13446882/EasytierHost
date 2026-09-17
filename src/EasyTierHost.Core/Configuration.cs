using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Core;

public static partial class NetworkProfileValidator
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,32}$")]
    private static partial Regex DevicePattern();
    public static void Validate(NetworkProfile p)
    {
        void Require(bool ok, string reason) { if (!ok) throw new HostException("ETH003", reason); }
        Require(p.SchemaVersion == 1, "Unsupported configuration schema");
        Require(Enum.IsDefined(p.Role), "Unknown node role");
        Require(!string.IsNullOrWhiteSpace(p.NetworkName), "Network name required");
        Require(!string.IsNullOrWhiteSpace(p.SecretFile), "Secret file required");
        Require(!string.IsNullOrWhiteSpace(p.CorePath) && !string.IsNullOrWhiteSpace(p.CliPath), "Core and CLI paths required");
        Require(p.Port is > 0 and <= 65535 && p.RpcPort is > 0 and <= 65535 && p.RpcPort != p.Port, "Invalid/conflicting ports");
        Require(p.DeviceName is not null && DevicePattern().IsMatch(p.DeviceName), "Invalid TUN device name");
        if (p.Role != NodeRole.Seed)
            Require(IPAddress.TryParse(p.SeedPhysicalIp, out var seed) && seed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(seed) && !IPAddress.IsLoopback(seed) && seed.GetAddressBytes()[0] is > 0 and < 224, "Seed must be a physical unicast IPv4 address");
        Require(p.Role == NodeRole.Dedicated ? p.DedicatedIndex is >= 2 and <= 10 : p.DedicatedIndex is null, "Dedicated index must be 2..10 and only used for Dedicated");
        Require(!p.EnableInternetGateway || p.Role == NodeRole.Client, "Only clients select an Internet gateway");
        if (p.DnsUpstreams is null) throw new HostException("ETH003", "DNS upstream list cannot be null");
        foreach (var dns in p.DnsUpstreams)
            Require(IPAddress.TryParse(dns, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(ip) && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224, "DNS upstream must be a physical unicast IPv4 address");
    }
}

public static class EasyTierConfigBuilder
{
    // Keep supplementary Unicode literal: TOML rejects JSON-style surrogate escapes.
    private static string Quote(string text)
    {
        var result = new System.Text.StringBuilder("\"");
        foreach (var c in text)
            result.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ when c < 0x20 || c == 0x7f => "\\u" + ((int)c).ToString("X4"),
                _ => c.ToString()
            });
        return result.Append('"').ToString();
    }
    public static string Build(NetworkProfile p, string secret)
    {
        NetworkProfileValidator.Validate(p);
        if (string.IsNullOrWhiteSpace(secret)) throw new HostException("ETH003", "Network secret is empty");
        var lines = new List<string> { $"instance_name = {Quote("EasyTierHost")}", $"dhcp = {(p.Role == NodeRole.Client ? "true" : "false")}", $"listeners = [\"tcp://0.0.0.0:{p.Port}\", \"udp://0.0.0.0:{p.Port}\"]" };
        if (p.Role is NodeRole.Gateway or NodeRole.Dedicated)
            lines.Add($"ipv4 = \"10.10.0.{(p.Role == NodeRole.Gateway ? 1 : p.DedicatedIndex)}/16\"");
        if (p.Role == NodeRole.Client) lines.Add("exit_nodes = [\"10.10.0.1\"]");
        lines.AddRange(["", "[network_identity]", $"network_name = {Quote(p.NetworkName)}", $"network_secret = {Quote(secret)}"]);
        if (p.Role != NodeRole.Seed) lines.AddRange(["", "[[peer]]", $"uri = \"tcp://{p.SeedPhysicalIp}:{p.Port}\""]);
        if (p.Role == NodeRole.Client) lines.AddRange(["", "[dhcp_range]", "network = \"10.10.0.0/16\"", "start = \"10.10.0.11\"", "end = \"10.10.255.254\""]);
        lines.AddRange(["", "[flags]", $"dev_name = {Quote(p.DeviceName)}", $"no_tun = {(p.Role == NodeRole.Seed ? "true" : "false")}", $"enable_exit_node = {(p.Role == NodeRole.Gateway ? "true" : "false")}", "bind_device = true"]);
        return string.Join('\n', lines) + "\n";
    }
}

public static class ConfigurationStore
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static async Task<NetworkProfile> LoadAsync(string path, CancellationToken ct = default)
    {
        var profile = JsonSerializer.Deserialize<NetworkProfile>(await File.ReadAllTextAsync(path, ct), Json) ?? throw new HostException("ETH003", "Empty profile");
        NetworkProfileValidator.Validate(profile);
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return profile with { SecretFile = Path.GetFullPath(profile.SecretFile, root) };
    }
    public static async Task SaveAtomicAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
