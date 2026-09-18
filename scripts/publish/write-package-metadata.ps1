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

function Get-PackageRelativePath([string] $Root, [string] $FullName) {
    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
        $rootFull += [IO.Path]::DirectorySeparatorChar
    }
    $full = [IO.Path]::GetFullPath($FullName)
    if ($full.Length -lt $rootFull.Length -or -not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside package: $FullName"
    }
    return $full.Substring($rootFull.Length).Replace('\', '/')
}

function Write-Utf8NoBomFile([string] $Path, [string] $Text) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

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

$versionLines = @(
    "EasyTierHost=$buildId"
    'EasyTierCoreBase=2.6.4'
    "HostCommit=$hostCommit"
    "PackageKind=$PackageKind"
    "RuntimeIdentifier=$RuntimeIdentifier"
)
Write-Utf8NoBomFile $versionPath (($versionLines -join "`n") + "`n")

$hashEntries = @(
    Get-ChildItem -LiteralPath $package -File -Recurse |
        Where-Object { $_.FullName -ne $shaPath -and $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = Get-PackageRelativePath $package $_.FullName
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
)
Write-Utf8NoBomFile $shaPath (($hashEntries -join "`n") + "`n")

$entries = @(
    Get-ChildItem -LiteralPath $package -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = Get-PackageRelativePath $package $_.FullName
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$manifestJson = [ordered]@{
    SchemaVersion = 1
    BuildId = $buildId
    CoreBase = '2.6.4'
    HostCommit = $hostCommit
    PackageKind = $PackageKind
    RuntimeIdentifier = $RuntimeIdentifier
    Files = $entries
} | ConvertTo-Json -Depth 5
Write-Utf8NoBomFile $manifestPath ($manifestJson.TrimEnd() + "`n")

Write-Host "Package metadata written: $package"
Write-Host "Manifest files: $($entries.Count)"
