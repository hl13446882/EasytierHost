using System.Net;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

public sealed record ResolvedLinkState(string[] Servers, DnsDomain[] Domains, bool DefaultRoute);
public sealed class ResolvedLink(ICommandRunner runner)
{
    private async Task<JsonElement> PropertyAsync(int index, string property, CancellationToken ct)
    {
        if (index <= 0) throw new ArgumentOutOfRangeException(nameof(index));
        var json = await runner.CheckedAsync("busctl", ["--json=short", "get-property", "org.freedesktop.resolve1", $"/org/freedesktop/resolve1/link/_{index}", "org.freedesktop.resolve1.Link", property], ct);
        using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("data")[0].Clone();
    }
    public async Task<ResolvedLinkState> ReadAsync(int index, CancellationToken ct)
    {
        var dns = await PropertyAsync(index, "DNS", ct);
        var domains = await PropertyAsync(index, "Domains", ct);
        var defaultRoute = await PropertyAsync(index, "DefaultRoute", ct);
        return Parse(dns, domains, defaultRoute);
    }
    public static ResolvedLinkState Parse(JsonElement dns, JsonElement domains, JsonElement defaultRoute)
    {
        var servers = dns.EnumerateArray().Select(entry =>
        {
            var bytes = entry[1].EnumerateArray().Select(b => b.GetByte()).ToArray();
            if ((entry[0].GetInt32() == 2 && bytes.Length != 4) || (entry[0].GetInt32() == 10 && bytes.Length != 16) || entry[0].GetInt32() is not (2 or 10)) throw new IOException("Invalid resolved DNS address");
            return new IPAddress(bytes).ToString();
        }).ToArray();
        return new(servers, domains.EnumerateArray().Select(d => new DnsDomain(d[0].GetString()!, d[1].GetBoolean())).ToArray(), defaultRoute.GetBoolean());
    }
}
