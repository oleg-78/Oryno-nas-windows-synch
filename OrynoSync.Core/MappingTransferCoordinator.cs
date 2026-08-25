namespace OrynoSync.Core;

public sealed class MappingTransferCoordinator(ISyncMappingStore mappings, IRemoteStateStore remoteState, IContentTransferApi api, string tempRoot)
{
    private readonly ResumableTransferClient _transfers = new(api, tempRoot, 3, new SqliteTransferSessionStore(Path.Combine(tempRoot, "transfer-state.db")));
    private readonly SemaphoreSlim _hashGate = new(2);
    private readonly IContentHasher _hasher = new Blake3ContentHasher();
    public event Action<SyncActivityEvent>? Activity;

    public async Task ProcessAsync(IReadOnlyList<SyncMapping> allMappings, CancellationToken ct = default)
    {
        foreach (var mapping in allMappings.Where(x => x.Enabled && x.ServerRootId is not null && x.Status != MappingStatus.Paused))
            await ProcessMappingAsync(mapping, ct);
    }

    private async Task ProcessMappingAsync(SyncMapping mapping, CancellationToken ct)
    {
        var rootId = mapping.ServerRootId!.Value;
        var remote = (await remoteState.GetRemoteItemsAsync(rootId, ct)).Where(x => !x.IsDeleted).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var local = (await mappings.GetItemsAsync(mapping.MappingId, ct)).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var pending = await mappings.GetPendingAsync(mapping.MappingId, DateTimeOffset.UtcNow, ct);
        if (!string.Equals(mapping.InventoryState, "Normalized", StringComparison.OrdinalIgnoreCase))
        {
            var desired = await SnapshotAsync(mapping.LocalPath, ct);
            var plan = QueueNormalizer.Plan(desired, remote.Values, pending);
            var normalized = new List<MappingPendingOperation>();
            normalized.AddRange(plan.Conflicts.Select(path => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, OperationType.Conflict, path, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, OperationState.Failed, "SYNC_CONFLICT: local and NAS content differ.")));
            normalized.AddRange(plan.Operations.Select(operation => new MappingPendingOperation(Guid.NewGuid(), mapping.MappingId, operation.Type, operation.RelativePath, null, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null)));
            await mappings.ReplacePendingAsync(mapping.MappingId, normalized, ct);
            await mappings.UpdateMappingAsync(mapping with { InventoryState = "Normalized", UpdatedAt = DateTimeOffset.UtcNow }, ct);
            pending = normalized.Where(x => x.State is OperationState.Pending or OperationState.Failed).ToArray();
        }
        foreach (var operation in pending)
        {
            try { await ProcessOperationAsync(mapping, rootId, operation, remote, local, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
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
        { ct.ThrowIfCancellationRequested(); var rel = PathRules.ToRelative(root, path); if (!PathRules.IsWindowsCompatible(rel)) continue; if (Directory.Exists(path)) result.Add(new(rel, ItemType.Directory, 0, Directory.GetLastWriteTimeUtc(path), null)); else { var info = new FileInfo(path); result.Add(new(rel, ItemType.File, info.Length, info.LastWriteTimeUtc, await hasher.ComputeAsync(path, ct))); } }
        return result;
    }

    private async Task ProcessOperationAsync(SyncMapping mapping, Guid rootId, MappingPendingOperation op, Dictionary<string, RemoteItemState> remote, IReadOnlyDictionary<string, MappingLocalItem> local, CancellationToken ct)
    {
        var rel = PathRules.NormalizeRelative(op.RelativePath);
        if (!PathRules.IsWindowsCompatible(rel)) throw new IOException("invalid filename: Windows cannot materialize this path.");
        remote.TryGetValue(rel, out var existing);
        switch (op.Type)
        {
            case OperationType.CreateDirectory:
                if (existing is null)
                {
                    var folder = await api.CreateFolderAsync(rootId, FindParent(remote, rel), Path.GetFileName(rel), op.OperationId, ct);
                    remote[rel] = new RemoteItemState(folder.ItemId, rootId, folder.ParentItemId, folder.Name, folder.RelativePath, folder.ItemType, folder.SizeBytes, folder.MtimeUtc, folder.ContentHash, folder.Version, 0, RemotePlanningState.MetadataOnly);
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
                    var result = await _transfers.UploadAsync(path, new UploadTarget(rootId, FindParent(remote, rel), Path.GetFileName(rel), existing?.ItemId, existing?.Version, op.OperationId), ct);
                    await mappings.UpsertItemAsync(new(mapping.MappingId, rel, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
                    await RecordAsync(mapping, rel, "Uploaded", "Synced", null, ct);
                    _ = result;
                }
                break;
            case OperationType.Move:
                if (op.SecondaryPath is null) throw new IOException("Move destination is missing.");
                if (!remote.TryGetValue(rel, out var moved)) throw new SyncApiException(System.Net.HttpStatusCode.NotFound, "SYNC_ITEM_NOT_FOUND", "The moved item no longer exists on Oryno NAS.");
                var destination = PathRules.NormalizeRelative(op.SecondaryPath);
                await api.MoveItemAsync(moved.ItemId, FindParent(remote, destination), Path.GetFileName(destination), moved.Version, op.OperationId, ct);
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
        foreach (var item in remote.Values.OrderBy(x => x.RelativePath.Count(c => c == '\\')))
        {
            if (!PathRules.IsWindowsCompatible(item.RelativePath)) { await RecordAsync(mapping, item.RelativePath, "Download", "Error", "invalid filename: Windows cannot materialize this path.", ct); continue; }
            var full = PathRules.ToAbsolute(mapping.LocalPath, item.RelativePath);
            if (item.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase)) { Directory.CreateDirectory(full); continue; }
            if (item.ContentHash is null) continue;
            if (File.Exists(full))
            {
                var localHash = await HashAsync(full, ct);
                if (string.Equals(localHash, item.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (pendingPaths.Contains(item.RelativePath))
                {
                    var conflict = ConflictCopyPath(full);
                    await _transfers.DownloadAsync(new DownloadTarget(item.ItemId, item.Version, item.RelativePath, item.SizeBytes ?? 0, item.ContentHash, item.MtimeUtc), conflict, ct);
                    await RecordAsync(mapping, item.RelativePath, "Conflict", "Error", "This file was changed both locally and on Oryno NAS.", ct);
                    continue;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            RemoteItemDto current;
            try { current = await api.GetItemAsync(item.ItemId, ct); } catch (SyncApiException e) when (e.Code == "SYNC_ITEM_NOT_FOUND") { continue; }
            await _transfers.DownloadAsync(new DownloadTarget(current.ItemId, current.Version, current.RelativePath, current.SizeBytes ?? 0, current.ContentHash ?? item.ContentHash!, current.MtimeUtc), full, ct);
            var info = new FileInfo(full); await mappings.UpsertItemAsync(new(mapping.MappingId, item.RelativePath, ItemType.File, info.Length, info.LastWriteTimeUtc, SyncItemState.Synced), ct);
            await RecordAsync(mapping, item.RelativePath, "Downloaded", "Synced", null, ct);
        }
        foreach (var item in local.Values.Where(x => !remote.ContainsKey(x.RelativePath)).ToArray())
        {
            if (pendingPaths.Contains(item.RelativePath)) continue;
            var full = PathRules.ToAbsolute(mapping.LocalPath, item.RelativePath);
            if (item.ItemType == ItemType.Directory) Directory.Delete(full, true); else if (File.Exists(full)) File.Delete(full);
            await mappings.RemoveItemAsync(mapping.MappingId, item.RelativePath, ct);
            await RecordAsync(mapping, item.RelativePath, "Deleted", "Synced", null, ct);
        }
    }

    private static string ConflictCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); var candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}){ext}"); var n = 2; while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem} (conflict Oryno {DateTime.Now:yyyyMMdd-HHmmss}-{n++}){ext}"); return candidate;
    }

    private async Task<string> HashAsync(string path, CancellationToken ct) { await _hashGate.WaitAsync(ct); try { return await _hasher.ComputeAsync(path, ct); } finally { _hashGate.Release(); } }
    private static Guid? FindParent(IReadOnlyDictionary<string, RemoteItemState> remote, string path) { var parent = Path.GetDirectoryName(path)?.Replace('/', '\\'); return parent is not null && remote.TryGetValue(parent, out var item) ? item.ItemId : null; }
    private async Task RecordAsync(SyncMapping mapping, string path, string action, string status, string? error, CancellationToken ct) { var row = new SyncActivityEvent(Guid.NewGuid(), mapping.MappingId, path, action, status, DateTimeOffset.UtcNow, error is null ? null : "SYNC_ERROR", error); await mappings.RecordActivityAsync(row, ct); Activity?.Invoke(row); }
}
