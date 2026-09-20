using System.Text;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Deployment;

public sealed class LinuxRemoteInstaller : IServiceInstaller
{
    public async Task<DeploymentResult> InstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        try
        {
            DeploymentValidation.Validate(request, ServerOsType.Linux, "scripts/linux/install-service.sh");
            await using var material = await DeploymentProfileMaterial.CreateAsync(request, ct);
            var stage = $"/tmp/easytier-host-deploy-{Guid.NewGuid():N}";
            var remotePackage = $"{stage}/{DeploymentValidation.PackageName(request.LocalPackageDirectory)}";
            var stagedProfile = $"{stage}/network.json";
            var stagedSecret = $"{stage}/network-secret.plain";
            var remoteSecret = DeploymentValidation.ResolveRemoteSecretPath(request, material.SecretRelativePath);

            try
            {
                // The SSH user owns staging so SCP works; mode 0700 prevents other local users reading plaintext material.
                var create = await remote.ExecuteAsync($"bash -c {Bash($"umask 077; mkdir -p -m 700 -- {Bash(stage)}; chmod 700 -- {Bash(stage)}")}", ct);
                if (!create.Success) return DeploymentResult.Fail("ETH401", "Unable to create secure Linux deployment staging directory");
                await remote.UploadAsync(request.LocalPackageDirectory, stage, ct);
                await remote.UploadAsync(request.LocalProfilePath, stagedProfile, ct);
                await remote.UploadAsync(material.LocalSecretPath, stagedSecret, ct);
                var protectSecret = await remote.ExecuteAsync($"bash -c {Bash($"chmod 600 -- {Bash(stagedSecret)}")}", ct);
                if (!protectSecret.Success) return DeploymentResult.Fail("ETH401", "Unable to secure staged Linux network secret");

                var result = await remote.ExecuteAsync(RootShell(request, BuildInstallTransaction(request, stage, remotePackage, stagedProfile, stagedSecret, remoteSecret)), ct);
                return result.Success
                    ? DeploymentResult.Ok("Linux service deployed and node readiness verified")
                    : DeploymentResult.Fail("ETH402", "Linux remote installation failed or readiness did not converge; rollback was attempted");
            }
            finally
            {
                // Covers SCP/profile/secret upload failures before the root transaction starts.
                await CleanupStageBestEffortAsync(remote, stage);
            }
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return DeploymentResult.Fail("ETH402", "Linux deployment preparation failed"); }
    }

    public async Task<DeploymentResult> UninstallAsync(DeploymentRequest request, IRemoteExecutor remote, CancellationToken ct)
    {
        try
        {
            DeploymentValidation.ValidateRemotePath(request.RemoteInstallDirectory);
            string script;
            if (request.Role == NodeRole.Client)
            {
                var uninstaller = DeploymentValidation.CombineRemote(request.RemoteInstallDirectory, "scripts/linux/client-control.sh");
                script = $"chmod 700 {Bash(uninstaller)} && EASYTIER_HOST_ROOT={Bash(request.RemoteInstallDirectory)} EASYTIER_HOST_PROFILE={Bash(request.RemoteProfilePath)} EASYTIER_HOST_STATE={Bash(request.RemoteStateDirectory)} EASYTIER_HOST_SERVICE={Bash(request.ServiceName)} {Bash(uninstaller)} uninstall";
            }
            else
            {
                var uninstaller = DeploymentValidation.CombineRemote(request.RemoteInstallDirectory, "scripts/linux/uninstall-service.sh");
                script = $"chmod 700 {Bash(uninstaller)} && {Bash(uninstaller)} {Bash(request.ServiceName)} {Bash(request.RemoteStateDirectory)} false";
            }
            var result = await remote.ExecuteAsync(RootShell(request, script), ct);
            return result.Success
                ? DeploymentResult.Ok(request.Role == NodeRole.Client ? "Linux virtual network uninstalled" : "Linux service uninstalled")
                : DeploymentResult.Fail("ETH402", "Linux remote uninstall failed");
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
    }

    private static async Task CleanupStageBestEffortAsync(IRemoteExecutor remote, string stage)
    {
        try
        {
            _ = await remote.ExecuteAsync($"bash -c {Bash($"rm -rf -- {Bash(stage)}")}", CancellationToken.None);
        }
        catch { }
    }

    private static string BuildInstallTransaction(DeploymentRequest request, string stage, string remotePackage, string stagedProfile, string stagedSecret, string remoteSecret)
    {
        var install = request.RemoteInstallDirectory;
        var profile = request.RemoteProfilePath;
        var installer = DeploymentValidation.CombineRemote(install, "scripts/linux/install-service.sh");
        var sb = new StringBuilder();
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"service={Bash(request.ServiceName)}");
        sb.AppendLine($"install={Bash(install)}");
        sb.AppendLine($"state={Bash(request.RemoteStateDirectory)}");
        sb.AppendLine($"incoming={Bash(remotePackage)}");
        sb.AppendLine($"profile={Bash(profile)}");
        sb.AppendLine($"staged_profile={Bash(stagedProfile)}");
        sb.AppendLine($"secret={Bash(remoteSecret)}");
        sb.AppendLine($"staged_secret={Bash(stagedSecret)}");
        sb.AppendLine($"stage={Bash(stage)}");
        sb.AppendLine("backup_install=\"${install}.previous\"");
        sb.AppendLine("backup_profile=\"${profile}.previous\"");
        sb.AppendLine("backup_secret=\"${secret}.previous\"");
        sb.AppendLine("had_install=false; if [[ -d \"$install\" ]]; then had_install=true; fi");
        sb.AppendLine("had_profile=false; if [[ -f \"$profile\" ]]; then had_profile=true; fi");
        sb.AppendLine("had_secret=false; if [[ -f \"$secret\" ]]; then had_secret=true; fi");
        sb.AppendLine("if systemctl list-unit-files --type=service --no-legend \"${service}.service\" 2>/dev/null | grep -q \"${service}.service\"; then if [[ \"$had_install\" != true ]]; then echo 'foreign service ownership' >&2; exit 41; fi; fi");
        sb.AppendLine("if systemctl cat \"$service\" >/dev/null 2>&1; then systemctl stop \"$service\" || exit 1; fi");
        sb.AppendLine("if [[ -f \"$state/route-journal.json\" || -f \"$state/gateway-journal.json\" ]]; then chmod +x \"$incoming/easytier-host\"; \"$incoming/easytier-host\" recover-network \"$state\"; fi");
        sb.AppendLine("rm -rf -- \"$backup_install\"; rm -f -- \"$backup_profile\" \"$backup_secret\"");
        sb.AppendLine("if [[ \"$had_install\" == true ]]; then mv -- \"$install\" \"$backup_install\"; fi");
        sb.AppendLine("if [[ \"$had_profile\" == true ]]; then mv -- \"$profile\" \"$backup_profile\"; fi");
        sb.AppendLine("if [[ \"$had_secret\" == true ]]; then mv -- \"$secret\" \"$backup_secret\"; fi");
        sb.AppendLine("rollback() {");
        sb.AppendLine("  if systemctl cat \"$service\" >/dev/null 2>&1; then systemctl stop \"$service\" || return 1; fi");
        sb.AppendLine("  if [[ -f \"$state/route-journal.json\" || -f \"$state/gateway-journal.json\" ]]; then \"$install/easytier-host\" recover-network \"$state\" || return 1; fi");
        sb.AppendLine("  rm -rf -- \"$install\"");
        sb.AppendLine("  if [[ \"$had_install\" == true && -d \"$backup_install\" ]]; then mv -- \"$backup_install\" \"$install\"; fi");
        sb.AppendLine("  rm -f -- \"$profile\"");
        sb.AppendLine("  if [[ \"$had_profile\" == true && -f \"$backup_profile\" ]]; then mv -- \"$backup_profile\" \"$profile\"; fi");
        sb.AppendLine("  rm -f -- \"$secret\"");
        sb.AppendLine("  if [[ \"$had_secret\" == true && -f \"$backup_secret\" ]]; then mv -- \"$backup_secret\" \"$secret\"; fi");
        sb.AppendLine("  rm -rf -- \"$stage\" 2>/dev/null || true");
        sb.AppendLine("  if [[ \"$had_install\" == true ]]; then systemctl start \"$service\" 2>/dev/null || true; fi");
        sb.AppendLine("}");
        sb.AppendLine("trap rollback ERR");
        sb.AppendLine("mkdir -p -- \"$(dirname \"$install\")\" \"$(dirname \"$profile\")\" \"$(dirname \"$secret\")\"");
        sb.AppendLine("chmod 700 -- \"$(dirname \"$profile\")\" \"$(dirname \"$secret\")\"");
        sb.AppendLine("mv -- \"$incoming\" \"$install\"");
        // Packages are commonly produced/uploaded from Windows, where Unix execute bits are not preserved.
        // Restore only the known executables and our own scripts before invoking any of them.
        sb.AppendLine("chmod 700 -- \"$install/easytier-host\" \"$install/easytier-core\" \"$install/easytier-cli\"");
        sb.AppendLine("if [[ -d \"$install/scripts/linux\" ]]; then find \"$install/scripts/linux\" -maxdepth 1 -type f -name '*.sh' -exec chmod 700 -- {} +; fi");
        sb.AppendLine("cp -- \"$staged_profile\" \"$profile\"");
        sb.AppendLine("chmod 600 -- \"$profile\" \"$staged_secret\"");
        sb.AppendLine("DOTNET_ROOT=\"$(\"$install/scripts/linux/ensure-dotnet-runtime.sh\" \"$install\")\"");
        sb.AppendLine("export DOTNET_ROOT PATH=\"$DOTNET_ROOT:$PATH\"");
        sb.AppendLine("\"$install/easytier-host\" set-secret \"$secret\" < \"$staged_secret\"");
        sb.AppendLine("rm -f -- \"$staged_secret\"");
        sb.AppendLine("chmod 600 -- \"$secret\"");
        sb.AppendLine($"chmod 700 {Bash(installer)}");
        sb.AppendLine($"{Bash(installer)} \"$install\" \"$profile\" {Bash(request.RemoteStateDirectory)} \"$service\"");
        sb.AppendLine("ready=false");
        sb.AppendLine("for _ in $(seq 1 45); do");
        sb.AppendLine("  if \"$install/easytier-host\" ready \"$profile\" >/dev/null 2>&1; then ready=true; break; fi");
        sb.AppendLine("  sleep 2");
        sb.AppendLine("done");
        sb.AppendLine("if [[ \"$ready\" != true ]]; then echo 'EasyTierHost node readiness did not converge within 90 seconds' >&2; false; fi");
        sb.AppendLine("trap - ERR");
        sb.AppendLine("rm -rf -- \"$backup_install\"; rm -f -- \"$backup_profile\" \"$backup_secret\"; rm -rf -- \"$stage\"");
        return sb.ToString();
    }

    private static string RootShell(DeploymentRequest request, string script)
    {
        var command = $"bash -c {Bash(script)}";
        return request.Remote.Username.Equals("root", StringComparison.Ordinal) ? command : $"sudo -n {command}";
    }

    private static string Bash(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
