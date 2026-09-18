[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CoreDirectory,
    [string] $OutputDirectory = "publish/client-windows",
    [ValidateSet('win-x64','win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [string] $Configuration = 'Release',
    [switch] $IncludeClientUi
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

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Service/EasyTierHost.Service.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item -LiteralPath $coreExe -Destination (Join-Path $out 'easytier-core.exe') -Force
Copy-Item -LiteralPath $cliExe -Destination (Join-Path $out 'easytier-cli.exe') -Force
foreach ($native in $nativeFiles) {
    Copy-Item -LiteralPath (Join-Path $nativeRoot $native) -Destination (Join-Path $out $native) -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $out 'scripts/windows') | Out-Null
Copy-Item -Path (Join-Path $repo 'scripts/windows/*.ps1') -Destination (Join-Path $out 'scripts/windows') -Force
Copy-Item -LiteralPath (Join-Path $repo 'config') -Destination (Join-Path $out 'config') -Recurse -Force

if ($IncludeClientUi) {
    $clientUi = Join-Path $out 'client-ui'
    & dotnet publish (Join-Path $repo 'src/EasyTierHost.Client.Windows/EasyTierHost.Client.Windows.csproj') `
        -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo -o $clientUi
    if ($LASTEXITCODE -ne 0) { throw 'Windows client UI publish failed' }
}

$kind = if ($IncludeClientUi) { 'client-windows' } else { 'server-windows' }
# PowerShell script failures propagate under ErrorActionPreference=Stop. Do not inspect LASTEXITCODE here:
# the metadata helper may probe optional native tools such as git and intentionally recover from failure.
& (Join-Path $PSScriptRoot 'write-package-metadata.ps1') `
    -PackageDirectory $out -RuntimeIdentifier $RuntimeIdentifier -PackageKind $kind

Write-Host "Windows package created: $out"
Write-Host "Native runtime: Packet.dll, wintun.dll ($nativeArch)"
if ($IncludeClientUi) { Write-Host "Client UI: client-ui/EasyTierHost.Client.Windows.exe" }
