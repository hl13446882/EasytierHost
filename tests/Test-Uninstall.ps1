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
function Get-CimInstance {
    param($ClassName)
    if (-not $script:coreRemains) { return @() }
    $core = [IO.Path]::GetFullPath((Join-Path $PWD 'fixture-core.exe'))
    $config = [IO.Path]::GetFullPath((Join-Path "$env:ProgramData/EasyTierHost/state" 'core.toml'))
    ,[pscustomobject]@{ ExecutablePath = $core; CommandLine = "easytier-core.exe -c $config"; ProcessId = 4242 }
}
function Stop-Process { param($Id, [switch]$Force) $script:events.Add('stop-core') }
function Wait-Process { param($Id, $Timeout, $ErrorAction) }
function Get-NetFirewallApplicationFilter { }
function Get-NetFirewallRule { process { } }
function Remove-NetFirewallRule { process { throw 'Unexpected firewall deletion in empty fixture' } }

$ownedState = [IO.Path]::GetFullPath((Join-Path (Join-Path $env:ProgramData 'EasyTierHost') 'state'))
foreach ($case in @('success','recovery-failed','journal-remains','route-remains','core-before-recover','bad-state','whatif')) {
    $script:events = [Collections.Generic.List[string]]::new()
    $script:recoveryExit = if ($case -eq 'recovery-failed') { 1 } else { 0 }
    $script:journalRemains = $case -eq 'journal-remains'
    $script:routeRemains = $case -eq 'route-remains'
    $script:coreRemains = $case -eq 'core-before-recover'
    $failed = $false
    $stateArg = if ($case -eq 'bad-state') { "$env:ProgramData/EasyTierHost/state_backup" } else { $ownedState }
    try {
        & $scriptBlock -HostPath (Join-Path $PWD 'fixture-host.exe') -CorePath (Join-Path $PWD 'fixture-core.exe') -StateDirectory $stateArg -RemoveState -WhatIf:($case -eq 'whatif')
    }
    catch { $failed = $true }
    if ($case -eq 'success') {
        if ($failed -or ($script:events -join ',') -ne 'recover,delete-service,delete-state') { throw 'Uninstall success ordering failed' }
    } elseif ($case -eq 'core-before-recover') {
        if ($failed -or ($script:events -join ',') -ne 'stop-core,recover,delete-service,delete-state') { throw 'Core was not stopped before recover-network' }
    } elseif ($case -eq 'bad-state') {
        if (-not $failed -or $script:events.Count) { throw 'Unexpected state directory was accepted' }
    } elseif ($case -eq 'whatif') {
        if ($failed -or $script:events.Count) { throw 'WhatIf mutated state' }
    } else {
        $expected = if ($case -eq 'recovery-failed') { 'recover' } else { 'recover' }
        # journal/route failures happen after recover; core was not lingering in these cases
        if (-not $failed -or ($script:events -join ',') -ne $expected) { throw "Unsafe cleanup in $case" }
    }
    Write-Host "PASS uninstall $case"
}

# Client path guards: exact ownership only.
$client = Get-Content (Join-Path $PSScriptRoot '../scripts/windows/uninstall-client.ps1') -Raw
if ($client -match 'StartsWith') { throw 'uninstall-client still uses StartsWith path checks' }
if ($client -notmatch 'Assert-ExactOwnedPath') { throw 'uninstall-client missing exact path helper' }
Write-Host 'PASS uninstall-client exact path helpers'
