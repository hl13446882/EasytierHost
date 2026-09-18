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

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Service/EasyTierHost.Service.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item -LiteralPath $coreExe -Destination (Join-Path $out 'easytier-core.exe') -Force
Copy-Item -LiteralPath $cliExe -Destination (Join-Path $out 'easytier-cli.exe') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $out 'scripts/windows') | Out-Null
Copy-Item -Path (Join-Path $repo 'scripts/windows/*.ps1') -Destination (Join-Path $out 'scripts/windows') -Force
Copy-Item -LiteralPath (Join-Path $repo 'config') -Destination (Join-Path $out 'config') -Recurse -Force

if ($IncludeClientUi) {
    $clientUi = Join-Path $out 'client-ui'
    & dotnet publish (Join-Path $repo 'src/EasyTierHost.Client.Windows/EasyTierHost.Client.Windows.csproj') `
        -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo -o $clientUi
    if ($LASTEXITCODE -ne 0) { throw 'Windows client UI publish failed' }
}

# Generate the manifest last so optional UI files are covered by the same deployment integrity check.
$manifestPath = Join-Path $out 'artifact-manifest.json'
$entries = @(
    Get-ChildItem -LiteralPath $out -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\','/')
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
@{ Files = $entries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Host "Windows package created: $out"
Write-Host "Files: $($entries.Count)"
if ($IncludeClientUi) { Write-Host "Client UI: client-ui/EasyTierHost.Client.Windows.exe" }
