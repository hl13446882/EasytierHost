using System.Text;
using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

/// <summary>
/// Reads route selection metrics without changing route ownership state. This is deliberately
/// separate from RouteEntry because diagnostic interface metrics must not affect exact rollback equality.
/// </summary>
public static class RouteDiagnostics
{
    private static readonly HashSet<string> DefaultLikeDestinations =
        ["0.0.0.0/0", "0.0.0.0/1", "128.0.0.0/1"];

    public static async Task<IReadOnlyList<RouteMetricDiagnostics>> ReadAsync(
        IRouteApi routes,
        ICommandRunner runner,
        CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = """
                $wanted = @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1')
                $items = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore |
                    Where-Object { $_.DestinationPrefix -in $wanted } |
                    ForEach-Object {
                        $nic = Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex $_.InterfaceIndex
                        [pscustomobject]@{
                            Destination = $_.DestinationPrefix
                            NextHop = $_.NextHop
                            InterfaceIndex = $_.InterfaceIndex
                            RouteMetric = $_.RouteMetric
                            InterfaceMetric = $nic.InterfaceMetric
                            TotalMetric = ($_.RouteMetric + $nic.InterfaceMetric)
                        }
                    } |
                    Sort-Object Destination,TotalMetric,InterfaceIndex)
                ConvertTo-Json -Compress -InputObject $items
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script));
            var json = await runner.CheckedAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], ct);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Select(item => new RouteMetricDiagnostics(
                item.GetProperty("Destination").GetString()!,
                item.GetProperty("NextHop").GetString()!,
                item.GetProperty("InterfaceIndex").GetInt32(),
                item.GetProperty("RouteMetric").GetInt32(),
                item.GetProperty("InterfaceMetric").GetInt32(),
                item.GetProperty("TotalMetric").GetInt32())).ToArray();
        }

        var current = await routes.ListAsync(ct);
        return current
            .Where(route => DefaultLikeDestinations.Contains(route.Destination))
            .OrderBy(route => route.Destination, StringComparer.Ordinal)
            .ThenBy(route => route.Metric)
            .Select(route => new RouteMetricDiagnostics(
                route.Destination,
                route.NextHop,
                route.InterfaceIndex,
                route.Metric,
                0,
                route.Metric))
            .ToArray();
    }
}