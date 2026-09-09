using OrynoSync.Core;
using System.Collections.Concurrent;

namespace OrynoSync.Core;

/// <summary>
/// Section I (corrective): Global transport scheduler.
/// Enforces max 3 concurrent file transfers globally across all mappings,
/// while respecting parent→child dependency ordering within each mapping.
/// Independent sibling operations run truly concurrent (not sequential).
/// </summary>
internal sealed class TransportScheduler : IDisposable
{
    private readonly SemaphoreSlim _globalGate;
    private int _activeCount;

    public TransportScheduler(int maxConcurrentTransfers = 3)
    {
        _globalGate = new SemaphoreSlim(maxConcurrentTransfers, maxConcurrentTransfers);
    }

    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// Acquires a transport slot. Returns a releaser that must be disposed when transfer completes.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _globalGate.WaitAsync(ct);
        Interlocked.Increment(ref _activeCount);
        return new SlotReleaser(this);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeCount);
        _globalGate.Release();
    }

    private sealed class SlotReleaser(TransportScheduler scheduler) : IDisposable
    {
        public void Dispose() => scheduler.Release();
    }

    public void Dispose() => _globalGate.Dispose();
}

/// <summary>
/// Groups operations into waves based on dependency ordering.
/// Wave 0: operations with no unmet dependencies (e.g. top-level files/dirs).
/// Wave 1+: operations whose parent was in a previous wave.
/// Operations within the same wave can execute concurrently.
/// </summary>
internal static class DependencyWavePlanner
{
    /// <summary>
    /// Splits pending operations into waves where each wave contains independent operations.
    /// A child operation cannot start until its parent directory operation completes.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MappingPendingOperation>> PlanWaves(
        IReadOnlyList<MappingPendingOperation> pending)
    {
        if (pending.Count == 0) return Array.Empty<IReadOnlyList<MappingPendingOperation>>();

        var pendingSet = pending.ToHashSet();
        var waves = new List<List<MappingPendingOperation>>();
        var completed = new HashSet<Guid>();

        while (pendingSet.Count > 0)
        {
            var wave = new List<MappingPendingOperation>();
            foreach (var op in pendingSet)
            {
                // Check if this op's parent path was completed in a previous wave
                var parentPath = GetParentPath(op.RelativePath);
                if (parentPath == null)
                {
                    // Top-level operation — always eligible
                    wave.Add(op);
                }
                else
                {
                    // Check if there's a pending parent operation
                    var parentPending = pendingSet.Any(o =>
                        o.Type == OperationType.CreateDirectory &&
                        string.Equals(o.RelativePath, parentPath, StringComparison.OrdinalIgnoreCase));

                    if (!parentPending)
                    {
                        // Parent either already completed or doesn't need an operation
                        var parentCompleted = completed.Contains(
                            pending.First(o => o.Type == OperationType.CreateDirectory &&
                                string.Equals(o.RelativePath, parentPath, StringComparison.OrdinalIgnoreCase)).OperationId);
                        if (parentCompleted || !pending.Any(o => o.Type == OperationType.CreateDirectory &&
                            string.Equals(o.RelativePath, parentPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            wave.Add(op);
                        }
                    }
                }
            }

            if (wave.Count == 0)
            {
                // All remaining ops have unmet dependencies — break to avoid infinite loop
                // This shouldn't happen with well-formed input, but guard against it
                wave.AddRange(pendingSet);
            }

            foreach (var op in wave)
            {
                pendingSet.Remove(op);
                completed.Add(op.OperationId);
            }

            waves.Add(wave);
        }

        return waves;
    }

    private static string? GetParentPath(string relativePath)
    {
        var parent = Path.GetDirectoryName(relativePath)?.Replace('/', '\\');
        return string.IsNullOrEmpty(parent) ? null : parent;
    }
}
