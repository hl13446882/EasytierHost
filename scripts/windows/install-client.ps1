[CmdletBinding()]
param(
    [string] $SeedPhysicalIp,
    [string] $NetworkName = 'company-overlay',
    [int] $ListenerPort = 11010,
    [string] $InstallRoot = "$env:ProgramFiles\EasyTierHost",
    [string] $PackageRoot,
    [string] $ProfilePath = "$env:ProgramData\EasyTierHost\config\network.json",
    [string] $SecretPath = "$env:ProgramData\EasyTierHost\config\network.secret",
    [string] $StateDirectory = "$env:ProgramData\EasyTierHost\state",
    [string] $ServiceName = 'EasyTierHost',
    [string] $SecretFromFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:pipedSecret = $null
if ([string]::IsNullOrWhiteSpace($SecretFromFile) -and $MyInvocation.ExpectingInput) {
    foreach ($item in $input) {
        $line = ([string]$item).TrimEnd("`r")
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $script:pipedSecret = $line
            break
        }
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required.'
    }
}

function Wait-ServiceState([string] $Name, [string] $Expected, [int] $TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $text = (& "$env:SystemRoot\System32\sc.exe" query $Name 2>$null | Out-String)
        if ($text -match "STATE\s+:\s+\d+\s+$Expected") { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "Service '$Name' did not reach state $Expected within $TimeoutSeconds seconds."
}

function Test-PhysicalSeedIp([string] $Value) {
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref]$parsed)) { return $false }
    if ($parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $parsed.GetAddressBytes()
    if ($bytes[0] -eq 127 -or $bytes[0] -eq 0 -or $bytes[0] -ge 224) { return $false }
    if ($bytes[0] -eq 10 -and $bytes[1] -eq 10) { return $false }
    return $true
}

function Get-SecretLine {
    if (-not [string]::IsNullOrWhiteSpace($SecretFromFile)) {
        if (-not (Test-Path -LiteralPath $SecretFromFile -PathType Leaf)) {
            throw "Secret file does not exist: $SecretFromFile"
        }
        $line = @(Get-Content -LiteralPath $SecretFromFile -TotalCount 1)
        if ($line.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$line[0])) {
            throw 'Secret file is empty.'
        }
        return ([string]$line[0]).TrimEnd("`r")
    }

    if (-not [string]::IsNullOrWhiteSpace($script:pipedSecret)) {
        return $script:pipedSecret
    }

    if ([Console]::IsInputRedirected) {
        $line = [Console]::In.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'Network secret on stdin is empty.' }
        return $line.TrimEnd("`r")
    }

    $secure = Read-Host 'Network secret' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $line = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'Network secret is empty.' }
        return $line
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Save-Secret([string] $HostExe, [string] $Path, [string] $Secret) {
    if ($Secret.IndexOfAny(@("`r", "`n", [char]0)) -ge 0) {
        throw 'Network secret must be a single line.'
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $HostExe
    # Windows PowerShell 5.1 ProcessStartInfo has no ArgumentList.
    $psi.Arguments = 'set-secret "' + ($Path.Replace('"', '\"')) + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($psi)
    if ($null -eq $process) { throw 'Unable to start easytier-host set-secret.' }
    try {
        $process.StandardInput.WriteLine($Secret)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "set-secret failed (exit $($process.ExitCode))."
        }
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout.TrimEnd() }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr.TrimEnd() }
    }
    finally {
        $process.Dispose()
    }
}

function Stop-ExistingService([string] $Name) {
    & "$env:SystemRoot\System32\sc.exe" query $Name *> $null
    if ($LASTEXITCODE -ne 0) { return }
    $query = (& "$env:SystemRoot\System32\sc.exe" query $Name | Out-String)
    if ($query -match 'STATE\s+:\s+1\s+STOPPED') { return }
    & "$env:SystemRoot\System32\sc.exe" stop $Name | Out-Host
    if ($LASTEXITCODE -notin 0, 1062) { throw "Unable to stop existing service '$Name'." }
    Wait-ServiceState $Name 'STOPPED' 35
}

function Copy-Package([string] $Source, [string] $Destination) {
    $from = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $to = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ($from.Equals($to, [StringComparison]::OrdinalIgnoreCase)) { return }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
}

function Invoke-HostReady([string] $HostExe, [string] $Profile) {
    # PowerShell 5.1 turns native stderr into a terminating NativeCommandError when
    # $ErrorActionPreference is Stop. "Not ready" during DHCP wait is expected.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $HostExe ready $Profile 2>&1
        $text = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Text     = $text.Trim()
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

Assert-Administrator
if ([string]::IsNullOrWhiteSpace($SeedPhysicalIp)) {
    $SeedPhysicalIp = Read-Host 'Seed physical IP'
}
$SeedPhysicalIp = $SeedPhysicalIp.Trim()
if (-not (Test-PhysicalSeedIp $SeedPhysicalIp)) {
    throw 'SeedPhysicalIp must be a non-overlay unicast IPv4 address.'
}
if ($NetworkName -notmatch '^[A-Za-z0-9_-]{1,64}$') {
    throw "network name must contain only letters, digits, '_' or '-'."
}
if ($ListenerPort -lt 1 -or $ListenerPort -gt 65535) {
    throw 'ListenerPort must be 1..65535.'
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..\..'))
}
else {
    $PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$ProfilePath = [IO.Path]::GetFullPath($ProfilePath)
$SecretPath = [IO.Path]::GetFullPath($SecretPath)
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$configDir = Split-Path -Parent $ProfilePath
$packageHost = Join-Path $PackageRoot 'easytier-host.exe'
if (-not (Test-Path -LiteralPath $packageHost -PathType Leaf)) {
    throw "Missing package executable: $packageHost"
}

Stop-ExistingService $ServiceName
Copy-Package $PackageRoot $InstallRoot

$hostExe = Join-Path $InstallRoot 'easytier-host.exe'
$installScript = Join-Path $InstallRoot 'scripts\windows\install-service.ps1'
$ensureRuntime = Join-Path $InstallRoot 'scripts\windows\ensure-dotnet-runtime.ps1'
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw "Missing service executable: $hostExe" }
if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) { throw "Missing installer: $installScript" }
if (Test-Path -LiteralPath $ensureRuntime -PathType Leaf) {
    & $ensureRuntime -PackageRoot $InstallRoot
}

New-Item -ItemType Directory -Force -Path $configDir, $StateDirectory | Out-Null
& "$env:SystemRoot\System32\icacls.exe" $configDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Failed to secure the client config directory.' }

$hasSecret = Test-Path -LiteralPath $SecretPath -PathType Leaf
if (-not $hasSecret -or -not [string]::IsNullOrWhiteSpace($SecretFromFile)) {
    $secret = Get-SecretLine
    try { Save-Secret $hostExe $SecretPath $secret }
    finally { $secret = $null }
}

$profileJson = @"
{
  "schemaVersion": 1,
  "networkName": "$NetworkName",
  "secretFile": "network.secret",
  "role": "Client",
  "seedPhysicalIp": "$SeedPhysicalIp",
  "port": $ListenerPort,
  "corePath": "easytier-core",
  "cliPath": "easytier-cli",
  "rpcPort": 15888,
  "deviceName": "easytierhost",
  "enableInternetGateway": true
}
"@
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText($ProfilePath, $profileJson, $utf8)

& $installScript -InstallRoot $InstallRoot -ProfilePath $ProfilePath -StateDirectory $StateDirectory -ServiceName $ServiceName

Write-Host 'Waiting for DHCP overlay address...'
$deadline = (Get-Date).AddSeconds(90)
$readyJson = $null
do {
    $ready = Invoke-HostReady $hostExe $ProfilePath
    if ($ready.ExitCode -eq 0 -and $ready.Text -match '\{') {
        $readyJson = $ready.Text
        break
    }
    Start-Sleep -Seconds 1
} while ((Get-Date) -lt $deadline)

if ([string]::IsNullOrWhiteSpace($readyJson)) {
    & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Host
    throw 'Client overlay did not become ready within 90 seconds; service stopped.'
}

$overlayIp = '-'
try {
    $ready = $readyJson | ConvertFrom-Json
    if ($null -ne $ready.overlayIp) { $overlayIp = [string]$ready.overlayIp }
} catch { }

Write-Host "Client installed. Overlay IP: $overlayIp"
Write-Host 'DHCP range: 10.10.0.11-10.10.255.254'
Write-Host 'Gateway/DNS: 10.10.0.1 (applied by EasyTierHost after overlay is ready)'
Write-Host "Profile: $ProfilePath"
Write-Host 'No GUI is required; the EasyTierHost service stays running after this script exits.'
