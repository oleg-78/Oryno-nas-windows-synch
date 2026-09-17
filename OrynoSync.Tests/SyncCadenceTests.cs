using System.Diagnostics;
using OrynoSync.Core;

namespace OrynoSync.Tests;

/// <summary>
/// Event-driven local sync + minute-cadence remote poll: the cadence primitives (poll schedule, wake signal,
/// queue drain) and the watcher paths that feed them.
/// </summary>
public class SyncCadenceTests
{
    private static SyncMapping Mapping(string root) => new(Guid.NewGuid(), root, Guid.NewGuid(), "Prod", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "NotStarted", null, null, MappingStatus.Stopped);

    private static async Task<(SqliteSyncMappingStore Store, SyncMapping Mapping, SyncMappingRuntimeManager Manager, SyncWakeSignal Wake, LocalMutationSuppression Suppression, string Root, string Db)> StartedRuntime()
    {
        var db = Path.Combine(Path.GetTempPath(), "oryno-env-" + Guid.NewGuid() + ".db");
        var root = Path.Combine(Path.GetTempPath(), "oryno-env-root-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base");
        var store = new SqliteSyncMappingStore(db);
        await store.InitializeAsync();
        var mapping = Mapping(root);
        await store.AddMappingAsync(mapping);
        var wake = new SyncWakeSignal();
        var suppression = new LocalMutationSuppression();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new SyncMappingRuntimeManager(store) { Wake = wake, Suppression = suppression };
        manager.Progress += (_, p) => { if (p.IsComplete) done.TrySetResult(); };
        await manager.StartAsync(mapping);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (store, mapping, manager, wake, suppression, root, db);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> check, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await check()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<IReadOnlyList<MappingPendingOperation>> Pending(SqliteSyncMappingStore store, Guid mappingId) =>
        await store.GetPendingAsync(mappingId, DateTimeOffset.MaxValue);

    // ---------------- cadence ----------------

    [Fact] public void RemoteIdlePollIsSixtySecondsNotFour()
    {
        SyncCadence.ResetForTests();
        Assert.Equal(TimeSpan.FromMinutes(1), SyncCadence.RemotePollInterval);
    }

    [Fact] public void StartupPerformsImmediatePoll()
    {
        var schedule = new RemotePollSchedule();
        Assert.Equal(TimeSpan.Zero, schedule.WaitBeforeNextPoll());
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.WaitBeforeNextPoll());
    }

    [Fact] public void ReconnectAndUserStartPerformImmediatePoll()
    {
        var schedule = new RemotePollSchedule();
        schedule.WaitBeforeNextPoll();                       // startup poll consumed
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.WaitBeforeNextPoll());
        foreach (var reason in new[] { "reconnect", "user-start", "destination-changed" })
        {
            schedule.RequestImmediate(reason);
            Assert.Equal(TimeSpan.Zero, schedule.WaitBeforeNextPoll());
            Assert.Equal(TimeSpan.FromMinutes(1), schedule.WaitBeforeNextPoll());
        }
    }

    [Fact] public async Task LocalChangeWakesTheSchedulerImmediately()
    {
        var wake = new SyncWakeSignal();
        var sw = Stopwatch.StartNew();
        var waiting = wake.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        await Task.Delay(200);
        wake.Pulse();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"woke after {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, wake.PulseCount);
    }

    [Fact] public async Task WokenSignalDoesNotLeakIntoLaterWaits()
    {
        var wake = new SyncWakeSignal();
        for (var i = 0; i < 5; i++) wake.Pulse();
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await wake.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None));
    }

    [Fact] public async Task ActiveQueueDoesNotWaitForNextPoll()
    {
        SyncCadence.ResetForTests();
        var remaining = 3;
        var passes = 0;
        var sw = Stopwatch.StartNew();
        var drainer = new QueueDrainer();
        var result = await drainer.DrainAsync(_ =>
        {
            passes++;
            return Task.FromResult(new QueueDrainResult(remaining--, 0, false));
        }, CancellationToken.None, recheck: TimeSpan.FromMilliseconds(20));
        Assert.Equal(4, passes);
        Assert.Equal(0, result.PendingOperations);
        Assert.True(sw.Elapsed < SyncCadence.RemotePollInterval, "queue must not wait for the idle poll");
    }

    [Fact] public async Task StalledQueueGivesTheLoopBackInsteadOfSpinning()
    {
        // Operations parked on their own retry back-off keep the queue non-empty: the drain must stop.
        var drainer = new QueueDrainer();
        var passes = 0;
        var result = await drainer.DrainAsync(_ => { passes++; return Task.FromResult(new QueueDrainResult(5, 0, false)); },
            CancellationToken.None, recheck: TimeSpan.FromMilliseconds(10));
        Assert.Equal(4, passes);                 // first observation + 3 stalled passes
        Assert.Equal(5, result.PendingOperations);
    }

    [Fact] public async Task EmptyQueueDrainsInOnePass()
    {
        var drainer = new QueueDrainer();
        var result = await drainer.DrainAsync(_ => Task.FromResult(new QueueDrainResult(0, 0, false)), CancellationToken.None);
        Assert.Equal(1, drainer.Passes);
        Assert.Equal(0, result.PendingOperations);
    }

    // ---------------- watcher ----------------

    [Fact] public void MaterialisationWindowSwallowsEveryEchoThenExpires()
    {
        var previous = LocalMutationSuppression.MaterialisationWindow;
        try
        {
            LocalMutationSuppression.MaterialisationWindow = TimeSpan.FromMilliseconds(150);
            var suppression = new LocalMutationSuppression();
            suppression.Expect("dl.txt", 10, DateTimeOffset.UtcNow);
            // Not consumed: Windows raises Created + Changed (+ often more) for one download.
            Assert.True(suppression.IsMaterialisedRecently("dl.txt"));
            Assert.True(suppression.IsMaterialisedRecently("dl.txt"));
            Assert.False(suppression.IsMaterialisedRecently("other.txt"));
            Thread.Sleep(250);
            Assert.False(suppression.IsMaterialisedRecently("dl.txt"));   // a real user edit after the window is not swallowed
        }
        finally { LocalMutationSuppression.MaterialisationWindow = previous; }
    }

    [Fact] public async Task LocalChangeQueuesOnlyThatPath()
    {
        var env = await StartedRuntime();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(env.Root, "saved.docx"), "v1");
            Assert.True(await WaitForAsync(async () => (await Pending(env.Store, env.Mapping.MappingId)).Any(o => o.RelativePath == "saved.docx")));
            var pending = await Pending(env.Store, env.Mapping.MappingId);
            Assert.Single(pending, o => o.RelativePath == "saved.docx");
            Assert.DoesNotContain(pending, o => o.RelativePath == "base.txt" && o.Type != OperationType.CreateFile && o.Type != OperationType.UpdateFile);
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task RepeatedEventsOnOnePathCoalesceToSingleOperation()
    {
        var env = await StartedRuntime();
        try
        {
            var path = Path.Combine(env.Root, "word.docx");
            for (var i = 0; i < 5; i++) { await File.WriteAllTextAsync(path, "v" + i); await Task.Delay(60); }
            Assert.True(await WaitForAsync(async () => (await Pending(env.Store, env.Mapping.MappingId)).Any(o => o.RelativePath == "word.docx")));
            await Task.Delay(1500);
            Assert.Single(await Pending(env.Store, env.Mapping.MappingId), o => o.RelativePath == "word.docx");
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task LocalDeleteCreatesDeleteIntent()
    {
        var env = await StartedRuntime();
        try
        {
            File.Delete(Path.Combine(env.Root, "base.txt"));
            Assert.True(await WaitForAsync(async () => (await Pending(env.Store, env.Mapping.MappingId)).Any(o => o.Type == OperationType.Delete && o.RelativePath == "base.txt")));
            var items = await env.Store.GetItemsAsync(env.Mapping.MappingId);
            Assert.Contains(items, i => i.RelativePath == "base.txt" && i.SyncState == SyncItemState.DeletedLocal);
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task LocalRenameCreatesMove()
    {
        var env = await StartedRuntime();
        try
        {
            File.Move(Path.Combine(env.Root, "base.txt"), Path.Combine(env.Root, "renamed.txt"));
            // A safe Move, not Delete + Upload: old path in RelativePath, new path in SecondaryPath.
            Assert.True(await WaitForAsync(async () => (await Pending(env.Store, env.Mapping.MappingId)).Any(o => o.Type == OperationType.Move && o.RelativePath == "base.txt" && o.SecondaryPath == "renamed.txt")));
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task DownloadMaterialisationIsNotUploadedBack()
    {
        var env = await StartedRuntime();
        try
        {
            // What the transfer coordinator does after writing a downloaded file: register it, then the
            // watcher fires Created/Changed. Nothing may be queued for it (feedback loop = 0).
            // The coordinator claims the path BEFORE writing (see MappingTransferCoordinator), so the watcher
            // event that fires during/after the write is swallowed instead of uploaded back.
            var path = Path.Combine(env.Root, "from-server.txt");
            var expectedMtime = DateTimeOffset.UtcNow.AddMinutes(-5);
            env.Suppression.Expect("from-server.txt", 14, expectedMtime);
            await File.WriteAllTextAsync(path, "server content");
            File.SetLastWriteTimeUtc(path, expectedMtime.UtcDateTime);
            await Task.Delay(2500);
            Assert.DoesNotContain(await Pending(env.Store, env.Mapping.MappingId), o => o.RelativePath == "from-server.txt");
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task IgnoredAndTempFilesProduceNoOperation()
    {
        var env = await StartedRuntime();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(env.Root, "~$open.docx"), "open");
            await File.WriteAllTextAsync(Path.Combine(env.Root, "partial.crdownload"), "partial");
            await File.WriteAllTextAsync(Path.Combine(env.Root, "draft.tmp"), "tmp");
            await Task.Delay(2500);
            var pending = await Pending(env.Store, env.Mapping.MappingId);
            Assert.DoesNotContain(pending, o => o.RelativePath.Contains("~$") || o.RelativePath.EndsWith(".crdownload") || o.RelativePath.EndsWith(".tmp"));
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task LocalChangeWakesTheSyncLoop()
    {
        var env = await StartedRuntime();
        try
        {
            var pulsesBefore = env.Wake.PulseCount;
            await File.WriteAllTextAsync(Path.Combine(env.Root, "wake-proof.txt"), "work");
            Assert.True(await WaitForAsync(async () => env.Wake.PulseCount > pulsesBefore));
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task RepeatedRescanRequestsRunOneFullReconcile()
    {
        var env = await StartedRuntime();
        try
        {
            var completions = 0;
            env.Manager.Progress += (_, p) => { if (p.IsComplete) Interlocked.Increment(ref completions); };
            for (var i = 0; i < 5; i++) env.Manager.RequestRescan("overflow");
            await WaitForAsync(() => Task.FromResult(Volatile.Read(ref completions) >= 1), 20000);
            await Task.Delay(1500);
            Assert.Equal(1, Volatile.Read(ref completions));   // the scan gate coalesces the burst
            Assert.Equal(1, (await Pending(env.Store, env.Mapping.MappingId)).Count(o => o.RelativePath == "base.txt" && o.Type is OperationType.CreateFile or OperationType.UpdateFile));
        }
        finally { env.Manager.Dispose(); Directory.Delete(env.Root, true); }
    }

    [Fact] public async Task OfflineLocalChangeSurvivesUntilRestart()
    {
        var env = await StartedRuntime();
        try
        {
            File.Delete(Path.Combine(env.Root, "base.txt"));
            Assert.True(await WaitForAsync(async () => (await Pending(env.Store, env.Mapping.MappingId)).Any(o => o.Type == OperationType.Delete && o.RelativePath == "base.txt")));
            env.Manager.Dispose();
            var reopened = new SqliteSyncMappingStore(env.Db);
            await reopened.InitializeAsync();
            Assert.Contains(await Pending(reopened, env.Mapping.MappingId), o => o.Type == OperationType.Delete && o.RelativePath == "base.txt");
        }
        finally { Directory.Delete(env.Root, true); }
    }
}
