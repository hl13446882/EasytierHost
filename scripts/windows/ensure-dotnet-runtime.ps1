[CmdletBinding()]
param(
    [string] $PackageRoot,
    [string] $RuntimeIdentifier,
    [switch] $WindowsDesktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PackageRoot {
    if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
        return [IO.Path]::GetFullPath($PackageRoot)
    }

    $scriptDir = $PSScriptRoot
    $parent = Split-Path -Parent $scriptDir
    $grandParent = Split-Path -Parent $parent
    if ((Split-Path -Leaf $scriptDir) -eq 'windows' -and (Split-Path -Leaf $parent) -eq 'scripts' -and -not [string]::IsNullOrWhiteSpace($grandParent)) {
        return $grandParent
    }

    return $scriptDir
}

function Get-RuntimeIdentifierFromPackage([string] $Root) {
    $versionPath = Join-Path $Root 'version.txt'
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { return $null }
    foreach ($line in Get-Content -LiteralPath $versionPath) {
        if ($line -match '^RuntimeIdentifier=(.+)$') { return $Matches[1].Trim() }
    }
    return $null
}

function Get-NativeWindowsArch {
    $arch = $env:PROCESSOR_ARCHITEW6432
    if ([string]::IsNullOrWhiteSpace($arch)) { $arch = $env:PROCESSOR_ARCHITECTURE }
    switch ($arch) {
        'ARM64' { return 'arm64' }
        'AMD64' { return 'x64' }
        default { throw "Unsupported Windows architecture: $arch" }
    }
}

function ConvertTo-WindowsRuntimeArch([string] $Rid) {
    switch ($Rid) {
        'win-x64' { return 'x64' }
        'win-arm64' { return 'arm64' }
        default { throw "Unsupported Windows runtime identifier: $Rid" }
    }
}

function Test-SharedFramework([string] $Name) {
    $roots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $roots += (Join-Path $env:DOTNET_ROOT "shared\$Name")
    }
    $roots += (Join-Path $env:ProgramFiles "dotnet\shared\$Name")
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '10.*' }
        if ($found) { return $true }
    }
    return $false
}

function Test-RequiredRuntime {
    if ($WindowsDesktop) { return Test-SharedFramework 'Microsoft.WindowsDesktop.App' }
    if (Test-SharedFramework 'Microsoft.WindowsDesktop.App') { return $true }
    return Test-SharedFramework 'Microsoft.NETCore.App'
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required to install the .NET 10 runtime.'
    }
}

function Install-OfficialRuntime([string] $Kind, [string] $Arch) {
    Assert-Administrator
    $uri = "https://aka.ms/dotnet/10.0/$Kind-win-$Arch.exe"
    $installer = Join-Path $env:TEMP ("easytier-host-" + $Kind + "-10-" + $Arch + ".exe")
    Write-Host "Downloading $Kind 10 ($Arch) from Microsoft..."
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path -LiteralPath $curl -PathType Leaf) {
        & $curl --fail --location --silent --show-error --output $installer $uri
        if ($LASTEXITCODE -ne 0) { throw "Failed to download $uri" }
    }
    else {
        Invoke-WebRequest -Uri $uri -OutFile $installer -UseBasicParsing
    }

    $process = Start-Process -FilePath $installer -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw ("Microsoft {0} installer failed with exit code {1}." -f $Kind, $process.ExitCode)
    }
}

$resolvedRoot = Get-PackageRoot
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = Get-RuntimeIdentifierFromPackage $resolvedRoot
}
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $arch = Get-NativeWindowsArch
}
else {
    $arch = ConvertTo-WindowsRuntimeArch $RuntimeIdentifier
}

if (Test-RequiredRuntime) { return }

if ($WindowsDesktop) {
    Install-OfficialRuntime 'windowsdesktop-runtime' $arch
}
else {
    Install-OfficialRuntime 'dotnet-runtime' $arch
}

if (-not (Test-RequiredRuntime)) {
    throw 'The .NET 10 runtime was installed but is still not visible to EasyTierHost.'
}
