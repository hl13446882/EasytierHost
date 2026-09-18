[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallRoot = "$env:ProgramFiles\EasyTierHost",
    [string] $ConfigDirectory = "$env:ProgramData\EasyTierHost",
    [string] $ServiceName = 'EasyTierHost',
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

function Assert-OwnedPath([string] $Path, [string] $ParentRoot, [string] $LeafName) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($ParentRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete path outside ${ParentRoot}: $full"
    }
    if (-not ([IO.Path]::GetFileName($full)).Equals($LeafName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected directory: $full"
    }
    return $full
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

Assert-Administrator
$InstallRoot = Assert-OwnedPath $InstallRoot $env:ProgramFiles 'EasyTierHost'
$ConfigDirectory = Assert-OwnedPath $ConfigDirectory $env:ProgramData 'EasyTierHost'

if (-not $Force) {
    $answer = Read-Host 'Uninstall the local virtual network and delete program/config? Type Y to confirm'
    if ($answer -notin @('Y', 'y')) {
        Write-Host 'Cancelled.'
        return
    }
}

$serviceUninstall = Join-Path $InstallRoot 'scripts\windows\uninstall-service.ps1'
if (-not (Test-Path -LiteralPath $serviceUninstall -PathType Leaf)) {
    $serviceUninstall = Join-Path $PSScriptRoot 'uninstall-service.ps1'
}
$stateDirectory = Join-Path $ConfigDirectory 'state'
if (Test-Path -LiteralPath $serviceUninstall -PathType Leaf) {
    & $serviceUninstall -ServiceName $ServiceName -StateDirectory $stateDirectory -RemoveState
}
else {
    & "$env:SystemRoot\System32\sc.exe" query $ServiceName *> $null
    if ($LASTEXITCODE -eq 0) {
        $query = (& "$env:SystemRoot\System32\sc.exe" query $ServiceName | Out-String)
        if ($query -notmatch 'STATE\s+:\s+1\s+STOPPED') {
            & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Host
            if ($LASTEXITCODE -notin 0, 1062) { throw "Unable to stop service '$ServiceName'." }
            Wait-ServiceState $ServiceName 'STOPPED' 35
        }
        & "$env:SystemRoot\System32\sc.exe" delete $ServiceName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Unable to delete service '$ServiceName'." }
    }
}

if (Test-Path -LiteralPath $ConfigDirectory) {
    Remove-Item -LiteralPath $ConfigDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $InstallRoot) {
    try {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }
    catch {
        Write-Host "Service and config were removed. Delete leftover files at $InstallRoot after this window closes."
        Write-Host 'Virtual network uninstalled.'
        return
    }
}

Write-Host 'Virtual network uninstalled. Service, profile, secret, state and program files were removed.'
