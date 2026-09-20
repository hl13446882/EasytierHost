using System.Text;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

public sealed class WindowsRemoteInstaller : IServiceInstaller
{
    public async Task<DeploymentResult> InstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        try
        {
            DeploymentValidation.Validate(request, ServerOsType.Windows, "scripts/windows/install-service.ps1");
            await using var material = await DeploymentProfileMaterial.CreateAsync(request, ct);
            var stage = $"C:/ProgramData/EasyTierHost/deploy/{Guid.NewGuid():N}";
            var remotePackage = $"{stage}/{DeploymentValidation.PackageName(request.LocalPackageDirectory)}";
            var stagedProfile = $"{stage}/network.json";
            var stagedSecret = $"{stage}/network-secret.plain";
            var remoteSecret = DeploymentValidation.ResolveRemoteSecretPath(request, material.SecretRelativePath);

            try
            {
                var createScript = $"$ErrorActionPreference='Stop'; $stage={Ps(stage)}; New-Item -ItemType Directory -Force -Path $stage | Out-Null; $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; & \"$env:SystemRoot\\System32\\icacls.exe\" $stage '/inheritance:r' '/grant:r' (\"*${{sid}}:(OI)(CI)F\") '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if ($LASTEXITCODE -ne 0) {{ throw 'Unable to secure deployment staging directory' }}";
                var create = await remote.ExecuteAsync(PowerShell(createScript), ct);
                if (!create.Success) return DeploymentResult.Fail("ETH401", "Unable to create secure Windows deployment staging directory");
                await remote.UploadAsync(request.LocalPackageDirectory, stage, ct);
                await remote.UploadAsync(request.LocalProfilePath, stagedProfile, ct);
                await remote.UploadAsync(material.LocalSecretPath, stagedSecret, ct);

                var result = await remote.ExecuteAsync(PowerShell(BuildInstallTransaction(request, stage, remotePackage, stagedProfile, stagedSecret, remoteSecret)), ct);
                return result.Success
                    ? DeploymentResult.Ok("Windows service deployed and node readiness verified")
                    : DeploymentResult.Fail("ETH402", "Windows remote installation failed or readiness did not converge; rollback was attempted");
            }
            finally
            {
                // The transaction normally removes staging. This also covers SCP/profile/secret upload failures
                // before the transaction starts, so plaintext network material is not left on the remote host.
                await CleanupStageBestEffortAsync(remote, stage);
            }
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return DeploymentResult.Fail("ETH402", "Windows deployment preparation failed"); }
    }

    public async Task<DeploymentResult> UninstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        try
        {
            DeploymentValidation.ValidateRemotePath(request.RemoteInstallDirectory);
            var uninstallerRelative = request.Role == NodeRole.Client
                ? "scripts/windows/uninstall-client.ps1"
                : "scripts/windows/uninstall-service.ps1";
            var uninstaller = DeploymentValidation.CombineRemote(request.RemoteInstallDirectory, uninstallerRelative);
            var script = request.Role == NodeRole.Client
                ? $"$ErrorActionPreference='Stop'; & {Ps(uninstaller)} -Force -InstallRoot {Ps(request.RemoteInstallDirectory.Replace('\\', '/'))} -ConfigDirectory {Ps(DeploymentValidation.RemoteDirectory(DeploymentValidation.RemoteDirectory(request.RemoteProfilePath)))} -StateDirectory {Ps(request.RemoteStateDirectory)} -ServiceName {Ps(request.ServiceName)}; if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}"
                : $"$ErrorActionPreference='Stop'; & {Ps(uninstaller)} -ServiceName {Ps(request.ServiceName)} -StateDirectory {Ps(request.RemoteStateDirectory)}; if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}";
            var result = await remote.ExecuteAsync(PowerShell(script), ct);
            return result.Success
                ? DeploymentResult.Ok(request.Role == NodeRole.Client ? "Windows virtual network uninstalled" : "Windows service uninstalled")
                : DeploymentResult.Fail("ETH402", "Windows remote uninstall failed");
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
    }

    private static async Task CleanupStageBestEffortAsync(IRemoteExecutor remote, string stage)
    {
        try
        {
            var script = $"$ErrorActionPreference='SilentlyContinue'; if (Test-Path -LiteralPath {Ps(stage)}) {{ Remove-Item -LiteralPath {Ps(stage)} -Recurse -Force }}";
            _ = await remote.ExecuteAsync(PowerShell(script), CancellationToken.None);
        }
        catch { }
    }

    private static string BuildInstallTransaction(DeploymentRequest request, string stage, string remotePackage, string stagedProfile, string stagedSecret, string remoteSecret)
    {
        var install = request.RemoteInstallDirectory.Replace('\\', '/');
        var profile = request.RemoteProfilePath.Replace('\\', '/');
        var state = request.RemoteStateDirectory.Replace('\\', '/');
        var installer = DeploymentValidation.CombineRemote(install, "scripts/windows/install-service.ps1");
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$serviceName={Ps(request.ServiceName)}");
        sb.AppendLine($"$install={Ps(install)}");
        sb.AppendLine($"$state={Ps(state)}");
        sb.AppendLine($"$incoming={Ps(remotePackage)}");
        sb.AppendLine($"$profile={Ps(profile)}");
        sb.AppendLine($"$stagedProfile={Ps(stagedProfile)}");
        sb.AppendLine($"$secret={Ps(remoteSecret)}");
        sb.AppendLine($"$stagedSecret={Ps(stagedSecret)}");
        sb.AppendLine($"$stage={Ps(stage)}");
        sb.AppendLine("$backupInstall=\"$install.previous\"");
        sb.AppendLine("$backupProfile=\"$profile.previous\"");
        sb.AppendLine("$backupSecret=\"$secret.previous\"");
        sb.AppendLine("$hadInstall=Test-Path -LiteralPath $install");
        sb.AppendLine("$hadProfile=Test-Path -LiteralPath $profile");
        sb.AppendLine("$hadSecret=Test-Path -LiteralPath $secret");
        sb.AppendLine("$existing=Get-Service -Name $serviceName -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($existing -and -not $hadInstall) { throw 'Refusing to replace a service not owned by the configured install directory' }");
        sb.AppendLine("if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $existing.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(180)) }");
        sb.AppendLine("if ((Test-Path (Join-Path $state 'route-journal.json')) -or (Test-Path (Join-Path $state 'gateway-journal.json'))) { & (Join-Path $incoming 'easytier-host.exe') recover-network $state; if ($LASTEXITCODE) { throw 'Network recovery failed; preserve old installation' } }");
        sb.AppendLine("if (Test-Path -LiteralPath $backupInstall) { Remove-Item -LiteralPath $backupInstall -Recurse -Force }");
        sb.AppendLine("if (Test-Path -LiteralPath $backupProfile) { Remove-Item -LiteralPath $backupProfile -Force }");
        sb.AppendLine("if (Test-Path -LiteralPath $backupSecret) { Remove-Item -LiteralPath $backupSecret -Force }");
        sb.AppendLine("if ($hadInstall) { Move-Item -LiteralPath $install -Destination $backupInstall }");
        sb.AppendLine("if ($hadProfile) { Move-Item -LiteralPath $profile -Destination $backupProfile }");
        sb.AppendLine("if ($hadSecret) { Move-Item -LiteralPath $secret -Destination $backupSecret }");
        sb.AppendLine("try {");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $install) | Out-Null");
        sb.AppendLine("  $profileDir=Split-Path -Parent $profile");
        sb.AppendLine("  $secretDir=Split-Path -Parent $secret");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path $profileDir | Out-Null");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path $secretDir | Out-Null");
        sb.AppendLine("  & \"$env:SystemRoot\\System32\\icacls.exe\" $profileDir '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'Unable to secure profile directory' }");
        sb.AppendLine("  if ($secretDir -ne $profileDir) { & \"$env:SystemRoot\\System32\\icacls.exe\" $secretDir '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Unable to secure secret directory' } }");
        sb.AppendLine("  Move-Item -LiteralPath $incoming -Destination $install");
        sb.AppendLine("  Copy-Item -LiteralPath $stagedProfile -Destination $profile -Force");
        sb.AppendLine("  $hostExe=Join-Path $install 'easytier-host.exe'");
        sb.AppendLine("  $ensureRuntime=Join-Path $install 'scripts/windows/ensure-dotnet-runtime.ps1'");
        sb.AppendLine("  if (-not (Test-Path -LiteralPath $ensureRuntime -PathType Leaf)) { throw 'Package is missing scripts/windows/ensure-dotnet-runtime.ps1' }");
        sb.AppendLine("  & $ensureRuntime -PackageRoot $install");
        sb.AppendLine("  Get-Content -LiteralPath $stagedSecret -Raw | & $hostExe set-secret $secret");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'Remote secret provisioning failed' }");
        sb.AppendLine("  Remove-Item -LiteralPath $stagedSecret -Force");
        sb.AppendLine($"  & {Ps(installer)} -InstallRoot $install -ProfilePath $profile -StateDirectory {Ps(state)} -ServiceName $serviceName");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'Service installer returned failure' }");
        sb.AppendLine("  $ready=$false");
        sb.AppendLine("  for ($i=0; $i -lt 45; $i++) {");
        sb.AppendLine("    & $hostExe ready $profile *> $null");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) { $ready=$true; break }");
        sb.AppendLine("    Start-Sleep -Seconds 2");
        sb.AppendLine("  }");
        sb.AppendLine("  if (-not $ready) { throw 'EasyTierHost node readiness did not converge within 90 seconds' }");
        sb.AppendLine("  if (Test-Path -LiteralPath $backupInstall) { Remove-Item -LiteralPath $backupInstall -Recurse -Force }");
        sb.AppendLine("  if (Test-Path -LiteralPath $backupProfile) { Remove-Item -LiteralPath $backupProfile -Force }");
        sb.AppendLine("  if (Test-Path -LiteralPath $backupSecret) { Remove-Item -LiteralPath $backupSecret -Force }");
        sb.AppendLine("  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }");
        sb.AppendLine("}");
        sb.AppendLine("catch {");
        sb.AppendLine("  $newService=Get-Service -Name $serviceName -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($newService -and $newService.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $newService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(180)) }");
        sb.AppendLine("  if ((Test-Path (Join-Path $state 'route-journal.json')) -or (Test-Path (Join-Path $state 'gateway-journal.json'))) { & (Join-Path $install 'easytier-host.exe') recover-network $state; if ($LASTEXITCODE) { throw 'Network recovery failed; preserve installation and backup' } }");
        sb.AppendLine("  if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }");
        sb.AppendLine("  if ($hadInstall -and (Test-Path -LiteralPath $backupInstall)) { Move-Item -LiteralPath $backupInstall -Destination $install }");
        sb.AppendLine("  if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Force }");
        sb.AppendLine("  if ($hadProfile -and (Test-Path -LiteralPath $backupProfile)) { Move-Item -LiteralPath $backupProfile -Destination $profile }");
        sb.AppendLine("  if (Test-Path -LiteralPath $secret) { Remove-Item -LiteralPath $secret -Force }");
        sb.AppendLine("  if ($hadSecret -and (Test-Path -LiteralPath $backupSecret)) { Move-Item -LiteralPath $backupSecret -Destination $secret }");
        sb.AppendLine("  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("  if ($existing -and $hadInstall) { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }");
        sb.AppendLine("  throw");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string PowerShell(string script) =>
        $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";

    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
