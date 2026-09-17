[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ServiceName = "EasyTierHost",
    [string] $StateDirectory = "$env:ProgramData\EasyTierHost\state",
    [switch] $RemoveState
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
& "$env:SystemRoot\System32\sc.exe" query $ServiceName *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Service '$ServiceName' is not installed."
}
else {
    $query = (& "$env:SystemRoot\System32\sc.exe" query $ServiceName | Out-String)
    if ($query -notmatch 'STATE\s+:\s+1\s+STOPPED') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service and allow transactional route/DNS rollback')) {
            & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Host
            if ($LASTEXITCODE -notin 0, 1062) { throw "Unable to stop service '$ServiceName'." }
            Wait-ServiceState $ServiceName 'STOPPED' 30
        }
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete Windows service registration')) {
        & "$env:SystemRoot\System32\sc.exe" delete $ServiceName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Unable to delete service '$ServiceName'." }
    }
}

if ($RemoveState) {
    $fullState = [IO.Path]::GetFullPath($StateDirectory)
    $programDataRoot = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
    if (-not $fullState.StartsWith($programDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete state outside ProgramData: $fullState"
    }
    if (Test-Path -LiteralPath $fullState) {
        if ($PSCmdlet.ShouldProcess($fullState, 'Delete EasyTierHost runtime state')) {
            Remove-Item -LiteralPath $fullState -Recurse -Force
        }
    }
}

Write-Host "Uninstalled $ServiceName. Network profile and secret files were not deleted."
