using EasyTierHost.Service;

internal static class RecoveryTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> Cases => new (string, Func<Task>)[]
    {
        ("Shutdown waits for rollback before stopping Core", WaitForRollback),
        ("Recovery attempts gateway cleanup even if routes fail", IndependentRecovery),
        ("Failed rollback stops Core but reports failure", FailedRollback),
        ("Standalone recovery refuses a live state lease", LiveLease)
    };
    private static async Task WaitForRollback()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        async Task Role()
        {
            try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
            finally { entered.SetResult(); await release.Task; }
        }
        var shutdown = NetworkRecovery.StopRoleAsync(lifetime, Role(), () => { stopped = true; return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (stopped || shutdown.IsCompleted) throw new Exception("Core stopped before rollback");
        release.SetResult(); await shutdown;
        if (!stopped) throw new Exception("Core not stopped");
    }
    private static async Task IndependentRecovery()
    {
        var second = false;
        try { await NetworkRecovery.RestoreAsync(() => Task.FromException(new IOException()), () => { second = true; return Task.CompletedTask; }); }
        catch (AggregateException) when (second) { return; }
        throw new Exception("Cleanup failure was hidden or second cleanup skipped");
    }
    private static async Task FailedRollback()
    {
        using var lifetime = new CancellationTokenSource(); var stopped = false;
        try { await NetworkRecovery.StopRoleAsync(lifetime, Task.FromException(new IOException()), () => { stopped = true; return Task.CompletedTask; }); }
        catch (IOException) when (stopped) { return; }
        throw new Exception("Rollback failure hidden");
    }
    private static async Task LiveLease()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easytier-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var lease = new FileStream(Path.Combine(dir, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await EasyTierHost.Service.Program.Main(["recover-network", dir]) == 0) throw new Exception("Live Host recovery was permitted");
        }
        finally { Directory.Delete(dir, true); }
    }
}
