namespace OrynoSync.Core;

/// <summary>
/// §7/§11/§18: one place for the sync cadence. Normal connected idle mode polls remote metadata once a
/// minute instead of every few seconds; tests override the properties instead of hunting for hard-coded
/// millisecond values spread over the loop.
/// </summary>
public static class SyncCadence
{
    /// <summary>§7/§18: idle remote poll. Local changes do NOT wait for it (see <see cref="SyncWakeSignal"/>).</summary>
    public static TimeSpan RemotePollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>§10: while the queue still has work the scheduler re-checks quickly — the poll interval never gates throughput.</summary>
    public static TimeSpan QueueRecheckInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>§10: upper bound so a queue stuck in retry back-off cannot spin the loop forever.</summary>
    public static TimeSpan ActiveQueueBudget { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Retry gap after a transient API/app error: fast enough to recover, far slower than the old 4 s spam.</summary>
    public static TimeSpan ErrorRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Keeps the tests hermetic: they change the properties and restore the product values.</summary>
    public static void ResetForTests()
    {
        RemotePollInterval = TimeSpan.FromMinutes(1);
        QueueRecheckInterval = TimeSpan.FromMilliseconds(1500);
        ActiveQueueBudget = TimeSpan.FromMinutes(2);
        ErrorRetryInterval = TimeSpan.FromSeconds(15);
    }
}

/// <summary>
/// §9: the local filesystem watcher must not wait for the next remote poll. It pulses this signal and the
/// sync loop wakes up immediately (a semaphore, not a timer, and not a faster poll).
/// </summary>
public sealed class SyncWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pulses;

    public int PulseCount => Volatile.Read(ref _pulses);

    public void Pulse()
    {
        Interlocked.Increment(ref _pulses);
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* a pending wake-up is already armed */ }
    }

    /// <summary>True when woken by <see cref="Pulse"/>, false on timeout, cancellation or an already-pending signal.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
        {
            try { return await _signal.WaitAsync(0, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { await _signal.WaitAsync(linked.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>
/// §8: startup, reconnect, a user Start and a changed destination must refresh the server right away;
/// every other turn takes the idle cadence. The schedule is consumed exactly once per immediate request.
/// </summary>
public sealed class RemotePollSchedule
{
    private bool _immediate = true; // §8: app startup polls immediately

    public string LastImmediateReason { get; private set; } = "startup";

    public void RequestImmediate(string reason)
    {
        _immediate = true;
        LastImmediateReason = reason;
    }

    /// <summary>TimeSpan.Zero means "poll now"; otherwise wait this long (or until a local change wakes us).</summary>
    public TimeSpan WaitBeforeNextPoll()
    {
        if (!_immediate) return SyncCadence.RemotePollInterval;
        _immediate = false;
        return TimeSpan.Zero;
    }
}

/// <summary>What one queue pass observed, so the drain loop can decide whether more work exists.</summary>
public sealed record QueueDrainResult(int PendingOperations, int ActiveTransfers, bool ChangesApplied = false);

/// <summary>
/// §10/§21: while the queue holds Pending/Retrying/active work the loop drains it immediately; the moment
/// the queue is empty the drain returns and the caller goes back to the minute cadence.
/// </summary>
public sealed class QueueDrainer
{
    public int Passes { get; private set; }

    /// <summary>
    /// §10: how many passes without progress (operations left on their own retry back-off) are tolerated
    /// before the drain gives the loop back to the idle cadence. Without this a backed-off queue would keep
    /// the scheduler re-checking every <see cref="SyncCadence.QueueRecheckInterval"/> for the whole budget.
    /// </summary>
    public int StallPasses { get; init; } = 3;

    public async Task<QueueDrainResult> DrainAsync(
        Func<CancellationToken, Task<QueueDrainResult>> pass,
        CancellationToken ct,
        TimeSpan? recheck = null,
        TimeSpan? budget = null)
    {
        var pause = recheck ?? SyncCadence.QueueRecheckInterval;
        var deadline = DateTimeOffset.UtcNow + (budget ?? SyncCadence.ActiveQueueBudget);
        var best = int.MaxValue;
        var stalled = 0;
        while (true)
        {
            var result = await pass(ct).ConfigureAwait(false);
            Passes++;
            var remaining = result.PendingOperations + result.ActiveTransfers;
            if (remaining == 0) return result;
            if (remaining < best) { best = remaining; stalled = 0; } else stalled++;
            if (stalled >= StallPasses || ct.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline) return result;
            try { await Task.Delay(pause, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return result; }
        }
    }
}
