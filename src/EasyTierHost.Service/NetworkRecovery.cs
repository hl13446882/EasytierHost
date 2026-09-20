namespace EasyTierHost.Service;

public static class NetworkRecovery
{
    // A failure in one subsystem must not prevent restoring the others.
    public static async Task RestoreAsync(params Func<Task>[] restorers)
    {
        var errors = new List<Exception>();
        foreach (var restore in restorers)
            try { await restore(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("ETH302: network recovery incomplete; preserve state and installation", errors);
    }

    public static async Task StopRoleAsync(CancellationTokenSource lifetime, Task? role, Func<Task> stopCore)
    {
        try
        {
            await lifetime.CancelAsync();
            if (role is not null)
                try { await role; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        finally { await stopCore(); }
    }
}
