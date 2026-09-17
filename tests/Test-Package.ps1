param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.buildId -ne '0.2.0-development-preview' -or $manifest.clientInternetEnabled -ne $false) { throw 'Unexpected package capabilities' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $package $file.path))
    if (!$path.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw "Hash mismatch: $($file.path)" }
}
$extension = if ($manifest.runtime -eq 'win-x64') { '.exe' } else { '' }
$hostPath = Join-Path $package "easytier-host$extension"
$help = & $hostPath help
if ($LASTEXITCODE -or ($help -join "`n") -notmatch '0\.2\.0') { throw 'Host help failed' }
foreach ($role in @('seed','gateway','dedicated','client')) {
    & $hostPath validate (Join-Path $package "templates/$role.json")
    if ($LASTEXITCODE) { throw "Invalid $role template" }
}
$coreHelp = & (Join-Path $package "easytier-core$extension") --help
if ($LASTEXITCODE) { throw 'Core help failed' }
foreach ($option in @('--dhcp-network','--dhcp-start','--dhcp-end','--underlay-source-ipv4')) {
    if (($coreHelp -join "`n") -notmatch [regex]::Escape($option)) { throw "Core missing $option" }
}
& (Join-Path $package "easytier-cli$extension") --version
if ($LASTEXITCODE) { throw 'CLI smoke test failed' }
Write-Host "PASS package smoke test and $($manifest.files.Count) SHA256 entries"
