using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Gateway;

public sealed partial class LinuxNatManager(ICommandRunner runner) : IGatewayPlatform
{
    public string Platform => "linux";
    [GeneratedRegex("^[a-zA-Z0-9_.:-]{1,64}$")]
    private static partial Regex Identifier();
    private static string Name(string value) => Identifier().IsMatch(value) ? value : throw new HostException("ETH003", "Unsupported network interface/resource name");
    private static bool IsIptables(GatewayPlatformSnapshot s) => s.ResourceName.StartsWith("ipt_", StringComparison.Ordinal);
    private static string Marker(GatewayPlatformSnapshot s) => "EasyTierHost:" + Name(s.OwnerToken);

    private async Task<bool> AvailableAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        try { return (await runner.RunAsync(executable, args, ct)).ExitCode == 0; }
        catch (Win32Exception) { return false; }
    }

    public async Task<GatewayPlatformSnapshot> CaptureAsync(RouteSnapshot physical, GatewayAdapter overlay, CancellationToken ct)
    {
        Name(physical.PhysicalInterfaceName); Name(overlay.Name);
        var nft = await AvailableAsync("nft", ["-j", "list", "tables"], ct);
        var iptables = nft || await AvailableAsync("iptables", ["-t", "nat", "-S", "POSTROUTING"], ct);
        if (!nft && !iptables) throw new HostException("ETH003", "Linux gateway requires nftables or iptables");

        var forwarding = (await runner.CheckedAsync("sysctl", ["-n", "net.ipv4.ip_forward"], ct)).Trim();
        if (forwarding is not ("0" or "1")) throw new IOException("Invalid forwarding setting");
        var token = Guid.NewGuid().ToString("N");
        return new((nft ? "nft_" : "ipt_") + token, token, "linux", physical, overlay, forwarding == "1", []);
    }

    public async Task EnableForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        if (!s.GlobalForwardingEnabled) await runner.CheckedAsync("sysctl", ["-w", "net.ipv4.ip_forward=1"], ct);
    }

    public async Task RestoreForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        if (!s.GlobalForwardingEnabled) await runner.CheckedAsync("sysctl", ["-w", "net.ipv4.ip_forward=0"], ct);
    }

    public static string NatScript(GatewayPlatformSnapshot s)
    {
        var table = Name(s.ResourceName); var wan = Name(s.Physical.PhysicalInterfaceName);
        return $$"""
            add table ip {{table}} { comment "EasyTierHost:{{Name(s.OwnerToken)}}"; }
            add chain ip {{table}} postrouting { type nat hook postrouting priority srcnat; policy accept; }
            add rule ip {{table}} postrouting ip saddr {{OverlayAddressPlan.Cidr}} oifname "{{wan}}" masquerade comment "EasyTierHost:{{Name(s.OwnerToken)}}"
            """;
    }

    public static string[] IptablesRuleArguments(GatewayPlatformSnapshot s, string action)
    {
        if (action is not ("-A" or "-C" or "-D")) throw new ArgumentException("Unsupported iptables action", nameof(action));
        var wan = Name(s.Physical.PhysicalInterfaceName);
        return ["-t", "nat", action, "POSTROUTING", "-s", OverlayAddressPlan.Cidr, "-o", wan,
            "-m", "comment", "--comment", Marker(s), "-j", "MASQUERADE"];
    }

    public async Task CreateNatAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        if (IsIptables(s))
        {
            if (await IptablesRuleExistsAsync(s, ct)) throw new IOException("NAT rule already exists");
            await runner.CheckedAsync("iptables", IptablesRuleArguments(s, "-A"), ct);
            return;
        }

        if (await TableExistsAsync(s, ct)) throw new IOException("NAT table already exists");
        // One nft transaction; never flush the system ruleset or touch another table.
        await runner.CheckedAsync("nft", ["-f", "-"], ct, NatScript(s));
    }

    private async Task<bool> IptablesRuleExistsAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        try { return (await runner.RunAsync("iptables", IptablesRuleArguments(s, "-C"), ct)).ExitCode == 0; }
        catch (Win32Exception) { return false; }
    }

    private async Task<bool> TableExistsAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await runner.CheckedAsync("nft", ["-j", "list", "tables"], ct));
        return json.RootElement.GetProperty("nftables").EnumerateArray().Any(e => e.TryGetProperty("table", out var t) && t.GetProperty("family").GetString() == "ip" && t.GetProperty("name").GetString() == s.ResourceName);
    }

    private async Task<bool> OwnedAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await runner.CheckedAsync("nft", ["-j", "list", "table", "ip", Name(s.ResourceName)], ct));
        return json.RootElement.GetProperty("nftables").EnumerateArray().Any(e => e.TryGetProperty("table", out var t) && t.TryGetProperty("comment", out var comment) && comment.GetString() == Marker(s));
    }

    public async Task RemoveNatAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        if (IsIptables(s))
        {
            if (!await IptablesRuleExistsAsync(s, ct)) return;
            await runner.CheckedAsync("iptables", IptablesRuleArguments(s, "-D"), ct);
            return;
        }

        if (!await TableExistsAsync(s, ct)) return;
        if (!await OwnedAsync(s, ct)) throw new IOException("NAT table ownership mismatch");
        await runner.CheckedAsync("nft", ["delete", "table", "ip", Name(s.ResourceName)], ct);
    }

    public async Task<bool> VerifyAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        if ((await runner.CheckedAsync("sysctl", ["-n", "net.ipv4.ip_forward"], ct)).Trim() != "1") return false;
        if (IsIptables(s)) return await IptablesRuleExistsAsync(s, ct);
        if (!await TableExistsAsync(s, ct) || !await OwnedAsync(s, ct)) return false;
        using var json = JsonDocument.Parse(await runner.CheckedAsync("nft", ["-j", "list", "table", "ip", Name(s.ResourceName)], ct));
        return HasExpectedRule(json.RootElement, s);
    }

    public static bool HasExpectedRule(JsonElement json, GatewayPlatformSnapshot s)
    {
        foreach (var item in json.GetProperty("nftables").EnumerateArray())
        {
            if (!item.TryGetProperty("rule", out var rule) || rule.GetProperty("chain").GetString() != "postrouting" || !rule.TryGetProperty("comment", out var comment) || comment.GetString() != Marker(s)) continue;
            bool nat = false, source = false, wan = false;
            foreach (var expr in rule.GetProperty("expr").EnumerateArray())
            {
                if (expr.TryGetProperty("masquerade", out _)) nat = true;
                if (!expr.TryGetProperty("match", out var match) || match.GetProperty("op").GetString() != "==") continue;
                var left = match.GetProperty("left"); var right = match.GetProperty("right");
                if (left.TryGetProperty("meta", out var meta) && meta.GetProperty("key").GetString() == "oifname" && right.ValueKind == JsonValueKind.String && right.GetString() == s.Physical.PhysicalInterfaceName) wan = true;
                if (left.TryGetProperty("payload", out var payload) && payload.TryGetProperty("protocol", out var protocol) && protocol.GetString() == "ip" && payload.GetProperty("field").GetString() == "saddr" && right.ValueKind == JsonValueKind.Object && right.TryGetProperty("prefix", out var prefix) && prefix.GetProperty("addr").GetString() == "10.10.0.0" && prefix.GetProperty("len").GetInt32() == 16) source = true;
            }
            if (nat && source && wan) return true;
        }
        return false;
    }
}
