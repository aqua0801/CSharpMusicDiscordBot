namespace DiscordBot;

/// <summary>
/// Replaces the old <c>new Timer(async _ =&gt; ...)</c> pattern.
/// Timer + async lambda is async void: a single exception inside kills the whole process,
/// runs can overlap, and it can't be stopped on shutdown. This uses PeriodicTimer + a shared shutdown token,
/// and exceptions from each tick are logged before continuing with the next tick.
/// </summary>
public static class BackgroundLoop
{
    private static readonly CancellationTokenSource _shutdown = new();

    public static CancellationToken ShutdownToken => _shutdown.Token;

    public static void StopAll() => _shutdown.Cancel();

    /// <summary>
    /// Runs at a fixed interval; the first run happens after <paramref name="initialDelay"/>.
    /// <paramref name="stopToken"/> can end the loop early (e.g. when a playlist finishes); it always stops on shutdown.
    /// </summary>
    public static void RunPeriodic(string name, TimeSpan interval, TimeSpan initialDelay, Func<CancellationToken, Task> action, CancellationToken stopToken = default)
    {
        _ = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, stopToken);
            var ct = linked.Token;
            try
            {
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, ct);

                using var timer = new PeriodicTimer(interval);
                do
                {
                    await RunOnceAsync(name, action, ct);
                }
                while (await timer.WaitForNextTickAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
    }

    /// <summary>Runs once a day at the given local time (recomputed every time, so DST / clock drift won't shift it).</summary>
    public static void RunDaily(string name, TimeOnly at, Func<CancellationToken, Task> action)
    {
        _ = Task.Run(async () =>
        {
            var ct = _shutdown.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = DateTime.Now;
                    var next = now.Date + at.ToTimeSpan();
                    if (next <= now) next = next.AddDays(1);

                    await Task.Delay(next - now, ct);
                    await RunOnceAsync(name, action, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
    }

    private static async Task RunOnceAsync(string name, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Background job '{name}' failed", ex);
        }
    }
}
