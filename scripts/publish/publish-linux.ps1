[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CoreDirectory,
    [string] $OutputDirectory = "publish/release/client-linux",
    [ValidateSet('linux-x64','linux-arm64')] [string] $RuntimeIdentifier = 'linux-x64',
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$core = [IO.Path]::GetFullPath($CoreDirectory)
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$coreExe = Join-Path $core 'easytier-core'
$cliExe = Join-Path $core 'easytier-cli'
if (-not (Test-Path -LiteralPath $coreExe -PathType Leaf)) { throw "Missing $coreExe" }
if (-not (Test-Path -LiteralPath $cliExe -PathType Leaf)) { throw "Missing $cliExe" }

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Service/EasyTierHost.Service.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained false --nologo `
    -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item -LiteralPath $coreExe -Destination (Join-Path $out 'easytier-core') -Force
Copy-Item -LiteralPath $cliExe -Destination (Join-Path $out 'easytier-cli') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $out 'scripts/linux') | Out-Null
Copy-Item -Path (Join-Path $repo 'scripts/linux/*.sh') -Destination (Join-Path $out 'scripts/linux') -Force
Copy-Item -LiteralPath (Join-Path $repo 'config') -Destination (Join-Path $out 'config') -Recurse -Force
$guide = Join-Path $repo 'docs/DEPLOY.txt'
if (-not (Test-Path -LiteralPath $guide -PathType Leaf)) { throw "Missing $guide" }
Copy-Item -LiteralPath $guide -Destination (Join-Path $out 'DEPLOY.txt') -Force
Get-ChildItem -LiteralPath $out -Filter '*.pdb' -File -Recurse | Remove-Item -Force

# Script errors propagate. LASTEXITCODE may legitimately contain the result of an optional git probe.
& (Join-Path $PSScriptRoot 'write-package-metadata.ps1') `
    -PackageDirectory $out -RuntimeIdentifier $RuntimeIdentifier -PackageKind 'node-linux'

Write-Host "Linux package created: $out"
