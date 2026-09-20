[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallRoot = "$env:ProgramFiles\EasyTierHost",
    [string] $ConfigDirectory = "$env:ProgramData\EasyTierHost",
    [string] $ServiceName = 'EasyTierHost',
    [string] $StateDirectory = '',
    [switch] $Force
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

function Assert-ExactOwnedPath([string] $Path, [string] $ExpectedPath) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    if (-not $full.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected path: $full (expected $expected)"
    }
    return $full
}

Assert-Administrator
if (-not $PSCmdlet.ShouldProcess($InstallRoot, 'Restore physical network and uninstall virtual network')) { return }
$InstallRoot = Assert-ExactOwnedPath $InstallRoot (Join-Path $env:ProgramFiles 'EasyTierHost')
$ConfigDirectory = Assert-ExactOwnedPath $ConfigDirectory (Join-Path $env:ProgramData 'EasyTierHost')
if (-not $StateDirectory) { $StateDirectory = Join-Path $ConfigDirectory 'state' }
$StateDirectory = Assert-ExactOwnedPath $StateDirectory (Join-Path $ConfigDirectory 'state')

if (-not $Force) {
    $answer = Read-Host 'Uninstall the local virtual network and delete program/config? Type Y to confirm'
    if ($answer -notin @('Y', 'y')) {
        Write-Host 'Cancelled.'
        return
    }
}

$serviceUninstall = Join-Path $PSScriptRoot 'uninstall-service.ps1'
if (-not (Test-Path -LiteralPath $serviceUninstall -PathType Leaf)) {
    throw 'Verified uninstaller missing; preserve state and installation.'
}
$hostPath = Join-Path $InstallRoot 'easytier-host.exe'
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    $hostPath = Join-Path $PSScriptRoot '..\..\easytier-host.exe'
}
& $serviceUninstall -ServiceName $ServiceName -StateDirectory $StateDirectory -RemoveState -HostPath $hostPath -CorePath (Join-Path $InstallRoot 'easytier-core.exe')

if (Test-Path -LiteralPath $ConfigDirectory) {
    Remove-Item -LiteralPath $ConfigDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $InstallRoot) {
    try {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }
    catch {
        Write-Host "Service and config were removed. Delete leftover files at $InstallRoot after this window closes."
        throw 'Program files remain; uninstall is incomplete.'
    }
}

Write-Host 'Virtual network uninstalled. Service, profile, secret, state and program files were removed.'
