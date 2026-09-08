namespace OrynoSync.Core;

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
        var remote = (await remoteState.GetRemoteItemsAsync(rootId, ct))
            .Where(x => !x.IsDeleted && mapping.InDestination(x.RelativePath))
            .Select(x => { var l = mapping.ToLocalRel(x.RelativePath); return l is null ? null : x with { RelativePath = l }; })
            .Where(x => x is not null).Select(x => x!).ToArray();
        var oldPending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue, ct);
        var desired = await SnapshotAsync(mapping.LocalPath, ct);
        var plan = QueueNormalizer.Plan(desired, remote, oldPending);
        var normalized = plan.Conflicts.Select(path => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.Failed, "SYNC_CONFLICT: local and NAS content differ.")).ToList();
        normalized.AddRange(plan.Operations.Select(operation => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, operation.Type, operation.RelativePath, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null)));
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
        // One transport operation per mapping per scheduler pass keeps a large
        // queue from starving smaller mappings. The application calls this pass
        // periodically, so the next pass naturally continues round-robin.
        foreach (var mapping in allMappings.Where(x => x.Enabled && x.ServerRootId is not null && x.Status != MappingStatus.Paused))
            await ProcessMappingAsync(mapping, ct, 1);
    }

    private async Task ProcessMappingAsync(SyncMapping mapping, CancellationToken ct, int maxOperations)
    {
        var rootId = mapping.ServerRootId!.Value;
        // Key remote lookup by CANONICAL path form so `a/b` (server forward slash)
        // and `a\b` (local backslash) always match. Without this, ProcessOperation
        // saw `existing == null` for an already-present NAS file and kept issuing
        // CreateFile → server 409 SYNC_NAME_CONFLICT → the infinite .txt loop.
        var remote = (await remoteState.GetRemoteItemsAsync(rootId, ct))
            .Where(x => !x.IsDeleted)
            .GroupBy(x => PathRules.NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var local = (await mappings.GetItemsAsync(mapping.MappingId, ct)).GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.UtcNow, ct);
        if (!string.Equals(mapping.InventoryState, "Normalized", StringComparison.OrdinalIgnoreCase))
        {
            var desired = await SnapshotAsync(mapping.LocalPath, ct);
            // Same destination-scoped remote view as RebuildAsync: only items inside the
            // selected destination subtree, expressed as local-relative paths, so the plan
            // never mixes root-level NAS content with the mapping's destination (Работа/…).
            var remoteInDestination = (await remoteState.GetRemoteItemsAsync(rootId, ct))
                .Where(x => !x.IsDeleted && mapping.InDestination(x.RelativePath))
                .Select(x => { var rel = mapping.ToLocalRel(x.RelativePath); return rel is null ? null : x with { RelativePath = rel }; })
                .Where(x => x is not null).Select(x => x!).ToArray();
            var plan = QueueNormalizer.Plan(desired, remoteInDestination, pending);
            var normalized = new List<MappingPendingOperation>();
            normalized.AddRange(plan.Conflicts.Select(path => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.Failed, "SYNC_CONFLICT: local and NAS content differ.")));
            normalized.AddRange(plan.Operations.Select(operation => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, operation.Type, operation.RelativePath, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null)));
            await mappings.ReplacePendingAsync(mapping.MappingId, normalized, ct);
            await mappings.UpdateMappingAsync(mapping with { InventoryState = "Normalized", UpdatedAt = DateTimeOffset.UtcNow }, ct);
            pending = normalized.Where(x => x.State is OperationState.Pending or OperationState.Failed).ToArray();
        }
        // Re-read status from DB to get fresh state (RebuildAsync may have set ReadyToSync).
        var freshMapping = await mappings.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        // Preflight modes (ReadyForPreflight, ReadyToSync): plan exists, but no
        // transfers run until the user explicitly approves via Start.
        // Stopped: sync engine disabled by user, no transfers.
        if (freshMapping.Status is MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync or MappingStatus.Stopped) return;
        foreach (var operation in pending.Take(Math.Max(1, maxOperations)))
        {
            try { await ProcessOperationAsync(mapping, rootId, operation, remote, local, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A download whose NAS content no longer exists is reconciled by dropping the
                // remote record (handled by ApplyRemoteOnly) instead of retrying forever.
                if (ex is SyncApiException api404 && (api404.Code == "SYNC_CONTENT_MISSING" || api404.Code == "SYNC_ITEM_NOT_FOUND"))
                {
                    await mappings.UpdateOperationAsync(operation with { State = OperationState.Completed, LastError = ex.Message, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
                    await RecordAsync(mapping, operation.RelativePath, operation.Type.ToString(), "Error", ex.Message, ct);
                    continue;
                }
                var attempts = operation.AttemptCount + 1;
                var permanent = ex is SyncApiException apiError && (apiError.Code is "SYNC_CONFLICT" or "SYNC_NAME_CONFLICT" or "SYNC_HASH_MISMATCH" or "SYNC_NAME_INVALID") || ex is FileChangedDuringTransferException;
                await mappings.UpdateOperationAsync(operation with { State = permanent ? OperationState.Failed : OperationState.Failed, AttemptCount = attempts, LastError = ex.Message, NextAttemptAt = permanent ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, attempts))) }, ct);
                await RecordAsync(mapping, operation.RelativePath, operation.Type.ToString(), "Error", ex.Message, ct);
            }
        }
        await ApplyRemoteOnlyAsync(mapping, rootId, remote, local, ct);
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

    private async Task ProcessOperationAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op, Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var rel = PathRules.NormalizeRelative(op.RelativePath);
        // Temporary/system/staging files never become sync operations: mark them complete (skip)
        // so they are not uploaded, downloaded, moved, or deleted remotely.
        if (_ignores.IsIgnored(rel) || (op.Type == OperationType.Move && op.SecondaryPath is not null && _ignores.IsIgnored(op.SecondaryPath)))
        {
            await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = null, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
            return;
        }
        if (!PathRules.IsWindowsCompatible(rel)) throw new IOException("invalid filename: Windows cannot materialize this path.");
        var remoteRel = mapping.Scope(rel); // root-relative server path (destination-prefixed)
        remote.TryGetValue(remoteRel, out var existing);
        switch (op.Type)
        {
            case OperationType.CreateDirectory:
                if (existing is null)
                {
                    var folder = await api.CreateFolderAsync(rootId, FindParent(remote, remoteRel), Path.GetFileName(remoteRel), op.OperationId, ct);
                    remote[remoteRel] = new RemoteItemState(folder.ItemId, rootId, folder.ParentItemId, folder.Name, folder.RelativePath, folder.ItemType, folder.SizeBytes, folder.MtimeUtc, folder.ContentHash, folder.Version, 0, RemotePlanningState.MetadataOnly);
                }
                break;
            case OperationType.CreateFile:
            case OperationType.UpdateFile:
                var path = PathRules.ToAbsolute(mapping.LocalPath, rel);
                var info = new FileInfo(path); if (!info.Exists) throw new FileNotFoundException("Local file is unavailable.", path);
                var hash = await HashAsync(path, ct);
                if (existing is not null && string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase)) { }
                else
                {
                    try { await _transfers.UploadAsync(path, new UploadTarget(rootId, FindParent(remote, remoteRel), Path.GetFileName(remoteRel), existing?.ItemId, existing?.Version, op.OperationId, mapping.MappingId), ct); }
                    catch (SyncApiException e) when (e.Code == "SYNC_NAME_CONFLICT")
                    {
                        // The NAS already has a file at this canonical path but the cached
                        // inventory didn't (stale cursor). Reconcile by binding to it instead
                        // of failing the op and re-creating CreateFile forever (name-exists loop).
                        if (await TryBindExistingAsync(mapping, rootId, remoteRel, ct) is null) throw;
                    }
                    await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
                    await RecordAsync(mapping, rel, "Uploaded", "Synced", null, ct);
                }
                break;
            case OperationType.Move:
                if (op.SecondaryPath is null) throw new IOException("Move destination is missing.");
                if (!remote.TryGetValue(remoteRel, out var moved)) throw new SyncApiException(System.Net.HttpStatusCode.NotFound, "SYNC_ITEM_NOT_FOUND", "The moved item no longer exists on Oryno NAS.");
                var destination = PathRules.NormalizeRelative(op.SecondaryPath);
                var destRemote = mapping.Scope(destination);
                await api.MoveItemAsync(moved.ItemId, FindParent(remote, destRemote), Path.GetFileName(destRemote), moved.Version, op.OperationId, ct);
                await mappings.RemoveItemAsync(mapping.MappingId, rel, ct);
                if (local.TryGetValue(rel, out var old)) await mappings.UpsertItemAsync(old with { RelativePath = destination }, ct);
                await RecordAsync(mapping, destination, "Moved", "Synced", null, ct);
                break;
            case OperationType.Delete:
                if (existing is not null) await api.DeleteItemAsync(existing.ItemId, op.OperationId, ct);
                await RecordAsync(mapping, rel, "Deleted", "Synced", null, ct);
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
                var downloaded = new FileInfo(downloadPath); await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.File, downloaded.Length, downloaded.LastWriteTimeUtc, SyncItemState.Synced), ct); await RecordAsync(mapping, rel, "Downloaded", "Synced", null, ct);
                break;
            case OperationType.Conflict:
                throw new SyncApiException(System.Net.HttpStatusCode.Conflict, "SYNC_CONFLICT", "This item was changed both locally and on Oryno NAS.");
        }
        await mappings.UpdateOperationAsync(op with { State = OperationState.Completed, LastError = null, NextAttemptAt = DateTimeOffset.MaxValue }, ct);
    }

    private async Task ApplyRemoteOnlyAsync(SyncMapping mapping, Guid rootId, IReadOnlyDictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var pendingPaths = (await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.MaxValue, ct)).Select(x => PathRules.NormalizeRelative(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote.Values.OrderBy(x => x.RelativePath.Count(c => c == '\\')).OrderBy(x => x.RelativePath.Count(c => c == '/')))
        {
            if (!mapping.InDestination(item.RelativePath)) continue;          // outside the selected destination subtree
            var localRel = mapping.ToLocalRel(item.RelativePath);
            if (localRel is null) continue;                                    // the destination boundary itself — not a local item
            if (_ignores.IsIgnored(localRel)) continue;                        // never materialize temporary/system/staging content locally
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
                // Content is gone server-side. Drop it from the cached inventory so it is
                // not re-attempted every pass (the endless SYNC_CONTENT_MISSING download loop).
                await remoteState.MarkItemMissingAsync(rootId, item.ItemId.ToString(), ct);
                await RecordAsync(mapping, localRel, "Download", "Error", e.Message, ct);
            }
        }
        // Intentionally do NOT delete local-only files on a routine pass. The local folder is
        // the source of truth for local changes: deletions propagate via the watcher → Delete op
        // → server DeleteItem above. Auto-deleting local-only files here caused the destructive
        // mirror (data loss) and the delete → re-download → re-create conflict loop.
    }

    private async Task<RemoteItemState?> TryBindExistingAsync(SyncMapping mapping, Guid rootId, string rel, CancellationToken ct)
    {
        foreach (var item in await remoteState.GetRemoteItemsAsync(rootId, ct))
        {
            if (item.IsDeleted) continue;
            if (string.Equals(PathRules.NormalizeRelative(item.RelativePath), rel, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static string ConflictCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); var candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}){ext}"); var n = 2; while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}-{n++}){ext}"); return candidate;
    }

    private async Task<string> HashAsync(string path, CancellationToken ct) { await _hashGate.WaitAsync(ct); try { return await _hasher.ComputeAsync(path, ct); } finally { _hashGate.Release(); } }
    private static Guid? FindParent(IReadOnlyDictionary<string, RemoteItemState> remote, string path) { var parent = Path.GetDirectoryName(path)?.Replace('/', '\\'); return parent is not null && remote.TryGetValue(parent, out var item) ? item.ItemId : null; }
    private async Task RecordAsync(SyncMapping mapping, string path, string action, string status, string? error, CancellationToken ct) { var row = new SyncActivityEvent(Guid.NewGuid(), mapping.MappingId, path, action, status, DateTimeOffset.UtcNow, error is null ? null : "SYNC_ERROR", error); await mappings.RecordActivityAsync(row, ct); Activity?.Invoke(row); }
}
