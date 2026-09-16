using OrynoSync.Core;
using System.Collections.Concurrent;

namespace OrynoSync.Core;

/// <summary>
/// Section C: Authoritative duplicate resolver for canonical paths.
/// Instead of silently taking First(), this resolver logs diagnostics and 
/// deterministically picks the authoritative item.
/// </summary>
internal static class DuplicateCanonicalResolver
{
    public record Resolution(IReadOnlyList<RemoteItemState> Items, IReadOnlyList<string> Conflicts);

    /// <summary>
    /// Resolves duplicate remote items by canonical path.
    /// For multiple live items: marks as Conflict and does NOT pick a winner —
    /// mutations on ambiguous paths are blocked until reconciliation.
    /// </summary>
    public static Resolution ResolveRemote(
        IReadOnlyList<RemoteItemState> remoteItems,
        Action<string, Guid, string> logDiagnostic)
    {
        var groups = remoteItems
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<RemoteItemState>();
        var conflicts = new List<string>();

        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count == 1)
            {
                items.Add(list[0]);
                continue;
            }

            // Multiple items with same canonical path
            var deleted = list.Where(x => x.IsDeleted).ToList();
            var live = list.Where(x => !x.IsDeleted).ToList();

            if (live.Count == 0)
            {
                // All deleted - pick the one with highest revision
                var best = list.OrderByDescending(x => x.LastRevision).ThenByDescending(x => x.Version).First();
                items.Add(best);
            }
            else if (live.Count == 1)
            {
                // Exactly one live - authoritative
                items.Add(live[0]);
            }
            else
            {
                // MULTIPLE LIVE ITEMS = CONFLICT
                // Do NOT pick a winner — block all mutations on this path
                conflicts.Add(g.Key);
                // Log diagnostic
                var ids = string.Join(",", list.Select(x => x.ItemId.ToString("N").Substring(0, 8)));
                logDiagnostic?.Invoke(g.Key, list[0].RootId, ids);
                // Do NOT add to items — ambiguous paths are blocked from mutation
            }
        }

        return new Resolution(items, conflicts);
    }
}

public sealed class MappingTransferCoordinator(ISyncMappingStore mappings, IRemoteStateStore remoteState, IContentTransferApi api, string tempRoot)
{
    private readonly SqliteTransferSessionStore _sessions = new(Path.Combine(tempRoot, "transfer-state.db"));
    private readonly ResumableTransferClient _transfers = new(api, tempRoot, 3, new SqliteTransferSessionStore(Path.Combine(tempRoot, "transfer-state.db")));
    private readonly TransportScheduler _scheduler = new(3);
    private readonly SemaphoreSlim _hashGate = new(2);
    private static readonly IgnoreRules _ignores = new();
    private readonly IContentHasher _hasher = new Blake3ContentHasher();
    public event Action<SyncActivityEvent>? Activity;
    public event Action<SyncActivityEvent>? DiagnosticLog;

    private readonly HashSet<string> _blockedCanonicalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DuplicateDiagnosticDedup _diagnosticDedup = new();

    public int ActiveTransfers => _scheduler.ActiveCount;

    public async Task RemoveMappingAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        await mappings.RemoveMappingAsync(mapping.MappingId, ct);
        await _sessions.RemoveForMappingAsync(mapping.MappingId, ct);
        if (mapping.ServerRootId is Guid rootId) await remoteState.RemoveRootStateAsync(rootId, ct);
    }

    public async Task<QueueNormalizationResult> RebuildAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        if (mapping.ServerRootId is not Guid rootId) throw new InvalidOperationException("Select an Oryno NAS folder before rebuilding sync state.");
        var remoteItems = (await remoteState.GetRemoteItemsAsync(rootId, ct))
            .Where(x => !x.IsDeleted && mapping.InDestination(x.RelativePath))
            .Select(x => { var l = mapping.ToLocalRel(x.RelativePath); return l is null ? null : x with { RelativePath = l }; })
            .Where(x => x is not null).Select(x => x!).ToArray();
        
        // Section C: Authoritative duplicate resolution instead of GroupBy.First
        var remoteResolution = DuplicateCanonicalResolver.ResolveRemote(remoteItems,
            (path, rootId, ids) => LogDiagnosticDeduped("REBUILD_DUPLICATE", mapping.MappingId, path, ids));
        
        if (remoteResolution.Conflicts.Count > 0)
        {
            var details = string.Join("; ", remoteResolution.Conflicts.Select(d => d));
            await RecordAsync(mapping, "", "Rebuild", "Info", $"Resolved {remoteResolution.Conflicts.Count} duplicate canonical paths: {details}", ct);
        }
        
        var remote = remoteResolution.Items;
        var oldPending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue, ct);
        var desired = await SnapshotAsync(mapping.LocalPath, ct);
        var plan = QueueNormalizer.Plan(desired, remote, oldPending);
        
        var normalized = new List<MappingPendingOperation>();
        // Add conflicts from duplicate resolution
        normalized.AddRange(remoteResolution.Conflicts.Select(path => 
            new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, 
                null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.FailedPermanent, 
                "SYNC_CONFLICT: duplicate items with different content on server.")));
        // Add conflicts from queue normalizer
        normalized.AddRange(plan.Conflicts.Select(path => 
            new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, 
                null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.FailedPermanent, 
                "SYNC_CONFLICT: local and NAS content differ.")));
        normalized.AddRange(plan.Operations.Select(operation => 
            new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, operation.Type, operation.RelativePath, 
                null, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null)));
        
        // Section 4 fix: Explicit canonical collision handling for remote items
        var remoteByPath = new Dictionary<string, RemoteItemState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in remote.GroupBy(x => PathRules.NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase))
        {
            var groupItems = group.ToList();
            if (groupItems.Count == 1)
            {
                remoteByPath[group.Key] = groupItems[0];
            }
            else
            {
                // Multiple items with same canonical path — log diagnostic, pick deterministic winner for READ-ONLY use
                // Mutation on this path is blocked by DuplicateCanonicalResolver
                var winner = groupItems.OrderByDescending(x => x.Version).ThenByDescending(x => x.LastRevision).First();
                remoteByPath[group.Key] = winner;
                var ids = string.Join(",", groupItems.Select(x => x.ItemId.ToString("N").Substring(0, 8)));
                LogDiagnosticDeduped("REBUILD_CANONICAL_COLLISION", mapping.MappingId, group.Key, ids);
            }
        }
        var items = desired.Select(item => new MappingLocalItem(mapping.MappingId, item.RelativePath, item.ItemType, item.Size, item.Mtime, item.ItemType == ItemType.File && remoteByPath.TryGetValue(item.RelativePath, out var r) && string.Equals(item.ContentHash, r.ContentHash, StringComparison.OrdinalIgnoreCase) ? SyncItemState.Synced : SyncItemState.Waiting)).ToArray();
        await mappings.ReplaceItemsAsync(mapping.MappingId, items, ct);
        await mappings.ReplacePendingAsync(mapping.MappingId, normalized, ct);
        await mappings.UpdateMappingAsync(mapping with { InventoryState = "Normalized", Status = normalized.Count == 0 ? MappingStatus.UpToDate : MappingStatus.ReadyToSync, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return plan;
    }

    public async Task ProcessAsync(IReadOnlyList<SyncMapping> allMappings, CancellationToken ct = default)
    {
        // Section I (corrective): Process multiple mappings in parallel
        // Each mapping's operations are scheduled with dependency-aware concurrency
        var tasks = allMappings
            .Where(x => x.Enabled && x.ServerRootId is not null && x.Status != MappingStatus.Paused)
            .Select(mapping => ProcessMappingAsync(mapping, ct))
            .ToList();
        
        await Task.WhenAll(tasks);
    }

    private async Task ProcessMappingAsync(SyncMapping mapping, CancellationToken ct)
    {
        var rootId = mapping.ServerRootId!.Value;
        // Section C: Authoritative duplicate resolution
        var remoteItems = (await remoteState.GetRemoteItemsAsync(rootId, ct)).Where(x => !x.IsDeleted).ToArray();
        var remoteResolution = DuplicateCanonicalResolver.ResolveRemote(remoteItems,
            (path, rootId, ids) => LogDiagnosticDeduped("PROCESS_DUPLICATE", mapping.MappingId, path, ids));
        var remote = remoteResolution.Items.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        
        // FIX: Track blocked canonical paths to prevent any mutation operations on them
        _blockedCanonicalPaths.Clear();
        foreach (var conflict in remoteResolution.Conflicts)
        {
            _blockedCanonicalPaths.Add(PathRules.NormalizeRelative(conflict));
        }
        
        if (remoteResolution.Conflicts.Count > 0)
        {
            var details = string.Join("; ", remoteResolution.Conflicts.Select(d => d));
            await RecordAsync(mapping, "", "Sync", "Info", $"Blocked {remoteResolution.Conflicts.Count} ambiguous paths from mutation: {details}", ct);
        }
        
        // FIX: Block existing pending operations on conflict paths BEFORE scheduler
        await BlockPendingOperationsOnConflictPathsAsync(mapping, ct);
        
        // Section 4 fix: Explicit canonical collision handling for local items
        var local = new Dictionary<string, MappingLocalItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in (await mappings.GetItemsAsync(mapping.MappingId, ct)).GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                local[group.Key] = items[0];
            }
            else
            {
                // Multiple local items with same canonical path — pick deterministic winner, log diagnostic
                var winner = items.OrderByDescending(x => x.Mtime).First();
                local[group.Key] = winner;
                var ids = string.Join(",", items.Select(x => x.RelativePath));
                LogDiagnosticDeduped("PROCESS_CANONICAL_COLLISION", mapping.MappingId, group.Key, ids);
            }
        }
        var pending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.UtcNow, ct);
        
        if (!string.Equals(mapping.InventoryState, "Normalized", StringComparison.OrdinalIgnoreCase))
        {
            var desired = await SnapshotAsync(mapping.LocalPath, ct);
            var remoteInDestination = (await remoteState.GetRemoteItemsAsync(rootId, ct))
                .Where(x => !x.IsDeleted && mapping.InDestination(x.RelativePath))
                .Select(x => { var rel = mapping.ToLocalRel(x.RelativePath); return rel is null ? null : x with { RelativePath = rel }; })
                .Where(x => x is not null).Select(x => x!).ToArray();
            var plan = QueueNormalizer.Plan(desired, remoteInDestination, pending);
            var normalized = new List<MappingPendingOperation>();
            foreach (var dup in remoteResolution.Conflicts)
            {
                var localRel = mapping.ToLocalRel(dup);
                if (localRel is not null)
                {
                    normalized.Add(new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, 
                        localRel, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.FailedPermanent, 
                        "SYNC_CONFLICT: duplicate items with different content on server."));
                }
            }
            normalized.AddRange(plan.Conflicts.Select(path => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.FailedPermanent, "SYNC_CONFLICT: local and NAS content differ.")));
            normalized.AddRange(plan.Operations.Select(operation => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, operation.Type, operation.RelativePath, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null)));
            await mappings.ReplacePendingAsync(mapping.MappingId, normalized, ct);
            await mappings.UpdateMappingAsync(mapping with { InventoryState = "Normalized", UpdatedAt = DateTimeOffset.UtcNow }, ct);
            pending = normalized.Where(x => x.State is OperationState.Pending or OperationState.Failed or OperationState.FailedPermanent).ToArray();
        }
        
        // Re-read status from DB to get fresh state
        var freshMapping = await mappings.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        if (freshMapping.Status is MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync or MappingStatus.Stopped) return;
        
        // Section I (corrective): Process operations with dependency-aware concurrency
        // Group into waves where independent operations run truly concurrent
        var waves = DependencyWavePlanner.PlanWaves(pending);
        
        foreach (var wave in waves)
        {
            if (wave.Count == 1)
            {
                // Single operation — no need for concurrency overhead
                await ProcessOperationSafeAsync(mapping, rootId, wave[0], remote, local, ct);
            }
            else
            {
                // Section I fix: Bounded execution — max 3 concurrent operations per wave
                // Uses Parallel.ForEachAsync to avoid allocating thousands of Task objects
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 3,
                    CancellationToken = ct
                };
                await Parallel.ForEachAsync(wave, options, async (op, ct) =>
                {
                    await ProcessOperationSafeAsync(mapping, rootId, op, remote, local, ct);
                });
            }
        }
        
        await ApplyRemoteOnlyAsync(mapping, rootId, remote, local, ct);
    }

    private async Task ProcessOperationSafeAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        try 
        { 
            await ProcessOperationAsync(mapping, rootId, op, remote, local, ct); 
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Section F: Proper operation state model - separate transient from permanent
            var attempts = op.AttemptCount + 1;
            var isPermanent = IsPermanentFailure(ex);
            
            // Section 6 fix: Check SYNC_ITEM_NOT_FOUND and SYNC_CONTENT_MISSING BEFORE isPermanent
            if (ex is SyncApiException apiNotFound && apiNotFound.Code == "SYNC_ITEM_NOT_FOUND")
            {
                // Idempotent delete — item already gone
                await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = ex.Message, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
                await RecordAsync(mapping, op.RelativePath, op.Type.ToString(), "Completed", $"Item already absent: {ex.Message}", ct);
            }
            else if (ex is SyncApiException apiContentMissing && apiContentMissing.Code == "SYNC_CONTENT_MISSING")
            {
                // Section 6 fix: Stale remote download metadata — mark remote missing, reconcile
                // FIX: Must pass serverItemId (GUID), NOT RelativePath
                var serverItemId = ResolveServerItemIdForOperation(op, remote);
                if (serverItemId is not null)
                {
                    await remoteState.MarkItemMissingAsync(rootId, serverItemId, ct);
                    // Also mark in remote dictionary to prevent reprocessing in same cycle
                    MarkRemoteItemDeleted(remote, op.RelativePath);
                }
                await mappings.UpdateOperationAsync(op with { State = OperationState.FailedPermanent, AttemptCount = attempts, LastError = ex.Message, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
                await RecordAsync(mapping, op.RelativePath, op.Type.ToString(), "Error", $"Content missing, marked for reconcile: {ex.Message}", ct);
            }
            else if (isPermanent)
            {
                // Permanent failure: FailedPermanent + NextAttemptAt = MaxValue
                await mappings.UpdateOperationAsync(op with 
                { 
                    State = OperationState.FailedPermanent, 
                    AttemptCount = attempts, 
                    LastError = ex.Message, 
                    NextAttemptAt = DateTimeOffset.MaxValue 
                }, ct);
                await RecordAsync(mapping, op.RelativePath, op.Type.ToString(), "Error", ex.Message, ct);
            }
            else
            {
                // Transient failure: Pending with retry backoff
                // Section 5 fix: FileChangedDuringTransferException is retryable
                var backoffSeconds = Math.Min(300, Math.Pow(2, attempts));
                await mappings.UpdateOperationAsync(op with 
                { 
                    State = OperationState.Pending, 
                    AttemptCount = attempts, 
                    LastError = ex.Message, 
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds) 
                }, ct);
                await RecordAsync(mapping, op.RelativePath, op.Type.ToString(), "Retrying", 
                    $"Attempt {attempts}, retry in {backoffSeconds}s: {ex.Message}", ct);
            }
        }
    }

    /// <summary>
    /// Section F: Determines if an exception represents a permanent (non-retryable) failure.
    /// Section 5 fix: FileChangedDuringTransferException is NOT permanent — it's retryable.
    /// </summary>
    private static bool IsPermanentFailure(Exception ex)
    {
        return ex switch
        {
            SyncApiException apiError => apiError.Code is "SYNC_CONFLICT" or "SYNC_NAME_CONFLICT" 
                or "SYNC_HASH_MISMATCH" or "SYNC_NAME_INVALID",
            // FileChangedDuringTransferException is RETRYABLE, not permanent
            _ => false
        };
    }

    private static async Task<IReadOnlyList<DesiredLocalItem>> SnapshotAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<DesiredLocalItem>(); var hasher = new Blake3ContentHasher();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested(); var rel = PathRules.ToRelative(root, path); if (!PathRules.IsWindowsCompatible(rel) || _ignores.IsIgnored(rel)) continue;
            if (Directory.Exists(path)) { result.Add(new(rel, ItemType.Directory, 0, Directory.GetLastWriteTimeUtc(path), null)); continue; }
            try
            {
                var info = new FileInfo(path);
                result.Add(new(rel, ItemType.File, info.Length, info.LastWriteTimeUtc, await hasher.ComputeAsync(path, ct)));
            }
            catch (IOException) { /* file locked: skip */ }
            catch (UnauthorizedAccessException) { /* protected: skip */ }
        }
        return result;
    }

    private async Task ProcessOperationAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op, 
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var rel = PathRules.NormalizeRelative(op.RelativePath);
        
        // FIX: Block any mutation operation on a duplicate canonical path
        if (IsMutationOperation(op.Type) && _blockedCanonicalPaths.Contains(rel))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                $"Duplicate server path requires reconciliation: {op.RelativePath}");
        }
        
        if (_ignores.IsIgnored(rel) || (op.Type == OperationType.Move && op.SecondaryPath is not null && _ignores.IsIgnored(op.SecondaryPath)))
        {
            await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = null, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
            return;
        }
        if (!PathRules.IsWindowsCompatible(rel)) throw new IOException("invalid filename: Windows cannot materialize this path.");
        var remoteRel = mapping.Scope(rel);
        remote.TryGetValue(remoteRel, out var existing);
        
        switch (op.Type)
        {
            case OperationType.CreateDirectory:
                await ProcessCreateDirectoryAsync(mapping, rootId, op, remote, remoteRel, existing, ct);
                break;
            case OperationType.CreateFile:
            case OperationType.UpdateFile:
                await ProcessCreateFileAsync(mapping, rootId, op, remote, local, remoteRel, existing, ct);
                break;
            case OperationType.Move:
                await ProcessMoveAsync(mapping, rootId, op, remote, local, ct);
                break;
            case OperationType.Delete:
                await ProcessDeleteAsync(mapping, op, remote, remoteRel, ct);
                break;
            case OperationType.DownloadDirectory:
                Directory.CreateDirectory(PathRules.ToAbsolute(mapping.LocalPath, rel));
                await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.Directory, 0, DateTimeOffset.UtcNow, SyncItemState.Synced), ct);
                break;
            case OperationType.DownloadFile:
                if (existing is null || existing.ContentHash is null) throw new SyncApiException(System.Net.HttpStatusCode.NotFound, "SYNC_CONTENT_MISSING", "Remote content metadata is unavailable.");
                var currentItem = await api.GetItemAsync(existing.ItemId, ct);
                var downloadPath = PathRules.ToAbsolute(mapping.LocalPath, rel); Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
                // Section I fix: Acquire transport slot for concurrent download
                using (await _scheduler.AcquireAsync(ct))
                {
                    await _transfers.DownloadAsync(new DownloadTarget(currentItem.ItemId, currentItem.Version, rel, currentItem.SizeBytes ?? 0, currentItem.ContentHash ?? existing.ContentHash, currentItem.MtimeUtc), downloadPath, ct);
                }
                var downloaded = new FileInfo(downloadPath); await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.File, downloaded.Length, downloaded.LastWriteTimeUtc, SyncItemState.Synced), ct); 
                await RecordAsync(mapping, rel, "Downloaded", "Synced", null, ct);
                // Section 9 fix: Update last successful file sync on download too
                await UpdateLastSuccessfulFileSync(mapping);
                break;
            case OperationType.Conflict:
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT", "This item was changed both locally and on Oryno NAS.");
        }
        
        await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = null, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
    }

    /// <summary>
    /// Section A: CreateDirectory with proper handling of existing directories.
    /// Section 2 fix: FindActualServerItemAsync now does real server refresh.
    /// </summary>
    private async Task ProcessCreateDirectoryAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, string remoteRel, RemoteItemState? existing, CancellationToken ct)
    {
        // Case 1: Remote cache already has this directory → satisfied
        if (existing is not null && existing.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
        {
            await RecordAsync(mapping, op.RelativePath, "CreateDirectory", "Satisfied", "Directory already exists on server", ct);
            return;
        }
        
        // Case 2: Remote cache has a FILE where we expect a DIRECTORY → conflict
        if (existing is not null && existing.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT", 
                $"Expected directory but found file at {remoteRel}");
        }
        
        // Case 3: Not in cache → try to create
        try
        {
            var parentItemId = await EnsureParentAsync(rootId, remote, remoteRel, ct);
            var folder = await api.CreateFolderAsync(rootId, parentItemId, Path.GetFileName(remoteRel), op.OperationId, ct);
            
            remote[remoteRel] = new RemoteItemState(folder.ItemId, rootId, folder.ParentItemId, folder.Name, 
                folder.RelativePath, folder.ItemType, folder.SizeBytes, folder.MtimeUtc, folder.ContentHash, 
                folder.Version, 0, RemotePlanningState.MetadataOnly);
            
            await RecordAsync(mapping, op.RelativePath, "Created", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
        {
            // Section 2 fix: Real server refresh — fetch authoritative item from server
            var actualItem = await FindActualServerItemAsync(rootId, remoteRel, remote, ct);
            
            if (actualItem is null)
            {
                // Truly transient — retry later
                throw;
            }
            
            if (!actualItem.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Name conflict: existing item at {remoteRel} is {actualItem.ItemType}, expected directory");
            }
            
            var normalizedActual = PathRules.NormalizeRelative(actualItem.RelativePath);
            var normalizedExpected = PathRules.NormalizeRelative(remoteRel);
            if (!string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Name conflict: existing path '{normalizedActual}' does not match expected '{normalizedExpected}'");
            }
            
            // Bind existing directory
            var state = new RemoteItemState(actualItem.ItemId, rootId, actualItem.ParentItemId, actualItem.Name,
                actualItem.RelativePath, actualItem.ItemType, actualItem.SizeBytes, actualItem.MtimeUtc,
                actualItem.ContentHash, actualItem.Version, 0, RemotePlanningState.MetadataOnly);
            
            remote[remoteRel] = state;
            
            await RecordAsync(mapping, op.RelativePath, "CreateDirectory", "Satisfied", $"Bound to existing directory {actualItem.ItemId}", ct);
        }
    }

    /// <summary>
    /// Section B: CreateFile/UpdateFile with proper conflict resolution.
    /// Section 2 fix: Real server refresh on name conflict.
    /// </summary>
    private async Task ProcessCreateFileAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local,
        string remoteRel, RemoteItemState? existing, CancellationToken ct)
    {
        var path = PathRules.ToAbsolute(mapping.LocalPath, op.RelativePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Local file is unavailable.", path);
        
        var hash = await HashAsync(path, ct);
        
        // Case 1: Remote cache has matching file → skip upload
        if (existing is not null && existing.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase) 
            && string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            await mappings.UpsertItemAsync(new(mapping.MappingId, op.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
            await RecordAsync(mapping, op.RelativePath, "Synced", "Synced", "Hash match - no upload needed", ct);
            return;
        }
        
        // Case 2: Remote cache has file with DIFFERENT hash → Conflict
        if (existing is not null && existing.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase) 
            && existing.ContentHash is not null 
            && !string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                $"Name conflict: file {remoteRel} exists on server with different content");
        }
        
        // Case 3: Remote cache has directory where file expected → conflict
        if (existing is not null && existing.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                $"Cannot create file {remoteRel}: a directory with this name exists on server");
        }
        
        // Case 4: Upload — родитель резолвится/досоздаётся на сервере, в корень не уходим.
        var parentItemId = await EnsureParentAsync(rootId, remote, remoteRel, ct);
        try
        {
            // Section I fix: Acquire transport slot for concurrent transfer
            using (await _scheduler.AcquireAsync(ct))
            {
                var result = await _transfers.UploadAsync(path, 
                    new UploadTarget(rootId, parentItemId, Path.GetFileName(remoteRel), existing?.ItemId, existing?.Version, op.OperationId, mapping.MappingId), ct);
                
                if (result.ItemId is Guid newId)
                {
                    remote[remoteRel] = new RemoteItemState(newId, rootId, parentItemId, Path.GetFileName(remoteRel),
                        remoteRel, "file", info.Length, info.LastWriteTimeUtc, hash, result.Version ?? 0, 0, RemotePlanningState.MetadataOnly);
                }
            }
            
            await UpdateLastSuccessfulFileSync(mapping);
            
            await mappings.UpsertItemAsync(new(mapping.MappingId, op.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
            await RecordAsync(mapping, op.RelativePath, "Uploaded", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
        {
            // Section 2 fix: Real server refresh
            var actualItem = await FindActualServerItemAsync(rootId, remoteRel, remote, ct);
            
            if (actualItem is null)
            {
                throw;
            }
            
            if (actualItem.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Cannot create file {remoteRel}: a directory with this name exists on server");
            }
            
            if (actualItem.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var serverMeta = await GetServerContentMetadataAsync(actualItem.ItemId, actualItem.Version, ct);
                
                if (serverMeta.Hash is not null)
                {
                    if (string.Equals(serverMeta.Hash, hash, StringComparison.OrdinalIgnoreCase) && serverMeta.Size == info.Length)
                    {
                        var state = new RemoteItemState(actualItem.ItemId, rootId, actualItem.ParentItemId,
                            actualItem.Name, actualItem.RelativePath, actualItem.ItemType,
                            actualItem.SizeBytes, actualItem.MtimeUtc, actualItem.ContentHash,
                            actualItem.Version, 0, RemotePlanningState.MetadataOnly);
                        
                        remote[remoteRel] = state;
                        
                        await mappings.UpsertItemAsync(new(mapping.MappingId, op.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
                        await RecordAsync(mapping, op.RelativePath, "Synced", "Synced", $"Bound to existing file {actualItem.ItemId} (hash match)", ct);
                        return;
                    }
                    else
                    {
                        throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                            $"Name conflict: file {remoteRel} exists on server with different content");
                    }
                }
                else
                {
                    throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                        $"Name conflict: file {remoteRel} exists on server but server did not provide content hash for verification");
                }
            }
            
            throw;
        }
    }

    /// <summary>
    /// Section 7 fix: Move — remove old path, use authoritative server response.
    /// </summary>
    private async Task ProcessMoveAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var remoteRel = mapping.Scope(op.RelativePath);
        if (op.SecondaryPath is null) throw new IOException("Move destination is missing.");
        if (!remote.TryGetValue(remoteRel, out var moved)) throw new SyncApiException(System.Net.HttpStatusCode.NotFound, "SYNC_ITEM_NOT_FOUND", "The moved item no longer exists on Oryno NAS.");
        var destination = PathRules.NormalizeRelative(op.SecondaryPath);
        var destRemote = mapping.Scope(destination);
        
        // Use authoritative server response
        var moveResponse = await api.MoveItemAsync(moved.ItemId, await EnsureParentAsync(rootId, remote, destRemote, ct), Path.GetFileName(destRemote), moved.Version, op.OperationId, ct);
        
        // Section 7 fix: Remove old path, add new path with authoritative data
        remote.Remove(remoteRel);
        remote[destRemote] = new RemoteItemState(moveResponse.ItemId, rootId, moveResponse.ParentItemId,
            moveResponse.Name, moveResponse.RelativePath, moveResponse.ItemType,
            moveResponse.SizeBytes, moveResponse.MtimeUtc, moveResponse.ContentHash,
            moveResponse.Version, 0, moved.PlanningState);
        
        await mappings.RemoveItemAsync(mapping.MappingId, op.RelativePath, ct);
        if (local.TryGetValue(op.RelativePath, out var old)) await mappings.UpsertItemAsync(old with { RelativePath = destination }, ct);
        await RecordAsync(mapping, destination, "Moved", "Synced", null, ct);
    }

    private async Task ProcessDeleteAsync(SyncMapping mapping, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, string remoteRel, CancellationToken ct)
    {
        if (!remote.TryGetValue(remoteRel, out var existing))
        {
            await RecordAsync(mapping, op.RelativePath, "Deleted", "Synced", "Already absent from server", ct);
            return;
        }
        
        try
        {
            await api.DeleteItemAsync(existing.ItemId, op.OperationId, ct);
            remote.Remove(remoteRel);
            await RecordAsync(mapping, op.RelativePath, "Deleted", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_ITEM_NOT_FOUND")
        {
            remote.Remove(remoteRel);
            await RecordAsync(mapping, op.RelativePath, "Deleted", "Synced", "Item already absent from server", ct);
        }
    }

    private async Task ApplyRemoteOnlyAsync(SyncMapping mapping, Guid rootId, IReadOnlyDictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var pendingPaths = (await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue, ct)).Select(x => PathRules.NormalizeRelative(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote.Values.OrderBy(x => x.RelativePath.Count(c => c == '\\')).OrderBy(x => x.RelativePath.Count(c => c == '/')))
        {
            if (!mapping.InDestination(item.RelativePath)) continue;
            var localRel = mapping.ToLocalRel(item.RelativePath);
            if (localRel is null) continue;
            if (_ignores.IsIgnored(localRel)) continue;
            if (!PathRules.IsWindowsCompatible(localRel)) { await RecordAsync(mapping, item.RelativePath, "Download", "Error", "invalid filename: Windows cannot materialize this path.", ct); continue; }
            var full = PathRules.ToAbsolute(mapping.LocalPath, localRel);
            if (item.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase)) { Directory.CreateDirectory(full); continue; }
            if (item.ContentHash is null) continue;
            if (File.Exists(full))
            {
                var localHash = await HashAsync(full, ct);
                if (string.Equals(localHash, item.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (pendingPaths.Contains(localRel))
                {
                    var conflict = ConflictCopyPath(full);
                    try
                    {
                        // Section I fix: Acquire transport slot for concurrent download
                        using (await _scheduler.AcquireAsync(ct))
                        {
                            await _transfers.DownloadAsync(new DownloadTarget(item.ItemId, item.Version, localRel, item.SizeBytes ?? 0, item.ContentHash, item.MtimeUtc), conflict, ct);
                        }
                        await RecordAsync(mapping, localRel, "Conflict", "Error", "This file was changed both locally and on Oryno NAS.", ct);
                    }
                    catch (SyncApiException e) when (e.Code == "SYNC_CONTENT_MISSING")
                    {
                        await RecordAsync(mapping, localRel, "Download", "Error", e.Message, ct);
                    }
                    continue;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            RemoteItemDto current;
            try { current = await api.GetItemAsync(item.ItemId, ct); }
            catch (SyncApiException e) when (e.Code == "SYNC_ITEM_NOT_FOUND") { await remoteState.MarkItemMissingAsync(rootId, item.ItemId.ToString(), ct); await RecordAsync(mapping, localRel, "Download", "Error", e.Message, ct); continue; }
            try
            {
                // Section I fix: Acquire transport slot for concurrent download
                using (await _scheduler.AcquireAsync(ct))
                {
                    await _transfers.DownloadAsync(new DownloadTarget(current.ItemId, current.Version, localRel, current.SizeBytes ?? 0, current.ContentHash ?? item.ContentHash!, current.MtimeUtc), full, ct);
                }
                var info = new FileInfo(full); await mappings.UpsertItemAsync(new(mapping.MappingId, localRel, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
                await RecordAsync(mapping, localRel, "Downloaded", "Synced", null, ct);
            }
            catch (SyncApiException e) when (e.Code == "SYNC_CONTENT_MISSING")
            {
                await remoteState.MarkItemMissingAsync(rootId, item.ItemId.ToString(), ct);
                await RecordAsync(mapping, localRel, "Download", "Error", e.Message, ct);
            }
        }
    }

    /// <summary>
    /// Section 2 fix: Find actual server item by path with REAL server refresh.
    /// First tries cache, then falls back to actual server API call.
    /// Uses ISyncMetadataApi.GetInventoryPageAsync to fetch authoritative data,
    /// persists to RemoteStateStore, then re-looks up.
    /// </summary>
    private async Task<RemoteItemState?> FindActualServerItemAsync(Guid rootId, string remoteRel, Dictionary<string, RemoteItemState> currentRemote, CancellationToken ct)
    {
        // First check current remote dictionary (may have been updated by previous operations)
        foreach (var item in currentRemote.Values)
        {
            if (item.IsDeleted) continue;
            if (string.Equals(PathRules.NormalizeRelative(item.RelativePath), remoteRel, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        
        // Section 2 fix: Real server refresh — fetch from server API (NOT SQLite cache)
        if (api is ISyncMetadataApi metadataApi)
        {
            try
            {
                // Fetch authoritative inventory from server.
                // §4: сервер разрешает limit ≤ 2000 (FastAPI Query(ge=1, le=MAX_PAGE)); раньше
                // здесь стоял 5000 → HTTP 422, поэтому разрешение конфликта имени (409) всегда
                // падало и операция уходила в FailedPermanent. Качаем страницами ≤ 500.
                var all = new List<RemoteItemDto>();
                string? cursor = null;
                for (var pageIndex = 0; pageIndex < 40; pageIndex++)
                {
                    var page = await metadataApi.GetInventoryPageAsync(rootId, cursor, 500, ct);
                    all.AddRange(page.Items);
                    cursor = page.NextCursor;
                    if (string.IsNullOrEmpty(cursor) || page.Items.Count == 0) break;
                }
                
                // Persist fresh data into RemoteStateStore (real server → SQLite)
                await remoteState.RefreshFromServerAsync(rootId, (r, c) =>
                {
                    // Return the fetched items as the "fetcher" result
                    return Task.FromResult<IReadOnlyList<RemoteItemDto>>(all);
                }, ct);
                
                // Now look up in the freshly updated remote dictionary
                foreach (var item in all)
                {
                    if (string.Equals(PathRules.NormalizeRelative(item.RelativePath), remoteRel, StringComparison.OrdinalIgnoreCase))
                    {
                        var state = new RemoteItemState(item.ItemId, rootId, item.ParentItemId, item.Name,
                            item.RelativePath, item.ItemType, item.SizeBytes, item.MtimeUtc, item.ContentHash,
                            item.Version, 0, item.ItemType == "directory" ? RemotePlanningState.MetadataOnly : RemotePlanningState.NeedsDownload);
                        
                        // Update the current remote dictionary with the fresh item
                        currentRemote[remoteRel] = state;
                        return state;
                    }
                }
            }
            catch
            {
                // Fall through to return null
            }
        }
        
        return null;
    }

    private async Task<(long Size, string? Hash)> GetServerContentMetadataAsync(Guid itemId, long version, CancellationToken ct)
    {
        try
        {
            var meta = await api.GetContentMetadataAsync(itemId, version, ct);
            return (meta.Size, meta.Hash);
        }
        catch
        {
            return (0, null);
        }
    }

    /// <summary>
    /// Section 9 fix: Update last successful file sync timestamp (per-mapping).
    /// </summary>
    private async Task UpdateLastSuccessfulFileSync(SyncMapping mapping, CancellationToken ct = default)
    {
        await remoteState.RecordSuccessfulFileSyncForMappingAsync(mapping.MappingId, ct);
        if (mapping.ServerRootId is Guid rootId)
        {
            await remoteState.RecordSuccessfulFileSyncAsync(rootId, ct);
        }
    }

    private static string? ResolveServerItemIdForOperation(MappingPendingOperation op, Dictionary<string, RemoteItemState> remote)
    {
        // Try to find the item in remote dictionary by path
        var rel = PathRules.NormalizeRelative(op.RelativePath);
        if (remote.TryGetValue(rel, out var existing) && existing.ItemId != Guid.Empty)
        {
            return existing.ItemId.ToString();
        }
        return null;
    }

    private static void MarkRemoteItemDeleted(Dictionary<string, RemoteItemState> remote, string relativePath)
    {
        var rel = PathRules.NormalizeRelative(relativePath);
        if (remote.TryGetValue(rel, out var existing))
        {
            remote[rel] = existing with { IsDeleted = true };
        }
    }

    private static string ConflictCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); var candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}){ext}"); var n = 2; while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}-{n++}){ext}"); return candidate;
    }

    private async Task<string> HashAsync(string path, CancellationToken ct) { await _hashGate.WaitAsync(ct); try { return await _hasher.ComputeAsync(path, ct); } finally { _hashGate.Release(); } }
    
    /// <summary>
    /// Резолвит родителя для server-relative пути, ДОСОЗДАВАЯ отсутствующую цепочку папок
    /// на сервере. Раньше при отсутствии родителя в локальном кэше FindParent возвращал null,
    /// и upload уходил в КОРЕНЬ root'а — так файлы попадали вне выбранной папки-приёмника
    /// (root-level pollution). POST /roots/{id}/folders идемпотентен на сервере
    /// (существующая папка возвращается тем же item_id), поэтому проход по цепочке безопасен.
    /// </summary>
    private async Task<Guid?> EnsureParentAsync(Guid rootId, Dictionary<string, RemoteItemState> remote, string remoteRel, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(remoteRel)?.Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(parent)) return null;               // сам корень назначения
        if (remote.TryGetValue(parent, out var cached) && cached.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            return cached.ItemId;
        Guid? currentParent = null;
        var currentRel = string.Empty;
        foreach (var part in parent.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            currentRel = currentRel.Length == 0 ? part : $"{currentRel}\\{part}";
            if (remote.TryGetValue(currentRel, out var known) && known.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                currentParent = known.ItemId;
                continue;
            }
            RemoteItemDto folder;
            try
            {
                folder = await api.CreateFolderAsync(rootId, currentParent, part, Guid.NewGuid(), ct);
            }
            catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
            {
                // Папка уже существует: не валим операцию, а привязываемся к существующей
                // (серверная сторона отдаёт имя-конфликт, когда запись есть в БД).
                var actual = await FindActualServerItemAsync(rootId, currentRel, remote, ct);
                if (actual is null) throw;
                if (!actual.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
                    throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                        $"a file already exists where the folder is required: {currentRel}");
                remote[currentRel] = actual;
                currentParent = actual.ItemId;
                continue;
            }
            remote[currentRel] = new RemoteItemState(folder.ItemId, rootId, folder.ParentItemId, folder.Name,
                folder.RelativePath, folder.ItemType, folder.SizeBytes, folder.MtimeUtc, folder.ContentHash,
                folder.Version, 0, RemotePlanningState.MetadataOnly);
            currentParent = folder.ItemId;
            SyncDiagnostics.Report("REMOTE_FOLDER_ENSURE", $"root={rootId} path={currentRel} item={folder.ItemId}");
        }
        return currentParent;
    }

    private static Guid? FindParent(IReadOnlyDictionary<string, RemoteItemState> remote, string path) 
    { 
        var parent = Path.GetDirectoryName(path)?.Replace('/', '\\'); 
        return parent is not null && remote.TryGetValue(parent, out var item) ? item.ItemId : null; 
    }
    
    private async Task RecordAsync(SyncMapping mapping, string path, string action, string status, string? error, CancellationToken ct) 
    { 
        var row = new SyncActivityEvent(Guid.NewGuid(), mapping.MappingId, path, action, status, DateTimeOffset.UtcNow, error is null ? null : "SYNC_ERROR", error); 
        await mappings.RecordActivityAsync(row, ct); 
        Activity?.Invoke(row); 
    }
    
    /// <summary>
    /// FIX: Determines if an operation type is a server mutation (vs read-only/download).
    /// </summary>
    private static bool IsMutationOperation(OperationType type) => type switch
    {
        OperationType.CreateFile or OperationType.UpdateFile or OperationType.CreateDirectory
            or OperationType.Move or OperationType.Delete => true,
        _ => false
    };
    
    /// <summary>
    /// FIX: Block existing pending operations on conflict paths by marking them FailedPermanent.
    /// This prevents old CreateFile/CreateDirectory/etc. operations from executing after duplicate detection.
    /// </summary>
    private async Task BlockPendingOperationsOnConflictPathsAsync(SyncMapping mapping, CancellationToken ct)
    {
        if (_blockedCanonicalPaths.Count == 0) return;
        
        var pending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.UtcNow, ct);
        foreach (var op in pending)
        {
            if (op.State != OperationState.Pending) continue;
            if (!IsMutationOperation(op.Type)) continue;
            
            var rel = PathRules.NormalizeRelative(op.RelativePath);
            if (_blockedCanonicalPaths.Contains(rel))
            {
                await mappings.UpdateOperationAsync(op with 
                { 
                    State = OperationState.FailedPermanent, 
                    AttemptCount = op.AttemptCount + 1,
                    LastError = $"Duplicate server path requires reconciliation: {op.RelativePath}",
                    NextAttemptAt = DateTimeOffset.MaxValue 
                }, ct);
                await RecordAsync(mapping, op.RelativePath, op.Type.ToString(), "Conflict", 
                    $"Blocked by duplicate path: {op.RelativePath}", ct);
            }
        }
    }
    
    /// <summary>
    /// FIX: Deduplicated diagnostic logging — only logs when duplicate set changes.
    /// </summary>
    private void LogDiagnosticDeduped(string category, Guid mappingId, string path, string ids)
    {
        if (_diagnosticDedup.ShouldLog(mappingId, path, ids))
        {
            var message = $"path={path} ids={ids}";
            var evt = new SyncActivityEvent(Guid.NewGuid(), mappingId, "", category, "Diagnostic", DateTimeOffset.UtcNow, null, message);
            DiagnosticLog?.Invoke(evt);
        }
    }
    
    /// <summary>
    /// Section 8 fix: LogDiagnostic now actually persists diagnostic events.
    /// Writes to a dedicated diagnostic log that doesn't pollute user activity.
    /// </summary>
    private Func<string, SyncActivityEvent> LogDiagnostic(string category, Guid mappingId)
    {
        return message => 
        {
            var evt = new SyncActivityEvent(Guid.NewGuid(), mappingId, "", category, "Diagnostic", DateTimeOffset.UtcNow, null, message);
            // Fire and forget — diagnostic events go to DiagnosticLog (not Activity)
            DiagnosticLog?.Invoke(evt);
            return evt;
        };
    }
}

/// <summary>
/// FIX: Deduplicates duplicate-detection diagnostics.
/// For each (mappingId, canonical_path) pair, only logs when the set of duplicate item IDs changes.
/// Prevents spamming PROCESS_DUPLICATE every 2-4 seconds for the same duplicates.
/// </summary>
internal sealed class DuplicateDiagnosticDedup
{
    private readonly Dictionary<(Guid, string), string> _lastSeen = new();

    public bool ShouldLog(Guid mappingId, string path, string ids)
    {
        var key = (mappingId, path);
        if (_lastSeen.TryGetValue(key, out var lastIds) && lastIds == ids)
        {
            return false; // Same duplicate set — don't spam
        }
        _lastSeen[key] = ids;
        return true;
    }
}
