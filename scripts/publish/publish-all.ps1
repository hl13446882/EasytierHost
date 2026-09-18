[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $WindowsCoreDirectory,
    [Parameter(Mandatory = $true)] [string] $LinuxCoreDirectory,
    [ValidateSet('win-x64','win-arm64')] [string] $WindowsRuntimeIdentifier = 'win-x64',
    [ValidateSet('linux-x64','linux-arm64')] [string] $LinuxRuntimeIdentifier = 'linux-x64',
    [string] $Configuration = 'Release',
    [string] $OutputRoot = 'publish/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$windowsPublisher = Join-Path $PSScriptRoot 'publish-windows.ps1'
$linuxPublisher = Join-Path $PSScriptRoot 'publish-linux.ps1'

function Invoke-RolePublisher {
    param(
        [Parameter(Mandatory = $true)] [string] $Script,
        [Parameter(Mandatory = $true)] [string] $CoreDirectory,
        [Parameter(Mandatory = $true)] [string] $OutputDirectory,
        [Parameter(Mandatory = $true)] [string] $RuntimeIdentifier
    )

    & $Script -CoreDirectory $CoreDirectory -OutputDirectory $OutputDirectory -RuntimeIdentifier $RuntimeIdentifier -Configuration $Configuration
}

# The gateway at 10.10.0.1 is a Dedicated-class deployment package with Role=Gateway in its profile.
# Do not create a second gateway binary layout: role behavior belongs to configuration/runtime state.
foreach ($role in @('seed', 'dedicated', 'client')) {
    Invoke-RolePublisher -Script $windowsPublisher -CoreDirectory $WindowsCoreDirectory `
        -OutputDirectory "$OutputRoot/$role-windows" -RuntimeIdentifier $WindowsRuntimeIdentifier
    Invoke-RolePublisher -Script $linuxPublisher -CoreDirectory $LinuxCoreDirectory `
        -OutputDirectory "$OutputRoot/$role-linux" -RuntimeIdentifier $LinuxRuntimeIdentifier
}

$managerOutRel = "$OutputRoot/manager"
$managerOut = Join-Path $repo $managerOutRel
if (Test-Path -LiteralPath $managerOut) { Remove-Item -LiteralPath $managerOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $managerOut | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Manager/EasyTierHost.Manager.csproj') `
    -c $Configuration -r $WindowsRuntimeIdentifier --self-contained false --nologo `
    -p:DebugType=None -p:DebugSymbols=false -o $managerOut
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed' }
Get-ChildItem -LiteralPath $managerOut -Filter '*.pdb' -File -Recurse | Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $repo 'scripts/windows/ensure-dotnet-runtime.ps1') -Destination (Join-Path $managerOut 'ensure-dotnet-runtime.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs/DEPLOY.txt') -Destination (Join-Path $managerOut 'DEPLOY.txt') -Force
$managerLauncher = @(
    '@echo off'
    'setlocal'
    'set "HERE=%~dp0"'
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%ensure-dotnet-runtime.ps1" -WindowsDesktop'
    'if errorlevel 1 exit /b 1'
    'start "" "%HERE%EasyTierHost.Manager.exe"'
)
Set-Content -LiteralPath (Join-Path $managerOut 'EasyTierHost.Manager.cmd') -Value $managerLauncher -Encoding ascii

& (Join-Path $PSScriptRoot 'write-package-metadata.ps1') `
    -PackageDirectory $managerOut -RuntimeIdentifier $WindowsRuntimeIdentifier -PackageKind 'manager-windows'

Write-Host 'Canonical publish layout created:'
Write-Host "  $OutputRoot/manager"
Write-Host "  $OutputRoot/seed-windows"
Write-Host "  $OutputRoot/seed-linux"
Write-Host "  $OutputRoot/dedicated-windows   (includes Gateway role at 10.10.0.1)"
Write-Host "  $OutputRoot/dedicated-linux     (includes Gateway role at 10.10.0.1)"
Write-Host "  $OutputRoot/client-windows      (install-client.ps1 / uninstall-client.ps1)"
Write-Host "  $OutputRoot/client-linux"
