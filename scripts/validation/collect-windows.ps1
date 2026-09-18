[CmdletBinding()]
param(
    [string] $HostExecutable = 'C:\Program Files\EasyTierHost\easytier-host.exe',
    [string] $ProfilePath = 'C:\ProgramData\EasyTierHost\config\network.json',
    [string] $StateDirectory = 'C:\ProgramData\EasyTierHost\state',
    [string] $OutputDirectory = (Join-Path $PWD ('validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$hostExe = [IO.Path]::GetFullPath($HostExecutable)
$profile = [IO.Path]::GetFullPath($ProfilePath)
$state = [IO.Path]::GetFullPath($StateDirectory)
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw "Host executable not found: $hostExe" }
if (-not (Test-Path -LiteralPath $profile -PathType Leaf)) { throw "Profile not found: $profile" }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$diagnosticsText = & $hostExe diagnostics $profile $state
if ($LASTEXITCODE -ne 0) { throw "EasyTierHost diagnostics failed with exit code $LASTEXITCODE" }
$diagnostics = $diagnosticsText | ConvertFrom-Json
$diagnosticsText | Set-Content -LiteralPath (Join-Path $out 'diagnostics.json') -Encoding utf8NoBOM

$profileView = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json
$seed = [string]$profileView.seedPhysicalIp

function Try-Collect {
    param([scriptblock] $Action)
    try { return & $Action }
    catch { return [pscustomobject]@{ Error = $_.Exception.GetType().Name } }
}

$service = Try-Collect {
    $s = Get-Service -Name 'EasyTierHost' -ErrorAction Stop
    [pscustomobject]@{ Name = $s.Name; Status = [string]$s.Status; StartType = [string]$s.StartType }
}
$adapters = @(Get-NetAdapter | Select-Object Name,InterfaceDescription,ifIndex,Status,MacAddress,LinkSpeed)
$addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias,InterfaceIndex,IPAddress,PrefixLength,AddressState)
$interfaces = @(Get-NetIPInterface -AddressFamily IPv4 | Select-Object InterfaceAlias,InterfaceIndex,ConnectionState,InterfaceMetric,Dhcp)
$routes = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Select-Object DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex,RouteMetric,State)
$dns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceAlias,InterfaceIndex,ServerAddresses)
$seedRouting = if ([string]::IsNullOrWhiteSpace($seed)) { $null } else { Try-Collect { Test-NetConnection $seed -DiagnoseRouting -InformationLevel Detailed } }
$internetRouting = Try-Collect { Test-NetConnection '1.1.1.1' -DiagnoseRouting -InformationLevel Detailed }
$gatewayProbe = Try-Collect { Test-NetConnection '10.10.0.1' -Port 53 -InformationLevel Detailed }

[ordered]@{
    ObservedUtc = [DateTimeOffset]::UtcNow
    ComputerName = $env:COMPUTERNAME
    Diagnostics = $diagnostics
    Service = $service
    Adapters = $adapters
    Addresses = $addresses
    Interfaces = $interfaces
    Routes = $routes
    Dns = $dns
    SeedRouting = $seedRouting
    InternetRouting = $internetRouting
    GatewayDnsTcpProbe = $gatewayProbe
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'windows-network-snapshot.json') -Encoding utf8NoBOM

Write-Host "Validation evidence written: $out"
Write-Host "Role=$($diagnostics.role) Overlay=$($diagnostics.overlayIp) GatewayState=$($diagnostics.gatewayState) PeerCount=$($diagnostics.peerCount)"
