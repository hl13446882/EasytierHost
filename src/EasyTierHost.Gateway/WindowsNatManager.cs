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
            Get-NetNat | Where-Object { $_.Name -like 'EasyTierHost_*' } | ForEach-Object { Remove-NetNat -Name $_.Name -Confirm:$false }
            if (@(Get-NetNat).Count -ne 0) { throw 'Existing WinNAT configuration; refuse ownership' }
            ConvertTo-Json -Compress -Depth 5 -InputObject @(@({{physical.PhysicalInterfaceIndex}},{{overlay.Index}}) | ForEach-Object {
                $a = @(Get-NetAdapter -InterfaceIndex $_ -IncludeHidden)[0]
                $i = @(Get-NetIPInterface -InterfaceIndex $_ -AddressFamily IPv4)[0]
                [pscustomobject]@{ Index=[int]$_; Identity=$a.InterfaceGuid.ToString(); Enabled=($i.Forwarding -eq 'Enabled') }
            })
            """, ct);
        var state = PowerShellCommand.DeserializeArray<ForwardingState>(json);
        if (state.Length != 2 || state.Select(x => x.Index).Distinct().Count() != 2) throw new IOException("Invalid gateway interfaces");
        var token = Guid.NewGuid().ToString("N");
        // Server 2019 New-NetNat rejects long names (ERROR_INSUFFICIENT_BUFFER / 122).
        return new("EasyTierHost_" + token[..8], token, "windows", physical, overlay, false, state);
    }
    public async Task EnableForwardingAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        foreach (var entry in s.Forwarding.Where(e => !e.Enabled))
            await RunAsync($"$a=@(Get-NetAdapter -InterfaceIndex {entry.Index} -IncludeHidden)[0]; if ($a.InterfaceGuid.ToString() -ne {Q(entry.Identity)}) {{ throw 'Interface changed' }}; Set-NetIPInterface -InterfaceIndex {entry.Index} -AddressFamily IPv4 -Forwarding Enabled -PolicyStore ActiveStore", ct);
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
    public async Task CreateNatAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        // Server 2019 rejects ExternalIPInterfaceAddressPrefix as error 87; internal prefix is the supported WinNAT form.
        try
        {
            await RunAsync($"Get-NetNat | Where-Object {{ $_.Name -like 'EasyTierHost_*' }} | ForEach-Object {{ Remove-NetNat -Name $_.Name -Confirm:$false }}; if (@(Get-NetNat).Count -ne 0) {{ throw 'Existing NAT appeared' }}; New-NetNat -Name {Q(s.ResourceName)} -InternalIPInterfaceAddressPrefix '{OverlayAddressPlan.Cidr}' | Out-Null", ct);
        }
        catch (Exception ex) when (ex is not HostException and not OperationCanceledException)
        {
            throw new HostException("ETH202", "New-NetNat failed");
        }
        // Do not bind -Program or Wintun InterfaceAlias: Server 2019 rejects both for this Host/TUN pair.
        try
        {
            foreach (var protocol in new[] { "TCP", "UDP" })
                await RunAsync($"New-NetFirewallRule -Name {Q(s.ResourceName + "_dns_" + protocol)} -DisplayName {Q(s.ResourceName + " DNS " + protocol)} -Description {Q("EasyTierHost:" + s.OwnerToken)} -Direction Inbound -Action Allow -Profile Any -Protocol {protocol} -LocalPort 53 -LocalAddress '{OverlayAddressPlan.Gateway}' -RemoteAddress '{OverlayAddressPlan.Cidr}' | Out-Null", ct);
        }
        catch (Exception ex) when (ex is not HostException and not OperationCanceledException)
        {
            throw new HostException("ETH202", "DNS firewall failed");
        }
    }
    public async Task RemoveNatAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        var errors = new List<Exception>();
        foreach (var protocol in new[] { "TCP", "UDP" })
            try
            {
                await RunAsync($"$r=@(Get-NetFirewallRule -Name {Q(s.ResourceName + "_dns_" + protocol)} -ErrorAction SilentlyContinue); if ($r.Count -gt 0) {{ if ($r[0].Description -ne {Q("EasyTierHost:" + s.OwnerToken)}) {{ throw 'Firewall ownership mismatch' }}; $r | Remove-NetFirewallRule }}", ct);
            }
            catch (Exception ex) { errors.Add(ex); }
        try { await RunAsync($"$n=@(Get-NetNat | Where-Object Name -eq {Q(s.ResourceName)}); if ($n.Count -gt 0) {{ if ($n[0].InternalIPInterfaceAddressPrefix -ne '{OverlayAddressPlan.Cidr}') {{ throw 'NAT ownership mismatch' }}; $n | Remove-NetNat -Confirm:$false }}", ct); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public async Task<bool> VerifyAsync(GatewayPlatformSnapshot s, CancellationToken ct)
    {
        var result = await RunAsync($"$n=@(Get-NetNat | Where-Object {{ $_.Name -eq {Q(s.ResourceName)} -and $_.InternalIPInterfaceAddressPrefix -eq '{OverlayAddressPlan.Cidr}' -and $_.Active }}); $ready=$n.Count -eq 1; " + string.Join("; ", s.Forwarding.Select(e => $"$a=@(Get-NetAdapter -InterfaceIndex {e.Index} -IncludeHidden)[0]; $i=@(Get-NetIPInterface -InterfaceIndex {e.Index} -AddressFamily IPv4)[0]; $ready=$ready -and ($a.InterfaceGuid.ToString() -eq {Q(e.Identity)}) -and ($i.Forwarding -eq 'Enabled')")) + "; ConvertTo-Json -Compress ([bool]$ready)", ct);
        if (!PowerShellCommand.Deserialize<bool>(result)) return false;
        foreach (var protocol in new[] { "TCP", "UDP" })
        {
            var exists = await RunAsync($"$rules=@(Get-NetFirewallRule -Name {Q(s.ResourceName + "_dns_" + protocol)} -ErrorAction SilentlyContinue | Where-Object {{ $_.Description -eq {Q("EasyTierHost:" + s.OwnerToken)} -and $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' }}); ConvertTo-Json -Compress ($rules.Count -eq 1)", ct);
            if (!PowerShellCommand.Deserialize<bool>(exists)) return false;
        }
        return true;
    }
}
