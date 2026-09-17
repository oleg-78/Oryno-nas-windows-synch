using OrynoSync.Core;
using System.Net;

namespace OrynoSync.Tests;

/// <summary>
/// §7/§9/§11/§16/§18/§19 (recursive audit): a local file that exists on disk must NEVER be accepted as
/// "already on the NAS" just because a metadata row says so. These tests pin the invariant that produced
/// the false "Everything is up to date" (WAITING = 0 / ERRORS = 0 while ~3 000 nested files were missing):
/// <list type="bullet">
/// <item>a local file without a *verified* remote counterpart always produces a repair operation;</item>
/// <item>metadata-only (phantom) remote rows are not proof — the content must be served;</item>
/// <item>the full consistency reconciliation runs at Start without any watcher event;</item>
/// <item>"Up to date" is forbidden while unsynced local items remain.</item>
/// </list>
/// </summary>
public class ConsistencyAuditTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"oryno-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SyncMapping NewMapping(string localPath, Guid rootId, string? destination = "Работа") =>
        new(Guid.NewGuid(), localPath, rootId, "root", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, "Normalized", null, null, MappingStatus.Syncing, null, destination);

    private static async Task<string> WriteFileAsync(string root, string relative, string content)
    {
        var path = Path.Combine(root, PathRules.NormalizeRelative(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    // ================= §7/§9: nested local files without a remote counterpart are always queued =====

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void R1_LocalNestedFile_AbsentRemote_QueuesCreateFile(int depth)
    {
        var root = NewRoot();
        try
        {
            var segments = Enumerable.Range(1, depth).Select(i => $"d{i}").ToArray();
            var rel = Path.Combine([.. segments, "deep.txt"]);
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "payload");

            var desired = new List<DesiredLocalItem>();
            for (var i = 1; i <= depth; i++)
            {
                var dirRel = Path.Combine(segments.Take(i).ToArray());
                desired.Add(new(dirRel, ItemType.Directory, 0, DateTimeOffset.UtcNow, null));
            }
            desired.Add(new(rel, ItemType.File, 7, DateTimeOffset.UtcNow, "hash"));

            var plan = QueueNormalizer.Plan(desired, [], []);

            Assert.Contains(plan.Operations, o => o.Type == OperationType.CreateFile && o.RelativePath == rel);
            for (var i = 1; i <= depth; i++)
            {
                var dirRel = Path.Combine(segments.Take(i).ToArray());
                Assert.Contains(plan.Operations, o => o.Type == OperationType.CreateDirectory && o.RelativePath == dirRel);
            }
            // §9: never a flattened copy at the root.
            Assert.DoesNotContain(plan.Operations, o => o.RelativePath == "deep.txt");
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §6/§7: stale SyncState=Synced + no remote counterpart => repair op ==========

    [Fact]
    public async Task R2_StaleSyncedItem_WithoutRemoteCounterpart_IsReplannedAndQueued()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"Ламинат\A\B\file.txt", "content");
            var rootId = Guid.NewGuid();
            var api = new AuditMockSyncApi();
            var store = new MockMappingStore();
            var remote = new AuditMockRemoteStateStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            // The client DB claims the file was synced long ago — the NAS never had it.
            await store.UpsertItemAsync(new(mapping.MappingId, @"Ламинат\A\B\file.txt", ItemType.File, 7, DateTimeOffset.UtcNow, SyncItemState.Synced));
            var coordinator = new MappingTransferCoordinator(store, remote, api, Path.GetTempPath());

            await coordinator.RebuildAsync(mapping);

            var pending = await store.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue);
            Assert.Contains(pending, o => o.Type == OperationType.CreateFile && o.RelativePath == @"Ламинат\A\B\file.txt");
            var items = await store.GetItemsAsync(mapping.MappingId);
            Assert.Equal(SyncItemState.Waiting, items.Single(x => x.RelativePath == @"Ламинат\A\B\file.txt").SyncState);
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §19: metadata-only (phantom) rows are not proof of a completed upload =======

    [Fact]
    public async Task R4_PhantomRemoteRow_ContentNotServed_StillQueuesTheUpload()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"Ламинат\A\file.txt", "content");
            var rootId = Guid.NewGuid();
            var api = new AuditMockSyncApi();
            var store = new MockMappingStore();
            var remote = new AuditMockRemoteStateStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            // A phantom row: the index says the file is there (root-relative path, correct destination)
            // with a matching hash, but the physical file is gone.
            var phantomId = Guid.NewGuid();
            remote.AddRemoteItem(new RemoteItemState(phantomId, rootId, null, "file.txt", @"Работа\Ламинат\A\file.txt",
                "file", 7, DateTimeOffset.UtcNow, "hash", 1, 0, RemotePlanningState.NeedsDownload));
            api.ContentMissing.Add(phantomId);
            var coordinator = new MappingTransferCoordinator(store, remote, api, Path.GetTempPath());

            await coordinator.RebuildAsync(mapping);

            var pending = await store.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue);
            Assert.Contains(pending, o => o.Type == OperationType.CreateFile && o.RelativePath == @"Ламинат\A\file.txt");
            var items = await store.GetItemsAsync(mapping.MappingId);
            Assert.Equal(SyncItemState.Waiting, items.Single(x => x.RelativePath == @"Ламинат\A\file.txt").SyncState);
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §12/§13: targeted repair must not re-upload matching files ==================

    [Fact]
    public async Task R5_TargetedRepair_DoesNotUploadAlreadyMatchingFiles()
    {
        var root = NewRoot();
        try
        {
            var full = await WriteFileAsync(root, @"Ламинат\matching.bin", "identical payload");
            var size = new FileInfo(full).Length;
            var hash = await new Blake3ContentHasher().ComputeAsync(full);
            var rootId = Guid.NewGuid();
            var api = new AuditMockSyncApi();
            var store = new MockMappingStore();
            var remote = new AuditMockRemoteStateStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            remote.AddRemoteItem(new RemoteItemState(Guid.NewGuid(), rootId, null, "matching.bin", @"Работа\Ламинат\matching.bin",
                "file", size, DateTimeOffset.UtcNow, hash, 1, 0, RemotePlanningState.NeedsDownload));
            var coordinator = new MappingTransferCoordinator(store, remote, api, Path.GetTempPath());

            await coordinator.RebuildAsync(mapping);

            var pending = await store.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue);
            Assert.DoesNotContain(pending, o => o.RelativePath == @"Ламинат\matching.bin");
            var items = await store.GetItemsAsync(mapping.MappingId);
            Assert.Equal(SyncItemState.Synced, items.Single(x => x.RelativePath == @"Ламинат\matching.bin").SyncState);
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §9: no `files\...` (double prefix) duplication into the plan ================

    [Fact]
    public async Task R6_LegacyFilesPrefix_LocalSubtreeNeverEntersThePlan()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"files\Ламинат\junk.txt", "junk");
            await WriteFileAsync(root, @"Ламинат\real.txt", "real");
            var rootId = Guid.NewGuid();
            var store = new MockMappingStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            var coordinator = new MappingTransferCoordinator(store, new AuditMockRemoteStateStore(), new AuditMockSyncApi(), Path.GetTempPath());

            var plan = await coordinator.RebuildAsync(mapping);

            Assert.True(PathRules.IsLegacyPrefixPath(@"files\Ламинат\junk.txt"));
            Assert.DoesNotContain(plan.Operations, o => PathRules.IsLegacyPrefixPath(o.RelativePath));
            var items = await store.GetItemsAsync(mapping.MappingId);
            Assert.DoesNotContain(items, x => PathRules.IsLegacyPrefixPath(x.RelativePath));
            Assert.Contains(plan.Operations, o => o.RelativePath == @"Ламинат\real.txt");
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §10/§18: deep parent chain is resolved, no root-level fallback ==============

    [Fact]
    public async Task R8_DeepParentChain_IsResolved_WithoutRootOrFilesFallback()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"A\B\C\file.txt", "deep");
            var rootId = Guid.NewGuid();
            var store = new MockMappingStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            var coordinator = new MappingTransferCoordinator(store, new AuditMockRemoteStateStore(), new AuditMockSyncApi(), Path.GetTempPath());

            var plan = await coordinator.RebuildAsync(mapping);
            var items = await store.GetItemsAsync(mapping.MappingId);

            foreach (var dir in new[] { "A", @"A\B", @"A\B\C" })
            {
                Assert.Contains(plan.Operations, o => o.Type == OperationType.CreateDirectory && o.RelativePath == dir);
                Assert.Contains(items, x => x.RelativePath == dir && x.ItemType == ItemType.Directory);
            }
            Assert.Contains(plan.Operations, o => o.Type == OperationType.CreateFile && o.RelativePath == @"A\B\C\file.txt");
            // §9: no flattened root copy and no `files\` prefix anywhere.
            Assert.DoesNotContain(plan.Operations, o => o.RelativePath == "file.txt");
            Assert.DoesNotContain(items, x => x.RelativePath == "file.txt");
            Assert.DoesNotContain(items, x => PathRules.IsLegacyPrefixPath(x.RelativePath));
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §11: the consistency pass runs at Start, without any watcher event =========

    [Fact]
    public async Task R7_StartupConsistencyScan_RunsWithoutWatcherEvent()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"Ламинат\untouched-since-forever.txt", "old");
            var store = new MockMappingStore();
            var mapping = NewMapping(root, Guid.NewGuid());
            await store.AddMappingAsync(mapping);
            var calls = 0;
            var manager = new SyncMappingRuntimeManager(store)
            {
                ConsistencyReconcile = (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; }
            };
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Progress += (_, p) => { if (p.IsComplete) complete.TrySetResult(); };

            await manager.StartAsync(mapping);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
            for (var i = 0; i < 40 && Volatile.Read(ref calls) == 0; i++) await Task.Delay(50);

            Assert.True(Volatile.Read(ref calls) >= 1, "the startup scan must end with a full consistency reconciliation");
            // §17: it is one-shot — not part of the periodic cadence.
            await Task.Delay(500);
            Assert.Equal(1, Volatile.Read(ref calls));
            manager.Dispose();
        }
        finally { Directory.Delete(root, true); }
    }

    // ================= §16: "Up to date" is forbidden while unsynced local items remain ===========

    [Fact]
    public void R3_UpToDate_IsForbidden_WhenLocalItemsHaveNoVerifiedRemoteCounterpart()
    {
        var idle = new SyncStatusInput(ConnectionState.Connected, PendingOperations: 0, ErrorCount: 0, UnsyncedLocalItems: 0);
        Assert.Equal(SyncUserStatus.UpToDate, SyncUserStatusPresenter.Resolve(idle).Status);

        var drift = idle with { UnsyncedLocalItems = 2948 };
        var resolved = SyncUserStatusPresenter.Resolve(drift);
        Assert.Equal(SyncUserStatus.SyncIssues, resolved.Status);
        Assert.NotEqual(SyncUserStatusPresenter.UpToDateText, resolved.Text);

        // Real work outranks the drift message while the queue is draining.
        var draining = drift with { PendingOperations = 5 };
        Assert.Equal(SyncUserStatus.Syncing, SyncUserStatusPresenter.Resolve(draining).Status);
    }

    [Fact]
    public async Task R3b_DashboardSummary_CountsUnsyncedLocalItems()
    {
        var db = Path.Combine(Path.GetTempPath(), $"oryno-audit-{Guid.NewGuid():N}.db");
        var store = new SqliteSyncMappingStore(db);
        await store.InitializeAsync();
        var mapping = NewMapping(Path.Combine(Path.GetTempPath(), $"oryno-audit-root-{Guid.NewGuid():N}"), Guid.NewGuid());
        await store.AddMappingAsync(mapping);
        await store.ReplaceItemsAsync(mapping.MappingId,
        [
            new(mapping.MappingId, "synced.txt", ItemType.File, 1, DateTimeOffset.UtcNow, SyncItemState.Synced),
            new(mapping.MappingId, @"Ламинат\a.txt", ItemType.File, 1, DateTimeOffset.UtcNow, SyncItemState.Waiting),
            new(mapping.MappingId, @"Ламинат\b.txt", ItemType.File, 1, DateTimeOffset.UtcNow, SyncItemState.Waiting),
            new(mapping.MappingId, "gone.txt", ItemType.File, 1, DateTimeOffset.UtcNow, SyncItemState.DeletedLocal)
        ]);
        var summary = await store.GetDashboardSummaryAsync();
        Assert.Equal(2, summary.UnsyncedLocalCount);
        Assert.Equal(0, summary.WaitingCount);   // the queue is empty — exactly the misleading state
        Assert.Equal(2, (await store.GetMappingSummariesAsync()).Single().UnsyncedLocalCount);
    }

    // ================= §19: legacy-path metadata alone never closes an operation ===================

    [Fact]
    public async Task R9_LegacyMetadataOnly_IsNotProof_OperationStaysActive()
    {
        var root = NewRoot();
        try
        {
            await WriteFileAsync(root, @"Ламинат\file.txt", "content");
            var rootId = Guid.NewGuid();
            var store = new MockMappingStore();
            var remote = new AuditMockRemoteStateStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            var op = new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.CreateFile,
                @"Ламинат\file.txt", null, DateTimeOffset.UtcNow, 3, DateTimeOffset.UtcNow, OperationState.Failed, "temporary server problem");
            await store.EnqueueAsync(op);
            // The row exists only at the pre-fix (destination-less) path — a phantom from the old layout.
            remote.AddRemoteItem(new RemoteItemState(Guid.NewGuid(), rootId, null, "file.txt", @"Ламинат\file.txt",
                "file", 7, DateTimeOffset.UtcNow, "hash", 1, 0, RemotePlanningState.MetadataOnly));

            // 1) content cannot be served → the operation must NOT be closed as "already uploaded".
            var unverified = await ErrorReconcileRunner.ReconcileAsync(store, remote, [mapping], null,
                (_, _) => Task.FromResult(false));
            Assert.Equal(0, await store.CountHistoricalErrorsAsync());
            Assert.Equal(1, unverified.Total);

            // 2) content verified present at the legacy path → closing it is legitimate (no duplicate upload).
            await ErrorReconcileRunner.ReconcileAsync(store, remote, [mapping], null, (_, _) => Task.FromResult(true));
            Assert.Equal(1, await store.CountHistoricalErrorsAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task R10_DirectoriesWithRemoteCounterpart_AreSynced_SoConsistencyCanReachZero()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "A"));
            await WriteFileAsync(root, @"A\f.txt", "x");
            var rootId = Guid.NewGuid();
            var store = new MockMappingStore();
            var remote = new AuditMockRemoteStateStore();
            var mapping = NewMapping(root, rootId);
            await store.AddMappingAsync(mapping);
            remote.AddRemoteItem(new RemoteItemState(Guid.NewGuid(), rootId, null, "A", @"Работа\A",
                "directory", null, null, null, 1, 0, RemotePlanningState.MetadataOnly));
            var api = new AuditMockSyncApi();
            api.ExistingItemId = Guid.Empty; // the mock inventory must not inject extra items
            var coordinator = new MappingTransferCoordinator(store, remote, api, Path.GetTempPath());

            await coordinator.RebuildAsync(mapping);

            var items = await store.GetItemsAsync(mapping.MappingId);
            Assert.Equal(SyncItemState.Synced, items.Single(x => x.RelativePath == "A" && x.ItemType == ItemType.Directory).SyncState);
            Assert.Equal(SyncItemState.Waiting, items.Single(x => x.RelativePath == @"A\f.txt").SyncState);
            // …and no directory operation is queued for the path the server already has.
            var pending = await store.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue);
            Assert.DoesNotContain(pending, o => o.Type == OperationType.CreateDirectory && o.RelativePath == "A");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void R9b_SyncApiException_CarriesTheContentMissingCode()
    {
        var ex = new SyncApiException(HttpStatusCode.NotFound, "SYNC_CONTENT_MISSING", "content missing on storage");
        Assert.Equal("SYNC_CONTENT_MISSING", ex.Code);
    }
}
