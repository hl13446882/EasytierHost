using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Gateway;

public sealed class WindowsNatManager(ICommandRunner runner) : IGatewayPlatform
{
    public string Platform => "windows";
    private Task<string> RunAsync(string script, CancellationToken ct) => PowerShellCommand.RunAsync(runner, script, ct);
    private static string Q(string value) => PowerShellCommand.Quote(value);
    public async Task<GatewayPlatformSnapshot> CaptureAsync(RouteSnapshot physical, GatewayAdapter overlay, CancellationToken ct)
    {
        // WinNAT supports a single internal prefix. Never remove another application's NAT.
        var json = await RunAsync($$"""
            if (@(Get-NetNat).Count -ne 0) { throw 'Existing WinNAT configuration; refuse ownership' }
            ConvertTo-Json -Compress -InputObject @(@({{physical.PhysicalInterfaceIndex}},{{overlay.Index}}) | ForEach-Object {
                $a = Get-NetAdapter -InterfaceIndex $_ -IncludeHidden
                $i = Get-NetIPInterface -InterfaceIndex $_ -AddressFamily IPv4
                [pscustomobject]@{ Index=[int]$_; Identity=$a.InterfaceGuid.ToString(); Enabled=($i.Forwarding -eq 'Enabled') }
            })
            """, ct);
        var state = JsonSerializer.Deserialize<ForwardingState[]>(json, ConfigurationStore.Json) ?? throw new IOException("Missing forwarding state");
        if (state.Length != 2 || state.Select(x => x.Index).Distinct().Count() != 2) throw new IOException("Invalid gateway interfaces");
        var token = Guid.NewGuid().ToString("N");
        return new("EasyTierHost_" + token, token, "windows", physical, overlay, false, state);
    }
    public async Task EnableForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        foreach (var entry in s.Forwarding.Where(e => !e.Enabled))
            await RunAsync($"$a=Get-NetAdapter -InterfaceIndex {entry.Index} -IncludeHidden; if ($a.InterfaceGuid.ToString() -ne {Q(entry.Identity)}) {{ throw 'Interface changed' }}; Set-NetIPInterface -InterfaceIndex {entry.Index} -AddressFamily IPv4 -Forwarding Enabled -PolicyStore ActiveStore", ct);
    }
    public async Task RestoreForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        var errors = new List<Exception>();
        foreach (var entry in s.Forwarding.Where(e => !e.Enabled))
            try
            {
                await RunAsync($"$a=@(Get-NetAdapter -IncludeHidden | Where-Object {{ $_.InterfaceGuid.ToString() -eq {Q(entry.Identity)} }}); if ($a.Count -gt 0) {{ Set-NetIPInterface -InterfaceIndex $a[0].ifIndex -AddressFamily IPv4 -Forwarding Disabled -PolicyStore ActiveStore }}", ct);
            }
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public async Task CreateNatAsync(GatewayPlatformSnapshot s, CancellationToken ct) => await RunAsync($"if (@(Get-NetNat).Count -ne 0) {{ throw 'Existing NAT appeared' }}; New-NetNat -Name {Q(s.ResourceName)} -InternalIPInterfaceAddressPrefix '{OverlayAddressPlan.Cidr}' -ExternalIPInterfaceAddressPrefix {Q(s.Physical.PhysicalIpv4 + "/32")} | Out-Null", ct);
    public async Task RemoveNatAsync(GatewayPlatformSnapshot s, CancellationToken ct) => await RunAsync($"$n=@(Get-NetNat | Where-Object Name -eq {Q(s.ResourceName)}); if ($n.Count -gt 0) {{ if ($n[0].InternalIPInterfaceAddressPrefix -ne '{OverlayAddressPlan.Cidr}' -or $n[0].ExternalIPInterfaceAddressPrefix -ne {Q(s.Physical.PhysicalIpv4 + "/32")}) {{ throw 'NAT ownership mismatch' }}; $n | Remove-NetNat -Confirm:$false }}", ct);
    public async Task<bool> VerifyAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        var result = await RunAsync($"$n=@(Get-NetNat | Where-Object {{ $_.Name -eq {Q(s.ResourceName)} -and $_.InternalIPInterfaceAddressPrefix -eq '{OverlayAddressPlan.Cidr}' -and $_.ExternalIPInterfaceAddressPrefix -eq {Q(s.Physical.PhysicalIpv4 + "/32")} -and $_.Active }}); $ready=$n.Count -eq 1; " + string.Join("; ", s.Forwarding.Select(e => $"$a=Get-NetAdapter -InterfaceIndex {e.Index} -IncludeHidden; $i=Get-NetIPInterface -InterfaceIndex {e.Index} -AddressFamily IPv4; $ready=$ready -and ($a.InterfaceGuid.ToString() -eq {Q(e.Identity)}) -and ($i.Forwarding -eq 'Enabled')")) + "; ConvertTo-Json -Compress $ready", ct);
        return JsonSerializer.Deserialize<bool>(result);
    }
}
