[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ServiceName = "EasyTierHost",
    [string] $StateDirectory = "$env:ProgramData\EasyTierHost\state",
    [switch] $RemoveState,
    [string] $HostPath = (Join-Path $PSScriptRoot '..\..\easytier-host.exe'),
    [string] $CorePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required.'
    }
}

function Wait-ServiceState([string] $Name, [string] $Expected, [int] $TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $text = (& "$env:SystemRoot\System32\sc.exe" query $Name 2>$null | Out-String)
        if ($text -match "STATE\s+:\s+\d+\s+$Expected") { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "Service '$Name' did not reach state $Expected within $TimeoutSeconds seconds."
}

function Assert-ExactOwnedPath([string] $Path, [string] $ExpectedPath) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    if (-not $full.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected path: $full (expected $expected)"
    }
    return $full
}

Assert-Administrator
if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Restore network and uninstall service')) { return }

$expectedState = Join-Path (Join-Path $env:ProgramData 'EasyTierHost') 'state'
$StateDirectory = Assert-ExactOwnedPath $StateDirectory $expectedState

& "$env:SystemRoot\System32\sc.exe" query $ServiceName *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Service '$ServiceName' is not installed."
}
else {
    $query = (& "$env:SystemRoot\System32\sc.exe" query $ServiceName | Out-String)
    if ($query -notmatch 'STATE\s+:\s+1\s+STOPPED') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service and allow transactional route/DNS rollback')) {
            & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Host
            if ($LASTEXITCODE -notin 0, 1062) { throw "Unable to stop service '$ServiceName'." }
            Wait-ServiceState $ServiceName 'STOPPED' 180
        }
    }
}

if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) { throw 'Recovery executable missing; preserve installation and state.' }
if (-not $CorePath) { $CorePath = Join-Path (Split-Path -Parent $HostPath) 'easytier-core.exe' }
$corePath = [IO.Path]::GetFullPath($CorePath)
$coreConfig = [IO.Path]::GetFullPath((Join-Path $StateDirectory 'core.toml'))
# Stop owned Core before recovery so it cannot rewrite virtual routes while Host restores physical networking.
foreach ($process in @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $corePath })) {
    if (-not $process.CommandLine -or $process.CommandLine.IndexOf($coreConfig, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'Another Core instance uses these program files; preserve installation.'
    }
    Stop-Process -Id $process.ProcessId -Force
    Wait-Process -Id $process.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
}

& $HostPath recover-network $StateDirectory
if ($LASTEXITCODE -ne 0) { throw 'Network recovery failed; service, state and files preserved.' }
foreach ($journal in @('route-journal.json', 'gateway-journal.json')) {
    if (Test-Path -LiteralPath (Join-Path $StateDirectory $journal)) { throw "Recovery incomplete: $journal" }
}
# Core creates these rules outside the Host journal. Match its exact binary path and group.
Get-NetFirewallApplicationFilter | Where-Object Program -eq $corePath | Get-NetFirewallRule |
    Where-Object { $_.Group -eq 'EasyTier' } | Remove-NetFirewallRule
if (@(Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'EasyTierHost:*' }).Count -gt 0) { throw 'Residual EasyTierHost DNS policy; recovery required.' }
if (@(Get-NetRoute -AddressFamily IPv4 | Where-Object { $_.NextHop -eq '10.10.0.1' -and $_.DestinationPrefix -in @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1') }).Count -gt 0) { throw 'Residual virtual default route; recovery required.' }
if (Get-Command Get-NetNat -ErrorAction SilentlyContinue) {
    if (@(Get-NetNat | Where-Object Name -like 'EasyTierHost_*').Count -gt 0) { throw 'Residual gateway NAT; recovery required.' }
}
& "$env:SystemRoot\System32\sc.exe" query $ServiceName *> $null
if ($LASTEXITCODE -eq 0) {
    & "$env:SystemRoot\System32\sc.exe" delete $ServiceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Unable to delete service '$ServiceName'." }
}

if ($RemoveState) {
    # Path already verified as the exact owned state directory above.
    if (Test-Path -LiteralPath $StateDirectory) {
        if ($PSCmdlet.ShouldProcess($StateDirectory, 'Delete EasyTierHost runtime state')) {
            Remove-Item -LiteralPath $StateDirectory -Recurse -Force
        }
    }
}

Write-Host "Uninstalled $ServiceName. Network profile and secret files were not deleted."
