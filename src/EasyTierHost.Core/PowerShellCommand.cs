using System.Text;

namespace EasyTierHost.Core;

public static class PowerShellCommand
{
    public static Task<string> RunAsync(ICommandRunner runner, string script, CancellationToken ct) => runner.CheckedAsync("powershell.exe",
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script))], ct);
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
