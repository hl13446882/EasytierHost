using System.Net.NetworkInformation;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

public sealed class WindowsRouteApi(CommandRunner runner) : IRouteApi
{
    internal Task<string> PowerShellAsync(string script, CancellationToken ct) => PowerShellCommand.RunAsync(runner, script, ct);
    public async Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct)
    {
        var json = await PowerShellAsync("""
            $items = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Select-Object DestinationPrefix,NextHop,InterfaceIndex,RouteMetric)
            $json = ConvertTo-Json -Compress -InputObject $items
            if ($items.Count -eq 1 -and $json.Length -gt 0 -and $json[0] -ne '[') { $json = "[$json]" }
            $json
            """, ct);
        using var doc = JsonDocument.Parse(PowerShellCommand.ReadJson(json));
        return AsArray(doc.RootElement).Select(r => new RouteEntry(
            Text(r, "DestinationPrefix"),
            Text(r, "NextHop"),
            Int(r, "InterfaceIndex"),
            Int(r, "RouteMetric")) { CreatedByEasyTierHost = false, OwnerTag = "" }).ToArray();
    }
    public async Task<RouteSnapshot> CaptureAsync(CancellationToken ct)
    {
        var json = await PowerShellAsync("""
            $physical = @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Select-Object -ExpandProperty ifIndex)
            $candidates = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceIndex -in $physical } | ForEach-Object {
                $nic = @(Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex $_.InterfaceIndex)[0]
                [pscustomobject]@{ Route=$_; Metric=[int]$nic.InterfaceMetric; Total=($_.RouteMetric + [int]$nic.InterfaceMetric) }
            } | Sort-Object Total)
            if (!$candidates.Count) { throw 'No physical default route' }
            $best=$candidates[0]; $r=$best.Route
            $ip=Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $r.InterfaceIndex | Where-Object { $_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
            if (!$ip) { throw 'No physical IPv4 address' }
            $dns=@(@((Get-DnsClientServerAddress -AddressFamily IPv4 -InterfaceIndex $r.InterfaceIndex -ErrorAction SilentlyContinue).ServerAddresses) | Where-Object { $_ })
            [pscustomobject]@{ Name=$r.InterfaceAlias; Index=[int]$r.InterfaceIndex; Ip=$ip.IPAddress; Gateway=$r.NextHop; Metric=[int]$best.Metric; Dns=$dns } | ConvertTo-Json -Compress -Depth 5
            """, ct);
        using var doc = JsonDocument.Parse(PowerShellCommand.ReadJson(json)); var x = doc.RootElement;
        var all = await ListAsync(ct);
        return new(Text(x, "Name"), Int(x, "Index"), Text(x, "Ip"), Text(x, "Gateway"), Int(x, "Metric"), all.Where(r => r.Destination == "0.0.0.0/0").ToArray(), all.Where(r => r.NextHop == "0.0.0.0").ToArray(), Strings(x, "Dns"));
    }
    private static JsonElement[] AsArray(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().ToArray(),
        JsonValueKind.Object => [element],
        _ => throw new IOException("Expected JSON array")
    };
    private static JsonElement Prop(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)) return value;
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        if (element.TryGetProperty(camel, out value)) return value;
        throw new IOException("Missing JSON property " + name);
    }
    private static string Text(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array when value.GetArrayLength() > 0 => TextFrom(value[0]),
            _ => throw new IOException("Expected string " + name)
        };
    }
    private static string TextFrom(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    private static int Int(JsonElement element, string name)
    {
        var value = Prop(element, name);
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0) value = value[0];
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        throw new IOException("Expected int " + name);
    }
    private static string[] Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) && !element.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out value))
            return [];
        if (value.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText()).Where(s => s.Length > 0).ToArray();
        return [];
    }
    private static void Validate(RouteEntry r)
    {
        var parts = r.Destination.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out _) || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32 || !System.Net.IPAddress.TryParse(r.NextHop, out _) || r.InterfaceIndex <= 0 || r.Metric < 0) throw new ArgumentException("Invalid route");
    }
    public async Task AddAsync(RouteEntry r, CancellationToken ct)
    {
        Validate(r);
        await PowerShellAsync($"New-NetRoute -DestinationPrefix '{r.Destination}' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.Metric} -PolicyStore ActiveStore | Out-Null", ct);
    }
    public async Task DeleteAsync(RouteEntry r, CancellationToken ct)
    {
        Validate(r);
        await PowerShellAsync($"Get-NetRoute -DestinationPrefix '{r.Destination}' -InterfaceIndex {r.InterfaceIndex} -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{r.NextHop}' -and $_.RouteMetric -eq {r.Metric} }} | Remove-NetRoute -Confirm:$false", ct);
    }
}

public sealed class LinuxRouteApi(CommandRunner runner) : IRouteApi
{
    public static string NormalizeDestination(string destination) => destination == "default" ? "0.0.0.0/0" : destination.Contains('/') ? destination : destination + "/32";
    private static NetworkInterface Nic(int index) => NetworkInterface.GetAllNetworkInterfaces().First(n => n.GetIPProperties().GetIPv4Properties()?.Index == index);
    private static int Index(string name) => NetworkInterface.GetAllNetworkInterfaces().First(n => n.Name == name).GetIPProperties().GetIPv4Properties().Index;
    public async Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await runner.CheckedAsync("ip", ["-j", "-4", "route", "show", "table", "main"], ct));
        return doc.RootElement.EnumerateArray().Where(r => r.TryGetProperty("dev", out _)).Select(r => new RouteEntry(
            NormalizeDestination(r.GetProperty("dst").GetString()!),
            r.TryGetProperty("gateway", out var gw) ? gw.GetString()! : "0.0.0.0", Index(r.GetProperty("dev").GetString()!),
            r.TryGetProperty("metric", out var m) ? m.GetInt32() : 0) { CreatedByEasyTierHost = false, OwnerTag = "" }).ToArray();
    }
    public async Task<RouteSnapshot> CaptureAsync(CancellationToken ct)
    {
        var routes = await ListAsync(ct);
        var candidates = routes.Where(r => r.Destination == "0.0.0.0/0" && Directory.Exists($"/sys/class/net/{Nic(r.InterfaceIndex).Name}/device")).OrderBy(r => r.Metric).ToArray();
        var route = candidates.FirstOrDefault() ?? throw new HostException("ETH301", "No physical default route");
        var nic = Nic(route.InterfaceIndex); var props = nic.GetIPProperties();
        var ip = props.UnicastAddresses.First(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !OverlayAddressPlan.IsOverlay(a.Address));
        string[] servers;
        try { servers = (await new ResolvedLink(runner).ReadAsync(route.InterfaceIndex, ct)).Servers; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { servers = []; }
        return new(nic.Name, route.InterfaceIndex, ip.Address.ToString(), route.NextHop, 0, routes.Where(r => r.Destination == "0.0.0.0/0").ToArray(), routes.Where(r => r.NextHop == "0.0.0.0").ToArray(), servers);
    }
    private Task<string> ChangeAsync(string action, RouteEntry r, CancellationToken ct)
    {
        var args = new List<string> { "-4", "route", action, r.Destination };
        if (r.NextHop != "0.0.0.0") args.AddRange(["via", r.NextHop]);
        args.AddRange(["dev", Nic(r.InterfaceIndex).Name, "metric", r.Metric.ToString(), "table", "main"]);
        return runner.CheckedAsync("ip", args, ct);
    }
    public async Task AddAsync(RouteEntry route, CancellationToken ct) => await ChangeAsync("add", route, ct);
    public async Task DeleteAsync(RouteEntry route, CancellationToken ct) => await ChangeAsync("del", route, ct);
}
