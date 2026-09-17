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
            var stage = $"C:/ProgramData/EasyTierHost/deploy/{Guid.NewGuid():N}";
            var remotePackage = $"{stage}/{DeploymentValidation.PackageName(request.LocalPackageDirectory)}";
            var stagedProfile = $"{stage}/network.json";

            var create = await remote.ExecuteAsync(PowerShell($"$ErrorActionPreference='Stop'; New-Item -ItemType Directory -Force -Path {Ps(stage)} | Out-Null"), ct);
            if (!create.Success) return DeploymentResult.Fail("ETH401", "Unable to create Windows deployment staging directory");
            await remote.UploadAsync(request.LocalPackageDirectory, stage, ct);
            await remote.UploadAsync(request.LocalProfilePath, stagedProfile, ct);

            var result = await remote.ExecuteAsync(PowerShell(BuildInstallTransaction(request, stage, remotePackage, stagedProfile)), ct);
            return result.Success
                ? DeploymentResult.Ok("Windows service deployed")
                : DeploymentResult.Fail("ETH402", "Windows remote installation failed and rollback was attempted");
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
            var uninstaller = DeploymentValidation.CombineRemote(request.RemoteInstallDirectory, "scripts/windows/uninstall-service.ps1");
            var script = $"$ErrorActionPreference='Stop'; & {Ps(uninstaller)} -ServiceName {Ps(request.ServiceName)} -StateDirectory {Ps(request.RemoteStateDirectory)}; if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}";
            var result = await remote.ExecuteAsync(PowerShell(script), ct);
            return result.Success ? DeploymentResult.Ok("Windows service uninstalled") : DeploymentResult.Fail("ETH402", "Windows remote uninstall failed");
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
    }

    private static string BuildInstallTransaction(DeploymentRequest request, string stage, string remotePackage, string stagedProfile)
    {
        var install = request.RemoteInstallDirectory.Replace('\\', '/');
        var profile = request.RemoteProfilePath.Replace('\\', '/');
        var state = request.RemoteStateDirectory.Replace('\\', '/');
        var installer = DeploymentValidation.CombineRemote(install, "scripts/windows/install-service.ps1");
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$serviceName={Ps(request.ServiceName)}");
        sb.AppendLine($"$install={Ps(install)}");
        sb.AppendLine($"$incoming={Ps(remotePackage)}");
        sb.AppendLine($"$profile={Ps(profile)}");
        sb.AppendLine($"$stagedProfile={Ps(stagedProfile)}");
        sb.AppendLine($"$stage={Ps(stage)}");
        sb.AppendLine("$backupInstall=\"$install.previous\"");
        sb.AppendLine("$backupProfile=\"$profile.previous\"");
        sb.AppendLine("$hadInstall=Test-Path -LiteralPath $install");
        sb.AppendLine("$hadProfile=Test-Path -LiteralPath $profile");
        sb.AppendLine("$existing=Get-Service -Name $serviceName -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($existing -and -not $hadInstall) { throw 'Refusing to replace a service not owned by the configured install directory' }");
        sb.AppendLine("if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $existing.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }");
        sb.AppendLine("if (Test-Path -LiteralPath $backupInstall) { Remove-Item -LiteralPath $backupInstall -Recurse -Force }");
        sb.AppendLine("if (Test-Path -LiteralPath $backupProfile) { Remove-Item -LiteralPath $backupProfile -Force }");
        sb.AppendLine("if ($hadInstall) { Move-Item -LiteralPath $install -Destination $backupInstall }");
        sb.AppendLine("if ($hadProfile) { Move-Item -LiteralPath $profile -Destination $backupProfile }");
        sb.AppendLine("try {");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $install) | Out-Null");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $profile) | Out-Null");
        sb.AppendLine("  Move-Item -LiteralPath $incoming -Destination $install");
        sb.AppendLine("  Copy-Item -LiteralPath $stagedProfile -Destination $profile -Force");
        sb.AppendLine($"  & {Ps(installer)} -InstallRoot $install -ProfilePath $profile -StateDirectory {Ps(state)} -ServiceName $serviceName");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'Service installer returned failure' }");
        sb.AppendLine("  if (Test-Path -LiteralPath $backupInstall) { Remove-Item -LiteralPath $backupInstall -Recurse -Force }");
        sb.AppendLine("  if (Test-Path -LiteralPath $backupProfile) { Remove-Item -LiteralPath $backupProfile -Force }");
        sb.AppendLine("  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }");
        sb.AppendLine("}");
        sb.AppendLine("catch {");
        sb.AppendLine("  $newService=Get-Service -Name $serviceName -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($newService -and $newService.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("  if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }");
        sb.AppendLine("  if ($hadInstall -and (Test-Path -LiteralPath $backupInstall)) { Move-Item -LiteralPath $backupInstall -Destination $install }");
        sb.AppendLine("  if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Force }");
        sb.AppendLine("  if ($hadProfile -and (Test-Path -LiteralPath $backupProfile)) { Move-Item -LiteralPath $backupProfile -Destination $profile }");
        sb.AppendLine("  if ($existing -and $hadInstall) { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }");
        sb.AppendLine("  throw");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string PowerShell(string script) =>
        $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";

    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
