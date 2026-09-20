$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../scripts/windows/uninstall-service.ps1') -Raw
# Replace OS mutation boundaries; execute the real control flow with synthetic state.
$source = $source.Replace("`nAssert-Administrator", "`n# Administrator check stubbed for this isolated test")
$source = $source.Replace('& "$env:SystemRoot\System32\sc.exe"', 'Invoke-TestSc')
$source = $source.Replace('& $HostPath recover-network $StateDirectory', 'Invoke-TestRecovery')
if ($source.Contains('& "$env:SystemRoot\System32\sc.exe"')) { throw 'Unmocked SCM invocation' }
$scriptBlock = [scriptblock]::Create($source)
function Invoke-TestSc {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'query') { 'STATE : 1 STOPPED' }
    if ($args[0] -eq 'delete') { $script:events.Add('delete-service') }
}
function Invoke-TestRecovery { $script:events.Add('recover'); $global:LASTEXITCODE = $script:recoveryExit }
function Test-Path {
    param($LiteralPath, $PathType)
    if ($LiteralPath -like '*-journal.json') { return $script:journalRemains }
    return $true
}
function Remove-Item { param($LiteralPath, [switch]$Recurse, [switch]$Force) $script:events.Add('delete-state') }
function Get-DnsClientNrptRule { }
function Get-NetRoute { param($AddressFamily) if ($script:routeRemains) { [pscustomobject]@{ NextHop='10.10.0.1'; DestinationPrefix='0.0.0.0/1' } } }
function Get-NetNat { }
function Get-CimInstance { param($ClassName) }
function Get-NetFirewallApplicationFilter { }
function Get-NetFirewallRule { process { } }
function Remove-NetFirewallRule { process { throw 'Unexpected firewall deletion in empty fixture' } }
foreach ($case in @('success','recovery-failed','journal-remains','route-remains','whatif')) {
    $script:events = [Collections.Generic.List[string]]::new()
    $script:recoveryExit = if ($case -eq 'recovery-failed') { 1 } else { 0 }
    $script:journalRemains = $case -eq 'journal-remains'
    $script:routeRemains = $case -eq 'route-remains'
    $failed = $false
    try { & $scriptBlock -HostPath (Join-Path $PWD 'fixture-host.exe') -StateDirectory "$env:ProgramData/EasyTierHost/state" -RemoveState -WhatIf:($case -eq 'whatif') }
    catch { $failed = $true }
    if ($case -eq 'success') {
        if ($failed -or ($script:events -join ',') -ne 'recover,delete-service,delete-state') { throw 'Uninstall success ordering failed' }
    } elseif ($case -eq 'whatif') {
        if ($failed -or $script:events.Count) { throw 'WhatIf mutated state' }
    } else {
        if (-not $failed -or ($script:events -join ',') -ne 'recover') { throw "Unsafe cleanup in $case" }
    }
    Write-Host "PASS uninstall $case"
}
