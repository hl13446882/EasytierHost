[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $DiagnosticsPath,
    [ValidateSet('Seed','Gateway','Dedicated','Client')] [string] $ExpectedRole,
    [int] $MinimumPeers = 1,
    [switch] $RequireInternetGateway
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$diagnostics = Get-Content -LiteralPath ([IO.Path]::GetFullPath($DiagnosticsPath)) -Raw | ConvertFrom-Json

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "VALIDATION FAILED: $Message" }
}

function IPv4-Number {
    param([string] $Address)
    $ip = [Net.IPAddress]::Parse($Address)
    $bytes = $ip.GetAddressBytes()
    Assert-True ($bytes.Length -eq 4) "IPv4 required: $Address"
    return ([uint64]$bytes[0] -shl 24) -bor ([uint64]$bytes[1] -shl 16) -bor ([uint64]$bytes[2] -shl 8) -bor [uint64]$bytes[3]
}

Assert-True ($diagnostics.role -eq $ExpectedRole) "expected role $ExpectedRole, got $($diagnostics.role)"
Assert-True ([string]::IsNullOrWhiteSpace([string]$diagnostics.captureError)) "network capture error: $($diagnostics.captureError)"
Assert-True ([string]::IsNullOrWhiteSpace([string]$diagnostics.coreError)) "Core RPC error: $($diagnostics.coreError)"
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$diagnostics.physicalIpv4)) 'physical IPv4 is missing'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$diagnostics.physicalGateway)) 'physical gateway is missing'
Assert-True ([int]$diagnostics.peerCount -ge $MinimumPeers) "peer count $($diagnostics.peerCount) is below $MinimumPeers"

switch ($ExpectedRole) {
    'Seed' {
        Assert-True ([string]::IsNullOrWhiteSpace([string]$diagnostics.overlayIp)) "Seed must not own an Overlay TUN address: $($diagnostics.overlayIp)"
        Assert-True ($diagnostics.gatewayState -eq 'NotApplicable') "unexpected Seed gateway state: $($diagnostics.gatewayState)"
    }
    'Gateway' {
        Assert-True ($diagnostics.overlayIp -eq '10.10.0.1') "Gateway must own 10.10.0.1, got $($diagnostics.overlayIp)"
        Assert-True ($diagnostics.gatewayState -eq 'GatewayReady') "Gateway server is not ready: $($diagnostics.gatewayState)"
    }
    'Dedicated' {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$diagnostics.overlayIp)) 'Dedicated Overlay IP is missing'
        $number = IPv4-Number ([string]$diagnostics.overlayIp)
        $min = IPv4-Number '10.10.0.2'
        $max = IPv4-Number '10.10.0.10'
        Assert-True ($number -ge $min -and $number -le $max) "Dedicated IP is outside 10.10.0.2-10.10.0.10: $($diagnostics.overlayIp)"
        Assert-True ($diagnostics.gatewayState -eq 'NotApplicable') "unexpected Dedicated gateway state: $($diagnostics.gatewayState)"
    }
    'Client' {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$diagnostics.overlayIp)) 'Client Overlay IP is missing'
        $number = IPv4-Number ([string]$diagnostics.overlayIp)
        $min = IPv4-Number '10.10.0.11'
        $max = IPv4-Number '10.10.255.254'
        Assert-True ($number -ge $min -and $number -le $max) "Client IP is outside DHCP pool: $($diagnostics.overlayIp)"

        if ($RequireInternetGateway) {
            Assert-True ($diagnostics.gatewayState -eq 'GatewayActive') "Client Internet gateway is not active: $($diagnostics.gatewayState)"
            $halfA = @($diagnostics.routeMetrics | Where-Object { $_.destination -eq '0.0.0.0/1' -and $_.nextHop -eq '10.10.0.1' })
            $halfB = @($diagnostics.routeMetrics | Where-Object { $_.destination -eq '128.0.0.0/1' -and $_.nextHop -eq '10.10.0.1' })
            $physicalDefault = @($diagnostics.routeMetrics | Where-Object { $_.destination -eq '0.0.0.0/0' })
            Assert-True ($halfA.Count -ge 1 -and $halfB.Count -ge 1) 'the two Overlay /1 default routes are missing'
            Assert-True ($physicalDefault.Count -ge 1) 'the original physical /0 route is missing'
            if (-not [string]::IsNullOrWhiteSpace([string]$diagnostics.seedPhysicalIp)) {
                Assert-True (@($diagnostics.protectedEndpoints) -contains [string]$diagnostics.seedPhysicalIp) "Seed /32 is not protected on the physical network: $($diagnostics.seedPhysicalIp)"
            }
        }
    }
}

Write-Host "PASS node contract: role=$ExpectedRole overlay=$($diagnostics.overlayIp) gateway=$($diagnostics.gatewayState) peers=$($diagnostics.peerCount)"
