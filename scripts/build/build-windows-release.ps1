[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [ValidateSet('Debug','Release')] [string] $Configuration = 'Release',
    [string] $OutputRoot = 'publish/release',
    [switch] $SkipTests,
    [switch] $SkipArchives
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Command([string] $Name, [string] $Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. $Hint"
    }
}

function Assert-RelativeOutput([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path) -or $Path -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'OutputRoot must be a repository-relative path and must not contain ..'
    }
}

function Invoke-Native([scriptblock] $Action, [string] $Label) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE" }
}

Assert-RelativeOutput $OutputRoot
Assert-Command 'dotnet' 'Install .NET 10 SDK.'
Assert-Command 'cargo' 'Install Rust stable with rustup.'
Assert-Command 'rustup' 'Install Rust with rustup.'
Assert-Command 'protoc' 'Install Protocol Buffers compiler and add it to PATH.'
Assert-Command '7z' 'Install 7-Zip and add 7z.exe to PATH.'

Push-Location $repo
try {
    Write-Host '== EasyTierHost Windows release build =='
    Write-Host "RID:           $RuntimeIdentifier"
    Write-Host "Configuration: $Configuration"
    Write-Host "Output:        $OutputRoot"

    Invoke-Native { dotnet build EasyTierHost.sln -c $Configuration --nologo } 'Host solution build'
    Invoke-Native { dotnet build src/EasyTierHost.Deployment/EasyTierHost.Deployment.csproj -c $Configuration --nologo } 'Deployment build'
    Invoke-Native { dotnet build src/EasyTierHost.Manager/EasyTierHost.Manager.csproj -c $Configuration --nologo } 'Manager build'
    Invoke-Native { dotnet build src/EasyTierHost.Client.Windows/EasyTierHost.Client.Windows.csproj -c $Configuration --nologo } 'Windows client build'

    if (-not $SkipTests) {
        foreach ($project in @(
            'tests/EasyTierHost.UnitTests',
            'tests/EasyTierHost.ClientTests',
            'tests/EasyTierHost.DiagnosticsTests',
            'tests/EasyTierHost.IntegrationTests',
            'tests/EasyTierHost.DeploymentTests'
        )) {
            Invoke-Native { dotnet run --project $project -c $Configuration } "Test $project"
        }
    }

    $easyTier = Join-Path $repo 'EasyTier-2.6.4'
    $nativeArch = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x86_64' }
    $nativeDirectory = Join-Path $easyTier "easytier/third_party/$nativeArch"
    if (-not (Test-Path -LiteralPath $nativeDirectory -PathType Container)) { throw "Missing EasyTier native directory: $nativeDirectory" }

    Push-Location $easyTier
    try {
        $env:PATH = "$nativeDirectory;C:/Program Files/7-Zip;" + $env:PATH
        if (-not $SkipTests) {
            # Targeted tests run on the build host before any cross-target release build.
            Invoke-Native { cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features --features tun } 'EasyTier DHCP tests'
            Invoke-Native { cargo +stable test -p easytier --lib underlay_ --no-default-features --features tun } 'EasyTier underlay tests'
        }

        if ($RuntimeIdentifier -eq 'win-x64') {
            Invoke-Native { cargo +stable build -p easytier --release --no-default-features --features tun --bin easytier-core --bin easytier-cli } 'EasyTier release build'
            $coreDir = Join-Path $easyTier 'target/release'
        }
        else {
            $target = 'aarch64-pc-windows-msvc'
            Invoke-Native { rustup target add $target } 'Install Rust win-arm64 target'
            Invoke-Native { cargo +stable build -p easytier --release --target $target --no-default-features --features tun --bin easytier-core --bin easytier-cli } 'EasyTier ARM64 release build'
            $coreDir = Join-Path $easyTier "target/$target/release"
        }
    }
    finally { Pop-Location }

    $publishWindows = Join-Path $repo 'scripts/publish/publish-windows.ps1'
    $verify = Join-Path $repo 'scripts/publish/verify-package.ps1'
    $metadata = Join-Path $repo 'scripts/publish/write-package-metadata.ps1'
    $roles = @('seed-windows','dedicated-windows','client-windows')

    foreach ($role in $roles) {
        $out = "$OutputRoot/$role"
        & $publishWindows -CoreDirectory $coreDir -OutputDirectory $out -RuntimeIdentifier $RuntimeIdentifier -Configuration $Configuration
        $kind = if ($role -eq 'client-windows') { 'client-windows' } else { 'server-windows' }
        & $verify -PackageDirectory $out -ExpectedPackageKind $kind
    }

    $managerOutRel = "$OutputRoot/manager"
    $managerOut = Join-Path $repo $managerOutRel
    if (Test-Path -LiteralPath $managerOut) { Remove-Item -LiteralPath $managerOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $managerOut | Out-Null
    Invoke-Native { dotnet publish src/EasyTierHost.Manager/EasyTierHost.Manager.csproj -c $Configuration -r $RuntimeIdentifier --self-contained false --nologo -p:DebugType=None -p:DebugSymbols=false -o $managerOut } 'Manager publish'
    Get-ChildItem -LiteralPath $managerOut -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/windows/ensure-dotnet-runtime.ps1') -Destination (Join-Path $managerOut 'ensure-dotnet-runtime.ps1') -Force
    $managerLauncher = @(
        '@echo off'
        'setlocal'
        'set "HERE=%~dp0"'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%ensure-dotnet-runtime.ps1" -WindowsDesktop'
        'if errorlevel 1 exit /b 1'
        'start "" "%HERE%EasyTierHost.Manager.exe"'
    )
    Set-Content -LiteralPath (Join-Path $managerOut 'EasyTierHost.Manager.cmd') -Value $managerLauncher -Encoding ascii
    & $metadata -PackageDirectory $managerOut -RuntimeIdentifier $RuntimeIdentifier -PackageKind 'manager-windows'
    & $verify -PackageDirectory $managerOut -ExpectedPackageKind 'manager-windows'

    if (-not $SkipArchives) {
        $archiveRoot = Join-Path $repo "$OutputRoot/archives"
        if (Test-Path -LiteralPath $archiveRoot) { Remove-Item -LiteralPath $archiveRoot -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
        foreach ($name in @('manager') + $roles) {
            $source = Join-Path $repo "$OutputRoot/$name"
            $zip = Join-Path $archiveRoot "EasyTierHost-$name-$RuntimeIdentifier.zip"
            Compress-Archive -Path (Join-Path $source '*') -DestinationPath $zip -CompressionLevel Optimal -Force
        }
        Write-Host "Archives: $archiveRoot"
    }

    Write-Host 'Windows release build completed successfully.'
    Write-Host "Packages: $(Join-Path $repo $OutputRoot)"
}
finally { Pop-Location }
