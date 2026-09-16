namespace OrynoSync.Core;

/// <summary>Classification of an error that is currently sitting in the queue (§7/§8).</summary>
public enum ErrorOutcome { Resolved, Retrying, PermanentConflict, StaleOrphan }

public sealed record ErrorReconcileInput(
    Guid OperationId,
    Guid MappingId,
    OperationType Type,
    string RelativePath,
    string ServerRelativePath,
    string? LastError,
    int AttemptCount,
    bool LocalExists,
    long? LocalSize,
    string? LocalHash,
    bool RemoteExists,
    string? RemoteHash,
    long? RemoteSize,
    bool RemoteIsDirectory,
    bool LegacyPrefixPath,
    bool RemoteAtLegacyPath = false);

public sealed record ErrorReconcileResult(
    Guid OperationId,
    Guid MappingId,
    string RelativePath,
    OperationType Type,
    ErrorOutcome Outcome,
    string Reason,
    string UserMessage);

public sealed record ErrorReconcileReport(
    int Total,
    int Resolved,
    int Retrying,
    int PermanentConflict,
    int StaleOrphan,
    IReadOnlyList<ErrorReconcileResult> Results)
{
    public int ActiveAfter => Retrying + PermanentConflict;

    public string Summary =>
        $"errors={Total} resolved={Resolved} retrying={Retrying} conflicts={PermanentConflict} stale_orphan={StaleOrphan}";

    public string Describe(int activeBefore) =>
        $"BEFORE: {activeBefore} | ACTIVE AFTER: {ActiveAfter} | RESOLVED/ARCHIVED: {Resolved + StaleOrphan} " +
        $"(resolved={Resolved}, stale_orphan={StaleOrphan}, retrying={Retrying}, conflicts={PermanentConflict})";
}

/// <summary>
/// Decides what an existing error really is right now: the queue keeps operations that were
/// failed days ago and the red counter must only contain things a human still has to look at.
/// The classifier is pure so it can be unit-tested without a server or a file system.
/// </summary>
public static class ErrorReconciler
{
    private static readonly string[] TransientMarkers =
        ["timeout", "timed out", "temporarily", "503", "502", "504", "internal server error", "connection", "network", "closed unexpectedly", "try again"];

    private static readonly string[] NameExistsMarkers = ["name exists", "already exists", "name already exists", "409", "name_conflict"];

    public static bool IsTransientError(string? error) =>
        error is { Length: > 0 } e && TransientMarkers.Any(m => e.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsNameExistsError(string? error) =>
        error is { Length: > 0 } e && NameExistsMarkers.Any(m => e.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Paths polluted by the historical double-destination bug ("files\..." inside the root scope).</summary>
    public static bool HasLegacyPrefix(string relativePath)
    {
        var p = (relativePath ?? string.Empty).Replace('/', '\\').TrimStart('\\');
        return p.StartsWith("files\\", StringComparison.OrdinalIgnoreCase) || p.Equals("files", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HashesEqual(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static ErrorReconcileResult Classify(ErrorReconcileInput i)
    {
        ErrorReconcileResult Done(ErrorOutcome outcome, string reason, string message) =>
            new(i.OperationId, i.MappingId, i.RelativePath, i.Type, outcome, reason, message);

        var path = (i.RelativePath ?? string.Empty).Trim();
        if (path.Length == 0)
            return Done(ErrorOutcome.StaleOrphan, "operation has no path", "Operation has no path — archived as orphan.");

        if (i.LegacyPrefixPath || HasLegacyPrefix(path))
            return Done(ErrorOutcome.StaleOrphan, "legacy destination prefix", "Left over from the old destination-prefix defect — archived as orphan.");

        // A deletion that already happened mirrors itself: nothing left remote, nothing active.
        if (i.Type == OperationType.Delete && !i.RemoteExists)
            return Done(i.LocalExists ? ErrorOutcome.Retrying : ErrorOutcome.Resolved,
                i.LocalExists ? "delete still required" : "already deleted remotely",
                i.LocalExists ? "Local copy is back; delete will be retried." : "Item is gone on both sides — error closed.");

        if (i.Type == OperationType.Delete && !i.LocalExists && i.RemoteExists)
            return Done(ErrorOutcome.Retrying, "delete not applied yet", "Delete is still outstanding and will be retried.");

        if (!i.LocalExists && !i.RemoteExists)
            return Done(ErrorOutcome.StaleOrphan, "neither side has the item", "Neither the local nor the remote copy exists — archived as orphan.");

        if (!i.LocalExists && i.RemoteExists)
            return Done(ErrorOutcome.Resolved, "remote copy authoritative", "Local copy is gone; the NAS copy is authoritative — error closed.");

        // Folder creation that the server already satisfied.
        if (i.Type == OperationType.CreateDirectory && i.RemoteExists && i.RemoteIsDirectory)
            return Done(ErrorOutcome.Resolved, i.RemoteAtLegacyPath ? "remote folder exists (legacy layout)" : "remote folder exists",
                i.RemoteAtLegacyPath
                    ? "Folder already exists on Oryno NAS, but outside the chosen destination — error closed. The layout difference is reported separately."
                    : "Folder already exists on Oryno NAS — error closed.");

        // The classic "name exists" answer: the item is there, the client just could not prove it before.
        if (i.RemoteExists && IsNameExistsError(i.LastError))
            return Done(ErrorOutcome.Resolved, "server already has this item", "Oryno NAS already has this item — error closed.");

        // Same content on both sides: the operation is effectively done regardless of its recorded failure.
        if (i.RemoteExists && HashesEqual(i.LocalHash, i.RemoteHash))
            return Done(ErrorOutcome.Resolved, i.RemoteAtLegacyPath ? "identical content at legacy location" : "identical content hash",
                "Both copies hold identical content — error closed.");

        // Item exists remotely outside the current destination (pre-fix root-level layout): the upload
        // already happened, so retrying only creates duplicates. Close the error and keep the item.
        if (i.RemoteExists && i.RemoteAtLegacyPath)
            return Done(ErrorOutcome.Resolved, "remote item at legacy location",
                "Oryno NAS has this item outside the chosen destination — error closed to avoid a duplicate upload.");

        // Sizes match but we could not read the remote hash: treat as satisfied only when the
        // remote file exists for the same path and the recorded failure was about naming.
        if (i.RemoteExists && i.LocalExists && i.LocalSize is { } ls && i.RemoteSize is { } rs && ls == rs && i.RemoteHash is null)
            return Done(ErrorOutcome.Resolved, "same size, remote item present", "NAS holds an item with the same size for this path — error closed.");

        if (i.LocalExists && i.RemoteExists)
            return Done(ErrorOutcome.PermanentConflict, "different content on both sides",
                "Oryno NAS holds a different version of this file — needs your decision (keep local or keep NAS).");

        // Local file is back / never left, remote copy missing: transient failures retry, the rest need attention.
        if (IsTransientError(i.LastError))
            return Done(ErrorOutcome.Retrying, "transient failure", "Temporary server problem — retrying automatically.");

        return Done(ErrorOutcome.PermanentConflict, "unresolved, needs attention",
            "This file could not be uploaded and retrying alone will not fix it — open the entry for details.");
    }
}

/// <summary>Runs the classification for every operation the queue still reports as an error and applies the outcome.</summary>
public static class ErrorReconcileRunner
{
    /// <summary>Reconciles all Failed/FailedPermanent/Cancelled operations of the given mappings.</summary>
    public static async Task<ErrorReconcileReport> ReconcileAsync(
        ISyncMappingStore store,
        IRemoteStateStore remoteStore,
        IReadOnlyList<SyncMapping> mappings,
        Func<string, CancellationToken, Task<string?>>? hashLocal,
        CancellationToken ct = default)
    {
        var results = new List<ErrorReconcileResult>();
        var remotes = new Dictionary<Guid, Dictionary<string, RemoteItemState>>();
        var legacyLayoutMatches = 0;

        foreach (var mapping in mappings)
        {
            ct.ThrowIfCancellationRequested();
            var ops = await store.GetOperationsForReconcileAsync(mapping.MappingId, ct);
            if (ops.Count == 0) continue;

            if (mapping.ServerRootId is { } rootId && !remotes.ContainsKey(rootId))
            {
                var items = await remoteStore.GetRemoteItemsAsync(rootId, ct);
                var dict = new Dictionary<string, RemoteItemState>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (item.IsDeleted) continue;
                    var key = PathRules.NormalizeRelative(item.RelativePath).Trim('\\');
                    dict[key] = item;
                }
                remotes[rootId] = dict;
            }
            var remote = mapping.ServerRootId is { } rid && remotes.TryGetValue(rid, out var d) ? d : [];

            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                var rel = op.RelativePath ?? string.Empty;
                var localPath = rel.Length == 0 ? mapping.LocalPath : Path.Combine(mapping.LocalPath, rel);
                bool localExists;
                long? localSize = null;
                try
                {
                    if (File.Exists(localPath)) { localExists = true; localSize = new FileInfo(localPath).Length; }
                    else localExists = Directory.Exists(localPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
                {
                    localExists = false;
                }

                var serverRel = mapping.Scope(rel);
                var remoteKey = PathRules.NormalizeRelative(serverRel).Trim('\\');
                remote.TryGetValue(remoteKey, out var remoteItem);
                var atLegacyPath = false;
                if (remoteItem is null)
                {
                    // Pre-fix clients wrote items under the root without the destination prefix
                    // ("Ламинат\..." instead of "Работа\Ламинат\..."). The upload already happened there,
                    // so it must be recognised instead of being retried into a duplicate.
                    var legacyKey = PathRules.NormalizeRelative(rel).Trim('\\');
                    if (remote.TryGetValue(legacyKey, out var legacyItem))
                    {
                        remoteItem = legacyItem;
                        atLegacyPath = true;
                    }
                }

                string? localHash = null;
                if (hashLocal is not null && localExists && localSize is not null && remoteItem?.ContentHash is { Length: > 0 } && remoteItem.SizeBytes == localSize)
                {
                    try { localHash = await hashLocal(localPath, ct); }
                    catch (Exception) { localHash = null; }
                }

                var input = new ErrorReconcileInput(
                    op.OperationId, mapping.MappingId, op.Type, rel, serverRel,
                    op.LastError, op.AttemptCount,
                    localExists, localSize, localHash,
                    remoteItem is not null, remoteItem?.ContentHash, remoteItem?.SizeBytes,
                    string.Equals(remoteItem?.ItemType, "Directory", StringComparison.OrdinalIgnoreCase),
                    ErrorReconciler.HasLegacyPrefix(rel),
                    atLegacyPath);

                results.Add(ErrorReconciler.Classify(input));
                if (atLegacyPath) legacyLayoutMatches++;
            }
        }

        foreach (var group in results.GroupBy(r => (r.Outcome, r.Reason)))
            SyncDiagnostics.Report("ERROR_RECONCILE", $"outcome={group.Key.Outcome} reason={group.Key.Reason} count={group.Count()}");
        if (legacyLayoutMatches > 0)
            SyncDiagnostics.Report("LEGACY_REMOTE_LAYOUT", $"count={legacyLayoutMatches} note=remote items found outside the chosen destination (pre-fix layout); errors closed without re-uploading");

        foreach (var result in results.Where(r => r.Outcome is ErrorOutcome.Resolved or ErrorOutcome.StaleOrphan))
        {
            await store.ArchiveOperationsAsync(
                [new ArchivedError(result.OperationId, result.MappingId, result.RelativePath, result.Type.ToString(),
                    result.Outcome.ToString(), result.Reason, result.UserMessage, DateTimeOffset.UtcNow)], ct);
        }

        foreach (var result in results.Where(r => r.Outcome == ErrorOutcome.Retrying))
            await store.ReArmOperationAsync(result.OperationId, result.Reason, ct);

        foreach (var result in results.Where(r => r.Outcome == ErrorOutcome.PermanentConflict))
            await store.SetOperationErrorAsync(result.OperationId, result.UserMessage, ct);

        return new ErrorReconcileReport(
            results.Count,
            results.Count(r => r.Outcome == ErrorOutcome.Resolved),
            results.Count(r => r.Outcome == ErrorOutcome.Retrying),
            results.Count(r => r.Outcome == ErrorOutcome.PermanentConflict),
            results.Count(r => r.Outcome == ErrorOutcome.StaleOrphan),
            results);
    }
}
