[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $InstallRoot,
    [Parameter(Mandatory = $true)] [string] $ProfilePath,
    [string] $StateDirectory = "$env:ProgramData\EasyTierHost\state",
    [string] $ServiceName = "EasyTierHost",
    [string] $DisplayName = "EasyTierHost Overlay Service"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required.'
    }
}

function Invoke-Sc([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failed with exit code $LASTEXITCODE: $($Arguments -join ' ')" }
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

Assert-Administrator
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$ProfilePath = [IO.Path]::GetFullPath($ProfilePath)
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$exe = Join-Path $InstallRoot 'easytier-host.exe'

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing service executable: $exe" }
if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) { throw "Missing network profile: $ProfilePath" }

New-Item -ItemType Directory -Force -Path $StateDirectory | Out-Null
# Host runtime state can contain route snapshots and recovery journals. Restrict it to SYSTEM/Admins.
& "$env:SystemRoot\System32\icacls.exe" $StateDirectory /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Failed to secure the service state directory.' }

# Validate before changing SCM. Validation also catches profile schema and reserved-address errors.
& $exe validate $ProfilePath
if ($LASTEXITCODE -ne 0) { throw 'Network profile validation failed.' }

$binaryPath = '"{0}" service "{1}" "{2}"' -f $exe, $ProfilePath, $StateDirectory
& "$env:SystemRoot\System32\sc.exe" query $ServiceName *> $null
$exists = $LASTEXITCODE -eq 0
if ($exists) {
    $query = (& "$env:SystemRoot\System32\sc.exe" query $ServiceName | Out-String)
    if ($query -notmatch 'STATE\s+:\s+1\s+STOPPED') {
        & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Host
        if ($LASTEXITCODE -notin 0, 1062) { throw "Unable to stop existing service '$ServiceName'." }
        Wait-ServiceState $ServiceName 'STOPPED'
    }
    Invoke-Sc config $ServiceName 'binPath=' $binaryPath 'start=' auto 'obj=' LocalSystem 'DisplayName=' $DisplayName
}
else {
    Invoke-Sc create $ServiceName 'binPath=' $binaryPath 'start=' auto 'obj=' LocalSystem 'DisplayName=' $DisplayName
}

Invoke-Sc description $ServiceName 'EasyTierHost owns EasyTier Core, overlay gateway/DNS and transactional route recovery.'
Invoke-Sc failure $ServiceName 'reset=' 120 'actions=' 'restart/5000/restart/5000/restart/10000'
Invoke-Sc failureflag $ServiceName 1

# Give STOP/PRESHUTDOWN enough time to remove owned DNS/routes and persist recovery state.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $serviceKey -Name PreshutdownTimeout -PropertyType DWord -Value 30000 -Force | Out-Null

Invoke-Sc start $ServiceName
Wait-ServiceState $ServiceName 'RUNNING' 30
Write-Host "Installed and started $ServiceName."
Write-Host "Profile: $ProfilePath"
Write-Host "State:   $StateDirectory"
