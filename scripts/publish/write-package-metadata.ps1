[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PackageDirectory,
    [Parameter(Mandatory = $true)] [string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)] [string] $PackageKind
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package directory not found: $package" }

[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$buildId = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($buildId)) { throw 'Directory.Build.props does not contain Version' }

$hostCommit = 'unknown'
try {
    $candidate = (& git -C $repo rev-parse HEAD 2>$null | Select-Object -First 1)
    if (-not [string]::IsNullOrWhiteSpace($candidate)) { $hostCommit = $candidate.Trim() }
}
catch { }

$versionPath = Join-Path $package 'version.txt'
$shaPath = Join-Path $package 'sha256.txt'
$manifestPath = Join-Path $package 'artifact-manifest.json'

@(
    "EasyTierHost=$buildId"
    'EasyTierCoreBase=2.6.4'
    "HostCommit=$hostCommit"
    "PackageKind=$PackageKind"
    "RuntimeIdentifier=$RuntimeIdentifier"
) | Set-Content -LiteralPath $versionPath -Encoding utf8NoBOM

$hashEntries = @(
    Get-ChildItem -LiteralPath $package -File -Recurse |
        Where-Object { $_.FullName -ne $shaPath -and $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
)
$hashEntries | Set-Content -LiteralPath $shaPath -Encoding utf8NoBOM

$entries = @(
    Get-ChildItem -LiteralPath $package -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
[ordered]@{
    SchemaVersion = 1
    BuildId = $buildId
    CoreBase = '2.6.4'
    HostCommit = $hostCommit
    PackageKind = $PackageKind
    RuntimeIdentifier = $RuntimeIdentifier
    Files = $entries
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host "Package metadata written: $package"
Write-Host "Manifest files: $($entries.Count)"
