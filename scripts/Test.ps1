$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    dotnet build EasyTierHost.sln --nologo
    if ($LASTEXITCODE) { throw 'Host build failed' }
    dotnet run --project tests/EasyTierHost.UnitTests --no-build
    if ($LASTEXITCODE) { throw 'Host tests failed' }
    & (Join-Path $workspace 'tests/Test-Configuration.ps1')
    Push-Location (Join-Path $workspace 'EasyTier-2.6.4')
    try {
        if (Test-Path -LiteralPath 'C:\Program Files\7-Zip\7z.exe') { $env:PATH = 'C:\Program Files\7-Zip;' + $env:PATH }
        $env:PATH = (Join-Path $workspace 'EasyTier-2.6.4/easytier/third_party/x86_64') + ';' + $env:PATH
        cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features
        if ($LASTEXITCODE) { throw 'DHCP tests failed' }
    } finally { Pop-Location }
} finally { Pop-Location }
