[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $WindowsCoreDirectory,
    [Parameter(Mandatory = $true)] [string] $LinuxCoreDirectory,
    [ValidateSet('win-x64','win-arm64')] [string] $WindowsRuntimeIdentifier = 'win-x64',
    [ValidateSet('linux-x64','linux-arm64')] [string] $LinuxRuntimeIdentifier = 'linux-x64',
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$windowsPublisher = Join-Path $PSScriptRoot 'publish-windows.ps1'
$linuxPublisher = Join-Path $PSScriptRoot 'publish-linux.ps1'

function Invoke-RolePublisher {
    param(
        [Parameter(Mandatory = $true)] [string] $Script,
        [Parameter(Mandatory = $true)] [string] $CoreDirectory,
        [Parameter(Mandatory = $true)] [string] $OutputDirectory,
        [Parameter(Mandatory = $true)] [string] $RuntimeIdentifier,
        [switch] $IncludeClientUi
    )

    if ($IncludeClientUi) {
        & $Script -CoreDirectory $CoreDirectory -OutputDirectory $OutputDirectory -RuntimeIdentifier $RuntimeIdentifier -Configuration $Configuration -IncludeClientUi
    }
    else {
        & $Script -CoreDirectory $CoreDirectory -OutputDirectory $OutputDirectory -RuntimeIdentifier $RuntimeIdentifier -Configuration $Configuration
    }
    if ($LASTEXITCODE -ne 0) { throw "Package publisher failed: $OutputDirectory" }
}

# The gateway at 10.10.0.1 is a Dedicated-class deployment package with Role=Gateway in its profile.
# Do not create a second gateway binary layout: role behavior belongs to configuration/runtime state.
foreach ($role in @('seed', 'dedicated', 'client')) {
    Invoke-RolePublisher -Script $windowsPublisher -CoreDirectory $WindowsCoreDirectory `
        -OutputDirectory "publish/$role-windows" -RuntimeIdentifier $WindowsRuntimeIdentifier `
        -IncludeClientUi:($role -eq 'client')
    Invoke-RolePublisher -Script $linuxPublisher -CoreDirectory $LinuxCoreDirectory `
        -OutputDirectory "publish/$role-linux" -RuntimeIdentifier $LinuxRuntimeIdentifier
}

$managerOut = Join-Path $repo 'publish/manager'
if (Test-Path -LiteralPath $managerOut) { Remove-Item -LiteralPath $managerOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $managerOut | Out-Null

& dotnet publish (Join-Path $repo 'src/EasyTierHost.Manager/EasyTierHost.Manager.csproj') `
    -c $Configuration -r $WindowsRuntimeIdentifier --self-contained true --nologo -o $managerOut
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed' }

$manifestPath = Join-Path $managerOut 'artifact-manifest.json'
$entries = @(
    Get-ChildItem -LiteralPath $managerOut -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = [IO.Path]::GetRelativePath($managerOut, $_.FullName).Replace('\','/')
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
@{ Files = $entries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host 'Canonical publish layout created:'
Write-Host '  publish/manager'
Write-Host '  publish/seed-windows'
Write-Host '  publish/seed-linux'
Write-Host '  publish/dedicated-windows   (includes Gateway role at 10.10.0.1)'
Write-Host '  publish/dedicated-linux     (includes Gateway role at 10.10.0.1)'
Write-Host '  publish/client-windows      (includes client-ui/EasyTierHost.Client.Windows.exe)'
Write-Host '  publish/client-linux'
