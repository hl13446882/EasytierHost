[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CoreDirectory,
    [string] $OutputDirectory = "publish/release/client-windows",
    [ValidateSet('win-x64','win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [string] $Configuration = 'Release',
    [ValidateSet('client-windows','server-windows')] [string] $PackageKind
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$core = [IO.Path]::GetFullPath($CoreDirectory)
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$coreExe = Join-Path $core 'easytier-core.exe'
$cliExe = Join-Path $core 'easytier-cli.exe'
if (-not (Test-Path -LiteralPath $coreExe -PathType Leaf)) { throw "Missing $coreExe" }
if (-not (Test-Path -LiteralPath $cliExe -PathType Leaf)) { throw "Missing $cliExe" }

$nativeArch = switch ($RuntimeIdentifier) {
    'win-x64' { 'x86_64' }
    'win-arm64' { 'arm64' }
    default { throw "Unsupported Windows runtime: $RuntimeIdentifier" }
}
$nativeRoot = Join-Path $repo "EasyTier-2.6.4/easytier/third_party/$nativeArch"
$nativeFiles = @('Packet.dll', 'wintun.dll')
foreach ($native in $nativeFiles) {
    $source = Join-Path $nativeRoot $native
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing EasyTier native runtime: $source" }
}

if ([string]::IsNullOrWhiteSpace($PackageKind)) {
    $leaf = Split-Path $out -Leaf
    $PackageKind = if ($leaf -match '(?i)client') { 'client-windows' } else { 'server-windows' }
}

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Service/EasyTierHost.Service.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained false --nologo `
    -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item -LiteralPath $coreExe -Destination (Join-Path $out 'easytier-core.exe') -Force
Copy-Item -LiteralPath $cliExe -Destination (Join-Path $out 'easytier-cli.exe') -Force
foreach ($native in $nativeFiles) {
    Copy-Item -LiteralPath (Join-Path $nativeRoot $native) -Destination (Join-Path $out $native) -Force
}

$scriptDir = Join-Path $out 'scripts/windows'
New-Item -ItemType Directory -Force -Path $scriptDir | Out-Null
$scripts = @('install-service.ps1', 'uninstall-service.ps1', 'ensure-dotnet-runtime.ps1')
if ($PackageKind -eq 'client-windows') {
    $scripts += @('install-client.ps1', 'uninstall-client.ps1')
}
foreach ($script in $scripts) {
    Copy-Item -LiteralPath (Join-Path $repo "scripts/windows/$script") -Destination (Join-Path $scriptDir $script) -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'config') -Destination (Join-Path $out 'config') -Recurse -Force
$guide = Join-Path $repo 'docs/DEPLOY.txt'
if (-not (Test-Path -LiteralPath $guide -PathType Leaf)) { throw "Missing $guide" }
Copy-Item -LiteralPath $guide -Destination (Join-Path $out 'DEPLOY.txt') -Force
Get-ChildItem -LiteralPath $out -Filter '*.pdb' -File -Recurse | Remove-Item -Force

# PowerShell script failures propagate under ErrorActionPreference=Stop. Do not inspect LASTEXITCODE here:
# the metadata helper may probe optional native tools such as git and intentionally recover from failure.
& (Join-Path $PSScriptRoot 'write-package-metadata.ps1') `
    -PackageDirectory $out -RuntimeIdentifier $RuntimeIdentifier -PackageKind $PackageKind

Write-Host "Windows package created: $out ($PackageKind)"
Write-Host "Native runtime: Packet.dll, wintun.dll ($nativeArch)"
