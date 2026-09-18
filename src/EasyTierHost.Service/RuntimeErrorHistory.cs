using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Service;

public sealed class RuntimeErrorHistory(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public const int Capacity = 20;

    public static RuntimeErrorRecord Sanitize(Exception exception, DateTimeOffset? timestamp = null)
    {
        if (exception is HostException host)
        {
            var prefix = host.Code + ": ";
            var message = host.Message.StartsWith(prefix, StringComparison.Ordinal)
                ? host.Message[prefix.Length..]
                : "Host operation failed";
            return new(timestamp ?? DateTimeOffset.UtcNow, host.Code, message);
        }

        // Unexpected exception messages can contain command lines, remote output, paths or credentials.
        // Persist only the exception type.
        return new(timestamp ?? DateTimeOffset.UtcNow, "UNEXPECTED", exception.GetType().Name);
    }

    public async Task AppendAsync(Exception exception, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = (await ReadInternalAsync(ct)).ToList();
            items.Add(Sanitize(exception));
            if (items.Count > Capacity) items.RemoveRange(0, items.Count - Capacity);
            await ConfigurationStore.SaveAtomicAsync(path, items, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<RuntimeErrorRecord>> ReadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await ReadInternalAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<RuntimeErrorRecord>> ReadInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<RuntimeErrorRecord[]>(await File.ReadAllTextAsync(path, ct), ConfigurationStore.Json) ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }
}