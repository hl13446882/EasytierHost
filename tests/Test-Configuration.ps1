param([string]$CorePath = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $workspace ('tests/.configuration-' + [Guid]::NewGuid().ToString('N'))
$hostDll = Join-Path $workspace 'src/EasyTierHost.Service/bin/Debug/net10.0/easytier-host.dll'
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    # Synthetic fixture, never a real network credential.
    'fixture-secret' | dotnet $hostDll set-secret (Join-Path $testDirectory 'network.secret')
    if ($LASTEXITCODE) { throw 'Secret fixture failed' }
    foreach ($role in @('seed','client','dedicated','gateway')) {
        $profile = Join-Path $testDirectory "$role.json"
        Copy-Item -LiteralPath (Join-Path $workspace "config/templates/$role.json") -Destination $profile
        dotnet $hostDll configure $profile (Join-Path $testDirectory "$role.toml")
        if ($LASTEXITCODE) { throw "$role generation failed" }
        if ($CorePath) {
            & $CorePath --check-config --config-file (Join-Path $testDirectory "$role.toml") | Out-Null
            if ($LASTEXITCODE) { throw "$role Core config validation failed" }
        }
    }
    @'
import sys, tomllib
from pathlib import Path
root = Path(sys.argv[1])
for role in ('seed', 'client', 'dedicated', 'gateway'):
    with (root / (role + '.toml')).open('rb') as stream:
        c = tomllib.load(stream)
    assert c['network_identity']['network_secret'] == 'fixture-secret'
    if role == 'seed':
        assert c['flags']['no_tun'] and 'ipv4' not in c and 'peer' not in c
    elif role == 'client':
        assert c['dhcp'] and c['dhcp_range'] == dict(network='10.10.0.0/16', start='10.10.0.11', end='10.10.255.254')
    elif role == 'dedicated':
        assert c['ipv4'] == '10.10.0.3/16' and not c['flags']['enable_exit_node']
    else:
        assert c['ipv4'] == '10.10.0.1/16' and c['flags']['enable_exit_node']
print('PASS all four generated TOML profiles parsed independently')
'@ | python - $testDirectory
    if ($LASTEXITCODE) { throw 'TOML verification failed' }
} finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    $allowed = [IO.Path]::GetFullPath((Join-Path $workspace 'tests')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup path outside tests' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
