using System.Text;
using System.Text.Json;

namespace EasyTierHost.Core;

public static class PowerShellCommand
{
    private static readonly JsonSerializerOptions LooseJson = new() { PropertyNameCaseInsensitive = true };

    public static Task<string> RunAsync(ICommandRunner runner, string script, CancellationToken ct) => runner.CheckedAsync("powershell.exe",
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " + script))], ct);
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public static string ReadJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new IOException("PowerShell returned no output");
        var text = output.Trim();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '{' or '[' or 't' or 'f' or 'n' or '-' || char.IsDigit(c)) { start = i; break; }
        }
        if (start < 0) throw new IOException("PowerShell returned no JSON");
        var slice = text[start..];
        var xml = slice.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (xml > 0) slice = slice[..xml];
        var objs = slice.IndexOf("<Objs ", StringComparison.Ordinal);
        if (objs > 0) slice = slice[..objs];
        return slice.Trim();
    }

    public static T[] DeserializeArray<T>(string output)
    {
        var json = ReadJson(output);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<T[]>(json, LooseJson) ?? [];
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var item = JsonSerializer.Deserialize<T>(json, LooseJson);
            return item is null ? [] : [item];
        }
        throw new IOException("Expected JSON array");
    }
    public static T Deserialize<T>(string output)
    {
        var json = ReadJson(output);
        if (typeof(T) == typeof(bool) && bool.TryParse(json, out var flag)) return (T)(object)flag;
        return JsonSerializer.Deserialize<T>(json, LooseJson) ?? throw new IOException("Empty JSON");
    }
}
