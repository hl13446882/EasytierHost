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
            var stage = $"/tmp/easytier-host-deploy-{Guid.NewGuid():N}";
            var remotePackage = $"{stage}/{DeploymentValidation.PackageName(request.LocalPackageDirectory)}";
            var stagedProfile = $"{stage}/network.json";

            // The SSH user owns staging so SCP works even when installation later requires sudo.
            var create = await remote.ExecuteAsync($"bash -c {Bash($"mkdir -p -- {Bash(stage)}")}", ct);
            if (!create.Success) return DeploymentResult.Fail("ETH401", "Unable to create Linux deployment staging directory");
            await remote.UploadAsync(request.LocalPackageDirectory, stage, ct);
            await remote.UploadAsync(request.LocalProfilePath, stagedProfile, ct);

            var result = await remote.ExecuteAsync(RootShell(request, BuildInstallTransaction(request, stage, remotePackage, stagedProfile)), ct);
            return result.Success
                ? DeploymentResult.Ok("Linux service deployed")
                : DeploymentResult.Fail("ETH402", "Linux remote installation failed and rollback was attempted");
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
            var uninstaller = DeploymentValidation.CombineRemote(request.RemoteInstallDirectory, "scripts/linux/uninstall-service.sh");
            var script = $"chmod 700 {Bash(uninstaller)} && {Bash(uninstaller)} {Bash(request.ServiceName)} {Bash(request.RemoteStateDirectory)} false";
            var result = await remote.ExecuteAsync(RootShell(request, script), ct);
            return result.Success ? DeploymentResult.Ok("Linux service uninstalled") : DeploymentResult.Fail("ETH402", "Linux remote uninstall failed");
        }
        catch (HostException ex) { return DeploymentResult.Fail(ex.Code, ex.Message); }
    }

    private static string BuildInstallTransaction(DeploymentRequest request, string stage, string remotePackage, string stagedProfile)
    {
        var install = request.RemoteInstallDirectory;
        var profile = request.RemoteProfilePath;
        var installer = DeploymentValidation.CombineRemote(install, "scripts/linux/install-service.sh");
        var sb = new StringBuilder();
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"service={Bash(request.ServiceName)}");
        sb.AppendLine($"install={Bash(install)}");
        sb.AppendLine($"incoming={Bash(remotePackage)}");
        sb.AppendLine($"profile={Bash(profile)}");
        sb.AppendLine($"staged_profile={Bash(stagedProfile)}");
        sb.AppendLine($"stage={Bash(stage)}");
        sb.AppendLine("backup_install=\"${install}.previous\"");
        sb.AppendLine("backup_profile=\"${profile}.previous\"");
        sb.AppendLine("had_install=false; if [[ -d \"$install\" ]]; then had_install=true; fi");
        sb.AppendLine("had_profile=false; if [[ -f \"$profile\" ]]; then had_profile=true; fi");
        sb.AppendLine("if systemctl list-unit-files --type=service --no-legend \"${service}.service\" 2>/dev/null | grep -q \"${service}.service\"; then if [[ \"$had_install\" != true ]]; then echo 'foreign service ownership' >&2; exit 41; fi; fi");
        sb.AppendLine("systemctl stop \"$service\" 2>/dev/null || true");
        sb.AppendLine("rm -rf -- \"$backup_install\"; rm -f -- \"$backup_profile\"");
        sb.AppendLine("if [[ \"$had_install\" == true ]]; then mv -- \"$install\" \"$backup_install\"; fi");
        sb.AppendLine("if [[ \"$had_profile\" == true ]]; then mv -- \"$profile\" \"$backup_profile\"; fi");
        sb.AppendLine("rollback() {");
        sb.AppendLine("  systemctl stop \"$service\" 2>/dev/null || true");
        sb.AppendLine("  rm -rf -- \"$install\"");
        sb.AppendLine("  if [[ \"$had_install\" == true && -d \"$backup_install\" ]]; then mv -- \"$backup_install\" \"$install\"; fi");
        sb.AppendLine("  rm -f -- \"$profile\"");
        sb.AppendLine("  if [[ \"$had_profile\" == true && -f \"$backup_profile\" ]]; then mv -- \"$backup_profile\" \"$profile\"; fi");
        sb.AppendLine("  if [[ \"$had_install\" == true ]]; then systemctl start \"$service\" 2>/dev/null || true; fi");
        sb.AppendLine("}");
        sb.AppendLine("trap rollback ERR");
        sb.AppendLine("mkdir -p -- \"$(dirname \"$install\")\" \"$(dirname \"$profile\")\"");
        sb.AppendLine("mv -- \"$incoming\" \"$install\"");
        sb.AppendLine("cp -- \"$staged_profile\" \"$profile\"");
        sb.AppendLine($"chmod 700 {Bash(installer)}");
        sb.AppendLine($"{Bash(installer)} \"$install\" \"$profile\" {Bash(request.RemoteStateDirectory)} \"$service\"");
        sb.AppendLine("trap - ERR");
        sb.AppendLine("rm -rf -- \"$backup_install\"; rm -f -- \"$backup_profile\"; rm -rf -- \"$stage\"");
        return sb.ToString();
    }

    private static string RootShell(DeploymentRequest request, string script)
    {
        var command = $"bash -c {Bash(script)}";
        return request.Remote.Username.Equals("root", StringComparison.Ordinal) ? command : $"sudo -n {command}";
    }

    private static string Bash(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
