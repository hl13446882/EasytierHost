[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PackageDirectory,
    [string] $ExpectedPackageKind
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$manifestPath = Join-Path $package 'artifact-manifest.json'
$shaPath = Join-Path $package 'sha256.txt'
$versionPath = Join-Path $package 'version.txt'
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package directory not found: $package" }
foreach ($requiredMetadata in @($manifestPath, $shaPath, $versionPath)) {
    if (-not (Test-Path -LiteralPath $requiredMetadata -PathType Leaf)) { throw "Missing package metadata: $requiredMetadata" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1) { throw "Unsupported manifest schema: $($manifest.SchemaVersion)" }
if ([string]::IsNullOrWhiteSpace([string]$manifest.BuildId)) { throw 'Manifest BuildId is empty' }
if ([string]::IsNullOrWhiteSpace([string]$manifest.PackageKind)) { throw 'Manifest PackageKind is empty' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageKind) -and $manifest.PackageKind -ne $ExpectedPackageKind) {
    throw "Package kind mismatch: expected $ExpectedPackageKind, got $($manifest.PackageKind)"
}

$entries = @($manifest.Files)
$byPath = @{}
foreach ($entry in $entries) {
    $relative = [string]$entry.Path
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('\')) {
        throw "Unsafe/non-canonical manifest path: $relative"
    }
    $segments = $relative.Split('/')
    if ($segments -contains '..' -or $segments -contains '.') { throw "Traversal path in manifest: $relative" }
    $key = $relative.ToLowerInvariant()
    if ($byPath.ContainsKey($key)) { throw "Duplicate manifest path: $relative" }
    $byPath[$key] = $entry
}

$actual = @(
    Get-ChildItem -LiteralPath $package -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        ForEach-Object {
            [pscustomobject]@{
                FullName = $_.FullName
                Path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')
                Length = $_.Length
            }
        }
)
if ($actual.Count -ne $entries.Count) { throw "Manifest file count mismatch: manifest=$($entries.Count), actual=$($actual.Count)" }
foreach ($file in $actual) {
    $key = $file.Path.ToLowerInvariant()
    if (-not $byPath.ContainsKey($key)) { throw "File missing from manifest: $($file.Path)" }
    $entry = $byPath[$key]
    if ([int64]$entry.Length -ne [int64]$file.Length) { throw "Length mismatch: $($file.Path)" }
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$entry.Sha256).ToLowerInvariant()) { throw "SHA256 mismatch: $($file.Path)" }
    if ($file.Path.EndsWith('.secret', [StringComparison]::OrdinalIgnoreCase) -or $file.Path.Equals('core.toml', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Secret-bearing runtime file must not be packaged: $($file.Path)"
    }
}

$shaEntries = @{}
foreach ($line in Get-Content -LiteralPath $shaPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Invalid sha256.txt line: $line" }
    $relative = $Matches[2]
    if ($relative -in @('sha256.txt','artifact-manifest.json')) { throw "sha256.txt must not hash itself/manifest: $relative" }
    $key = $relative.ToLowerInvariant()
    if ($shaEntries.ContainsKey($key)) { throw "Duplicate sha256 entry: $relative" }
    $shaEntries[$key] = $Matches[1].ToLowerInvariant()
}
foreach ($file in $actual | Where-Object Path -ne 'sha256.txt') {
    $key = $file.Path.ToLowerInvariant()
    if (-not $shaEntries.ContainsKey($key)) { throw "sha256.txt missing entry: $($file.Path)" }
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($shaEntries[$key] -ne $actualHash) { throw "sha256.txt mismatch: $($file.Path)" }
}
if ($shaEntries.Count -ne ($actual.Count - 1)) { throw 'sha256.txt contains unexpected entries' }

$windowsRuntime = @('Packet.dll','wintun.dll')
$required = switch ([string]$manifest.PackageKind) {
    'manager-windows' { @('EasyTierHost.Manager.exe','version.txt','sha256.txt') }
    'client-windows' { @('easytier-host.exe','easytier-core.exe','easytier-cli.exe') + $windowsRuntime + @('scripts/windows/install-service.ps1','scripts/windows/uninstall-service.ps1','client-ui/EasyTierHost.Client.Windows.exe','version.txt','sha256.txt') }
    'server-windows' { @('easytier-host.exe','easytier-core.exe','easytier-cli.exe') + $windowsRuntime + @('scripts/windows/install-service.ps1','scripts/windows/uninstall-service.ps1','version.txt','sha256.txt') }
    'node-linux' { @('easytier-host','easytier-core','easytier-cli','scripts/linux/install-service.sh','scripts/linux/uninstall-service.sh','version.txt','sha256.txt') }
    default { throw "Unknown package kind: $($manifest.PackageKind)" }
}
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) { throw "Required package file missing: $relative" }
}

Write-Host "PASS package integrity: $($manifest.PackageKind) $($manifest.BuildId), files=$($entries.Count)"
