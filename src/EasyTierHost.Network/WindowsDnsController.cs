using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

/// <summary>A uniquely tagged NRPT rule overrides DNS while leaving DHCP/static adapter DNS intact.</summary>
public sealed class WindowsDnsController(ICommandRunner runner) : IDnsController
{
    private Task<string> RunAsync(string script, CancellationToken ct) => PowerShellCommand.RunAsync(runner, script, ct);
    private static string Token(RouteSnapshot snapshot) => snapshot.DnsState is { Platform: "windows-nrpt" } state && Guid.TryParseExact(state.OwnerToken, "N", out _) ? PowerShellCommand.Quote("EasyTierHost:" + state.OwnerToken) : throw new HostException("ETH302", "Missing Windows DNS ownership snapshot");
    public async Task<RouteSnapshot> CaptureAsync(RouteSnapshot snapshot, GatewayContext context, CancellationToken ct)
    {
        await RunAsync("if (@(Get-DnsClientNrptRule).Count -gt 0 -or @(Get-DnsClientNrptPolicy -Effective).Count -gt 0) { throw 'Existing NRPT policy; refuse override' }", ct);
        return snapshot with { DnsState = new("windows-nrpt", context.OverlayInterfaceIndex, "", [], [], false, Guid.NewGuid().ToString("N")) };
    }
    public async Task ApplyAsync(RouteSnapshot snapshot, CancellationToken ct)
    {
        var token = Token(snapshot);
        await RunAsync($"if (@(Get-DnsClientNrptRule).Count -gt 0 -or @(Get-DnsClientNrptPolicy -Effective).Count -gt 0) {{ throw 'NRPT policy changed' }}; Add-DnsClientNrptRule -Namespace '.' -NameServers '{OverlayAddressPlan.Gateway}' -Comment {token} -DisplayName {token}; Clear-DnsClientCache", ct);
    }
    public async Task RestoreAsync(RouteSnapshot snapshot, CancellationToken ct)
    {
        var token = Token(snapshot);
        await RunAsync($"$rules=@(Get-DnsClientNrptRule | Where-Object Comment -eq {token}); foreach ($r in $rules) {{ if (@($r.Namespace).Count -ne 1 -or @($r.Namespace)[0] -ne '.' -or @($r.NameServers).Count -ne 1 -or @($r.NameServers)[0] -ne '{OverlayAddressPlan.Gateway}') {{ throw 'Owned DNS rule changed; preserve for review' }}; Remove-DnsClientNrptRule -Name $r.Name -Force }}; if (@(Get-DnsClientNrptRule | Where-Object Comment -eq {token}).Count -gt 0) {{ throw 'DNS rollback incomplete' }}; Clear-DnsClientCache", ct);
    }
}
