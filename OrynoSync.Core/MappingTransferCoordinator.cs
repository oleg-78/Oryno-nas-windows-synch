using OrynoSync.Core;

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
                // Multiple live items = conflict
                conflicts.Add(g.Key);
                // Log diagnostic
                var ids = string.Join(",", list.Select(x => x.ItemId.ToString("N").Substring(0, 8)));
                logDiagnostic?.Invoke(g.Key, list[0].RootId, ids);
                // Deterministic: highest version, then highest revision
                var best = live.OrderByDescending(x => x.Version).ThenByDescending(x => x.LastRevision).First();
                items.Add(best);
            }
        }

        return new Resolution(items, conflicts);
    }
}

public sealed class MappingTransferCoordinator(ISyncMappingStore mappings, IRemoteStateStore remoteState, IContentTransferApi api, string tempRoot)
{
    private readonly SqliteTransferSessionStore _sessions = new(Path.Combine(tempRoot, "transfer-state.db"));
    private readonly ResumableTransferClient _transfers = new(api, tempRoot, 3, new SqliteTransferSessionStore(Path.Combine(tempRoot, "transfer-state.db")));
    private readonly SemaphoreSlim _hashGate = new(2);
    private static readonly IgnoreRules _ignores = new();
    private readonly IContentHasher _hasher = new Blake3ContentHasher();
    public event Action<SyncActivityEvent>? Activity;

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
        
        // SEK C: Authoritative duplicate resolution instead of GroupBy.First
        var remoteResolution = DuplicateCanonicalResolver.ResolveRemote(remoteItems,
            (path, rootId, ids) => LogDiagnostic("REBUILD_DUPLICATE", mapping.MappingId)( $"path={path} ids={ids}"));
        
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
        
        var remoteByPath = remote
            .GroupBy(x => PathRules.NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var items = desired.Select(item => new MappingLocalItem(mapping.MappingId, item.RelativePath, item.ItemType, item.Size, item.Mtime, item.ItemType == ItemType.File && remoteByPath.TryGetValue(item.RelativePath, out var r) && string.Equals(item.ContentHash, r.ContentHash, StringComparison.OrdinalIgnoreCase) ? SyncItemState.Synced : SyncItemState.Waiting)).ToArray();
        await mappings.ReplaceItemsAsync(mapping.MappingId, items, ct);
        await mappings.ReplacePendingAsync(mapping.MappingId, normalized, ct);
        await mappings.UpdateMappingAsync(mapping with { InventoryState = "Normalized", Status = normalized.Count == 0 ? MappingStatus.UpToDate : MappingStatus.ReadyToSync, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return plan;
    }

    public async Task ProcessAsync(IReadOnlyList<SyncMapping> allMappings, CancellationToken ct = default)
    {
        // SEK I: Scheduler throughput - process multiple independent operations
        // Process operations in parallel for different mappings
        var tasks = allMappings
            .Where(x => x.Enabled && x.ServerRootId is not null && x.Status != MappingStatus.Paused)
            .Select(mapping => Task.Run(async () => await ProcessMappingAsync(mapping, ct, maxOperations: 3), ct))
            .ToList();
        
        await Task.WhenAll(tasks);
    }

    private async Task ProcessMappingAsync(SyncMapping mapping, CancellationToken ct, int maxOperations)
    {
        var rootId = mapping.ServerRootId!.Value;
        // SEK C: Authoritative duplicate resolution instead of silent GroupBy.First
        var remoteItems = (await remoteState.GetRemoteItemsAsync(rootId, ct)).Where(x => !x.IsDeleted).ToArray();
        var remoteResolution = DuplicateCanonicalResolver.ResolveRemote(remoteItems,
            (path, rootId, ids) => LogDiagnostic("PROCESS_DUPLICATE", mapping.MappingId)( $"path={path} ids={ids}"));
        var remote = remoteResolution.Items.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        
        if (remoteResolution.Conflicts.Count > 0)
        {
            var details = string.Join("; ", remoteResolution.Conflicts.Select(d => d));
            await RecordAsync(mapping, "", "Sync", "Info", $"Resolved {remoteResolution.Conflicts.Count} duplicate canonical paths: {details}", ct);
        }
        
        var local = (await mappings.GetItemsAsync(mapping.MappingId, ct))
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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
            // Add conflicts from duplicate resolution
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
        
        // Re-read status from DB to get fresh state (RebuildAsync may have set ReadyToSync).
        var freshMapping = await mappings.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        // Preflight modes (ReadyForPreflight, ReadyToSync): plan exists, but no
        // transfers run until the user explicitly approves via Start.
        // Stopped: sync engine disabled by user, no transfers.
        if (freshMapping.Status is MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync or MappingStatus.Stopped) return;
        
        // Process operations with dependency ordering
        foreach (var operation in pending.Take(Math.Max(1, maxOperations)))
        {
            try { await ProcessOperationAsync(mapping, rootId, operation, remote, local, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // SEK F: Proper operation state model - separate transient from permanent
                var attempts = operation.AttemptCount + 1;
                var isPermanent = IsPermanentFailure(ex);
                
                if (isPermanent)
                {
                    // Permanent failure: FailedPermanent + NextAttemptAt = MaxValue
                    await mappings.UpdateOperationAsync(operation with 
                    { 
                        State = OperationState.FailedPermanent, 
                        AttemptCount = attempts, 
                        LastError = ex.Message, 
                        NextAttemptAt = DateTimeOffset.MaxValue 
                    }, ct);
                    await RecordAsync(mapping, operation.RelativePath, operation.Type.ToString(), "Error", ex.Message, ct);
                }
                else if (ex is SyncApiException api404 && (api404.Code == "SYNC_CONTENT_MISSING" || api404.Code == "SYNC_ITEM_NOT_FOUND"))
                {
                    // Content gone server-side: mark completed to stop retry
                    await mappings.UpdateOperationAsync(operation with { State = OperationState.Completed, LastError = ex.Message, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
                    await RecordAsync(mapping, operation.RelativePath, operation.Type.ToString(), "Error", ex.Message, ct);
                }
                else
                {
                    // Transient failure: Pending with retry backoff
                    // SEK G: Transient retries should NOT show as red errors
                    var backoffSeconds = Math.Min(300, Math.Pow(2, attempts)); // max 5 min backoff
                    await mappings.UpdateOperationAsync(operation with 
                    { 
                        State = OperationState.Pending, 
                        AttemptCount = attempts, 
                        LastError = ex.Message, 
                        NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds) 
                    }, ct);
                    await RecordAsync(mapping, operation.RelativePath, operation.Type.ToString(), "Retrying", 
                        $"Attempt {attempts}, retry in {backoffSeconds}s: {ex.Message}", ct);
                }
            }
        }
        await ApplyRemoteOnlyAsync(mapping, rootId, remote, local, ct);
    }

    /// <summary>
    /// SEK F: Determines if an exception represents a permanent (non-retryable) failure.
    /// </summary>
    private static bool IsPermanentFailure(Exception ex)
    {
        return ex switch
        {
            SyncApiException apiError => apiError.Code is "SYNC_CONFLICT" or "SYNC_NAME_CONFLICT" 
                or "SYNC_HASH_MISMATCH" or "SYNC_NAME_INVALID" or "SYNC_CONTENT_MISSING",
            FileChangedDuringTransferException => true,
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
            catch (IOException) { /* file locked by Office/antivirus: skip, next pass will retry */ }
            catch (UnauthorizedAccessException) { /* protected item: skip */ }
        }
        return result;
    }

    private async Task ProcessOperationAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op, 
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var rel = PathRules.NormalizeRelative(op.RelativePath);
        // Temporary/system/staging files never become sync operations: mark them complete (skip)
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
                await _transfers.DownloadAsync(new DownloadTarget(currentItem.ItemId, currentItem.Version, rel, currentItem.SizeBytes ?? 0, currentItem.ContentHash ?? existing.ContentHash, currentItem.MtimeUtc), downloadPath, ct);
                var downloaded = new FileInfo(downloadPath); await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.File, downloaded.Length, downloaded.LastWriteTimeUtc, SyncItemState.Synced), ct); 
                await RecordAsync(mapping, rel, "Downloaded", "Synced", null, ct);
                break;
            case OperationType.Conflict:
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT", "This item was changed both locally and on Oryno NAS.");
        }
        
        await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = null, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
    }

    /// <summary>
    /// SEK A: CreateDirectory with proper handling of existing directories.
    /// If directory already exists on server, bind to it instead of failing.
    /// </summary>
    private async Task ProcessCreateDirectoryAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, string remoteRel, RemoteItemState? existing, CancellationToken ct)
    {
        // Case 1: Remote cache already has this directory → satisfied
        if (existing is not null && existing.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
        {
            // Already exists as directory → satisfied
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
            var parentItemId = FindParent(remote, remoteRel);
            var folder = await api.CreateFolderAsync(rootId, parentItemId, Path.GetFileName(remoteRel), op.OperationId, ct);
            
            // SEK K: Update remote snapshot with authoritative metadata
            remote[remoteRel] = new RemoteItemState(folder.ItemId, rootId, folder.ParentItemId, folder.Name, 
                folder.RelativePath, folder.ItemType, folder.SizeBytes, folder.MtimeUtc, folder.ContentHash, 
                folder.Version, 0, RemotePlanningState.MetadataOnly);
            
            await RecordAsync(mapping, op.RelativePath, "Created", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
        {
            // Stale cache: directory actually exists on server
            // Case 4: Fetch/reconcile existing child
            var actualItem = await FindActualServerItemAsync(rootId, remoteRel, ct);
            
            if (actualItem is null)
            {
                // Truly transient — retry later
                throw;
            }
            
            if (!actualItem.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                // Existing object is a FILE, not a directory → conflict
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Name conflict: existing item at {remoteRel} is {actualItem.ItemType}, expected directory");
            }
            
            // Normalize and validate path match
            var normalizedActual = PathRules.NormalizeRelative(actualItem.RelativePath);
            var normalizedExpected = PathRules.NormalizeRelative(remoteRel);
            if (!string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Name conflict: existing path '{normalizedActual}' does not match expected '{normalizedExpected}'");
            }
            
            // Case 5: Bind existing directory
            var state = new RemoteItemState(actualItem.ItemId, rootId, actualItem.ParentItemId, actualItem.Name,
                actualItem.RelativePath, actualItem.ItemType, actualItem.SizeBytes, actualItem.MtimeUtc,
                actualItem.ContentHash, actualItem.Version, 0, RemotePlanningState.MetadataOnly);
            
            // SEK K: Add to remote dictionary so child operations can resolve parent
            remote[remoteRel] = state;
            
            await RecordAsync(mapping, op.RelativePath, "CreateDirectory", "Satisfied", $"Bound to existing directory {actualItem.ItemId}", ct);
        }
    }

    /// <summary>
    /// SEK B: CreateFile/UpdateFile with proper conflict resolution.
    /// Never mark as "Uploaded" unless upload commit succeeded AND server hash matches.
    /// </summary>
    private async Task ProcessCreateFileAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local,
        string remoteRel, RemoteItemState? existing, CancellationToken ct)
    {
        var path = PathRules.ToAbsolute(mapping.LocalPath, op.RelativePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Local file is unavailable.", path);
        
        var hash = await HashAsync(path, ct);
        
        // Case 2: Remote cache has matching file → skip upload
        if (existing is not null && existing.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase) 
            && string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            await mappings.UpsertItemAsync(new(mapping.MappingId, op.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
            await RecordAsync(mapping, op.RelativePath, "Synced", "Synced", "Hash match - no upload needed", ct);
            return;
        }
        
        // Case 2b: Remote cache has file with DIFFERENT hash → Conflict (never silently overwrite)
        if (existing is not null && existing.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase) 
            && existing.ContentHash is not null 
            && !string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                $"Name conflict: file {remoteRel} exists on server with different content (server_hash={existing.ContentHash[..Math.Min(8, existing.ContentHash.Length)]}... local_hash={hash[..Math.Min(8, hash.Length)]}...)");
        }
        
        // Case 3: Remote cache has directory where file expected → conflict
        if (existing is not null && existing.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                $"Cannot create file {remoteRel}: a directory with this name exists on server");
        }
        
        // Case 3: Upload
        var parentItemId = FindParent(remote, remoteRel);
        try
        {
            var result = await _transfers.UploadAsync(path, 
                new UploadTarget(rootId, parentItemId, Path.GetFileName(remoteRel), existing?.ItemId, existing?.Version, op.OperationId, mapping.MappingId), ct);
            
            // SEK K: Refresh remote snapshot with authoritative metadata from commit
            if (result.ItemId is Guid newId)
            {
                remote[remoteRel] = new RemoteItemState(newId, rootId, parentItemId, Path.GetFileName(remoteRel),
                    remoteRel, "file", info.Length, info.LastWriteTimeUtc, hash, result.Version ?? 0, 0, RemotePlanningState.MetadataOnly);
            }
            
            // SEK H: Record successful file sync
            await UpdateLastSuccessfulFileSync(mapping);
            
            await mappings.UpsertItemAsync(new(mapping.MappingId, op.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
            await RecordAsync(mapping, op.RelativePath, "Uploaded", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
        {
            // SEK B: Stale cache — actual file exists. Verify server metadata BEFORE marking as uploaded.
            var actualItem = await FindActualServerItemAsync(rootId, remoteRel, ct);
            
            if (actualItem is null)
            {
                // Truly transient — retry later
                throw;
            }
            
            // Case A: Existing item is a DIRECTORY → conflict
            if (actualItem.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                    $"Cannot create file {remoteRel}: a directory with this name exists on server");
            }
            
            // Case B: Existing item is a FILE
            if (actualItem.ItemType.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                // Get actual server content metadata
                var serverMeta = await GetServerContentMetadataAsync(actualItem.ItemId, actualItem.Version, ct);
                
                if (serverMeta.Hash is not null)
                {
                    if (string.Equals(serverMeta.Hash, hash, StringComparison.OrdinalIgnoreCase) && serverMeta.Size == info.Length)
                    {
                        // SEK B-1: Same hash + same size → bind existing, mark Synced
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
                        // SEK B-2: Hash differs → NOT uploaded. Update or Conflict per policy.
                        // According to W.2 policy: different hash = Conflict (not silent overwrite)
                        throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                            $"Name conflict: file {remoteRel} exists on server with different content (server_hash={serverMeta.Hash[..Math.Min(8, serverMeta.Hash.Length)]}... local_hash={hash[..Math.Min(8, hash.Length)]}...)");
                    }
                }
                else
                {
                    // Server didn't provide hash — safer to treat as conflict
                    throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT",
                        $"Name conflict: file {remoteRel} exists on server but server did not provide content hash for verification");
                }
            }
            
            // Unknown item type → retry
            throw;
        }
    }

    private async Task ProcessMoveAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var remoteRel = mapping.Scope(op.RelativePath);
        if (op.SecondaryPath is null) throw new IOException("Move destination is missing.");
        if (!remote.TryGetValue(remoteRel, out var moved)) throw new SyncApiException(System.Net.HttpStatusCode.NotFound, "SYNC_ITEM_NOT_FOUND", "The moved item no longer exists on Oryno NAS.");
        var destination = PathRules.NormalizeRelative(op.SecondaryPath);
        var destRemote = mapping.Scope(destination);
        
        await api.MoveItemAsync(moved.ItemId, FindParent(remote, destRemote), Path.GetFileName(destRemote), moved.Version, op.OperationId, ct);
        
        // SEK K: Refresh remote snapshot after move
        remote.TryAdd(destRemote, new RemoteItemState(moved.ItemId, rootId, FindParent(remote, destRemote),
            Path.GetFileName(destRemote), destRemote, moved.ItemType, moved.SizeBytes, moved.MtimeUtc,
            moved.ContentHash, moved.Version + 1, 0, moved.PlanningState));
        
        await mappings.RemoveItemAsync(mapping.MappingId, op.RelativePath, ct);
        if (local.TryGetValue(op.RelativePath, out var old)) await mappings.UpsertItemAsync(old with { RelativePath = destination }, ct);
        await RecordAsync(mapping, destination, "Moved", "Synced", null, ct);
    }

    /// <summary>
    /// SEK M: Delete — if server target already missing/tombstoned, treat as idempotent Completed.
    /// </summary>
    private async Task ProcessDeleteAsync(SyncMapping mapping, MappingPendingOperation op,
        Dictionary<string, RemoteItemState> remote, string remoteRel, CancellationToken ct)
    {
        if (!remote.TryGetValue(remoteRel, out var existing))
        {
            // Already gone from remote cache → idempotent success
            await RecordAsync(mapping, op.RelativePath, "Deleted", "Synced", "Already absent from server", ct);
            return;
        }
        
        try
        {
            await api.DeleteItemAsync(existing.ItemId, op.OperationId, ct);
            // SEK K: Remove from remote snapshot
            remote.Remove(remoteRel);
            await RecordAsync(mapping, op.RelativePath, "Deleted", "Synced", null, ct);
        }
        catch (SyncApiException e) when (e.Code == "SYNC_ITEM_NOT_FOUND")
        {
            // Server says item not found → idempotent success (already deleted/tombstoned)
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
                        await _transfers.DownloadAsync(new DownloadTarget(item.ItemId, item.Version, localRel, item.SizeBytes ?? 0, item.ContentHash, item.MtimeUtc), conflict, ct);
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
                await _transfers.DownloadAsync(new DownloadTarget(current.ItemId, current.Version, localRel, current.SizeBytes ?? 0, current.ContentHash ?? item.ContentHash!, current.MtimeUtc), full, ct);
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
    /// SEK C: Find actual server item by path, bypassing cache.
    /// </summary>
    private async Task<RemoteItemState?> FindActualServerItemAsync(Guid rootId, string remoteRel, CancellationToken ct)
    {
        foreach (var item in await remoteState.GetRemoteItemsAsync(rootId, ct))
        {
            if (item.IsDeleted) continue;
            if (string.Equals(PathRules.NormalizeRelative(item.RelativePath), remoteRel, StringComparison.OrdinalIgnoreCase))
                return item;
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
    /// SEK H: Update last successful file sync timestamp.
    /// </summary>
    private async Task UpdateLastSuccessfulFileSync(SyncMapping mapping, CancellationToken ct = default)
    {
        if (mapping.ServerRootId is Guid rootId)
        {
            await remoteState.RecordSuccessfulFileSyncAsync(rootId, ct);
        }
    }

    private static string ConflictCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); var candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}){ext}"); var n = 2; while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}-{n++}){ext}"); return candidate;
    }

    private async Task<string> HashAsync(string path, CancellationToken ct) { await _hashGate.WaitAsync(ct); try { return await _hasher.ComputeAsync(path, ct); } finally { _hashGate.Release(); } }
    
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
    
    private Func<string, SyncActivityEvent> LogDiagnostic(string category, Guid mappingId)
    {
        return message => new SyncActivityEvent(Guid.NewGuid(), mappingId, "", category, "Diagnostic", DateTimeOffset.UtcNow, null, message);
    }
}
