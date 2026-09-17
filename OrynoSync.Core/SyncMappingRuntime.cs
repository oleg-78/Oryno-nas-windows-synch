namespace OrynoSync.Core;

public sealed class SyncMappingRuntimeManager(ISyncMappingStore store) : IDisposable
{
    private readonly Dictionary<Guid, Runtime> _runtimes = [];

    public event Action<SyncMapping, string>? Activity;
    public event Action<SyncMapping, ScanProgress>? Progress;
    public IReadOnlyCollection<Guid> ActiveMappings => _runtimes.Keys.ToArray();

    /// <summary>
    /// §9/§13: shared with the app loop. A local change pulses the wake signal so the queue is drained at
    /// once instead of waiting for the next remote poll, and files that the client itself materialised are
    /// registered in the suppression set so the watcher does not upload them straight back.
    /// </summary>
    public SyncWakeSignal? Wake { get; set; }
    public LocalMutationSuppression? Suppression { get; set; }

    /// <summary>
    /// §11 (recursive audit): the full local↔remote consistency reconciliation, run once at the end of
    /// every full scan (first Start, re-Start, watcher overflow, explicit rescan) — never on the 60 s
    /// cadence. The app wires this to <c>MappingTransferCoordinator.RebuildAsync</c>, which verifies the
    /// remote content physically before it concludes that a local file needs no upload.
    /// </summary>
    public Func<SyncMapping, CancellationToken, Task>? ConsistencyReconcile { get; set; }

    /// <summary>
    /// §2/§13/§15: the persisted desired state decides what happens after a restart.
    /// Enabled=true → continuous sync resumes without any click; Enabled=false → it stays Stopped.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RestoreAsync(CancellationToken ct = default)
    {
        var resumed = new List<Guid>();
        foreach (var mapping in await store.GetMappingsAsync(ct))
        {
            if (!mapping.Enabled)
            {
                SyncDiagnostics.Report("SYNC_RESTORE", $"mapping={mapping.MappingId} desired=enabled:false action=stay_stopped path={mapping.LocalPath}");
                continue;
            }
            await StartAsync(mapping, ct);
            var current = await store.GetMappingAsync(mapping.MappingId, ct);
            if (current is not null && current.Status is MappingStatus.Stopped or MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync or MappingStatus.Offline)
                await SetStatusAsync(current, MappingStatus.Syncing, null, ct);
            resumed.Add(mapping.MappingId);
            SyncDiagnostics.Report("SYNC_RESTORE", $"mapping={mapping.MappingId} desired=enabled:true action=auto_resume path={mapping.LocalPath}");
        }
        return resumed;
    }

    // Starts the watcher and schedules reconciliation. It intentionally does not
    // await the scan: callers can close a dialog and keep using the app.
    public async Task StartAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        // §12: Start is idempotent — never spin up a second engine (watcher/scan loop) for a mapping.
        if (_runtimes.TryGetValue(mapping.MappingId, out var existing))
        {
            existing.StartScan();
            SyncDiagnostics.Report("SYNC_START", $"mapping={mapping.MappingId} action=noop reason=already_running");
            return;
        }
        if (!Directory.Exists(mapping.LocalPath))
        {
            await SetStatusAsync(mapping, MappingStatus.LocalFolderUnavailable, "Local folder unavailable", ct);
            return;
        }
        if (mapping.ServerRootId is null)
        {
            await SetStatusAsync(mapping, MappingStatus.ServerRootUnavailable, "NAS destination missing", ct);
            return;
        }

        var runtime = new Runtime(
            mapping,
            store,
            text => Activity?.Invoke(mapping, text),
            progress => Progress?.Invoke(mapping, progress),
            Wake,
            Suppression,
            ConsistencyReconcile);
        _runtimes[mapping.MappingId] = runtime;
        if (mapping.Status is not MappingStatus.ReadyForPreflight and not MappingStatus.ReadyToSync and not MappingStatus.Stopped)
            await SetStatusAsync(mapping, MappingStatus.Scanning, null, ct);
        runtime.Start();
    }

    /// <summary>§2/§12: persists the user's decision — Start means enabled, Stop means disabled.</summary>
    public async Task SetDesiredStateAsync(SyncMapping mapping, bool enabled, CancellationToken ct = default)
    {
        var current = await store.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        var status = enabled ? MappingStatus.Syncing : MappingStatus.Stopped;
        var updated = current with { Enabled = enabled, Status = status, LastError = enabled ? current.LastError : null, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateMappingAsync(updated, ct);
        SyncDiagnostics.Report("SYNC_DESIRED_STATE", $"mapping={mapping.MappingId} enabled={enabled.ToString().ToLowerInvariant()} status={status}");
        Progress?.Invoke(updated, new ScanProgress(0, 0, 0));
    }

    public async Task StartSyncAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        if (mapping.ServerRootId is null)
        {
            await SetStatusAsync(mapping, MappingStatus.ServerRootUnavailable, "NAS destination missing", ct);
            return;
        }
        // Transition: ReadyForPreflight/ReadyToSync → Syncing (transfers begin)
        await SetDesiredStateAsync(mapping, true, ct);
        if (_runtimes.TryGetValue(mapping.MappingId, out var runtime)) runtime.StartScan();
    }

    /// <summary>§2/§15: a user Stop survives an app restart; local changes stay queued for the next Start.</summary>
    public async Task StopAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        Stop(mapping.MappingId);
        await SetDesiredStateAsync(mapping, false, ct);
    }

    public async Task SetPausedAsync(SyncMapping mapping, bool paused, CancellationToken ct = default)
    {
        if (mapping.ServerRootId is null)
        {
            await SetStatusAsync(mapping, MappingStatus.ServerRootUnavailable, "NAS destination missing", ct);
            return;
        }
        if (_runtimes.TryGetValue(mapping.MappingId, out var runtime)) runtime.Paused = paused;
        await SetStatusAsync(mapping, paused ? MappingStatus.Paused : MappingStatus.Scanning, null, ct);
        if (!paused && _runtimes.TryGetValue(mapping.MappingId, out runtime)) runtime.StartScan();
    }

    /// <summary>
    /// §15: ask every running mapping for a full reconciliation again (used after a watcher buffer overflow
    /// and by the UI). The per-mapping scan gate coalesces a burst of requests into one running scan.
    /// </summary>
    public void RequestRescan(string reason = "request")
    {
        foreach (var runtime in _runtimes.Values) runtime.StartScan(reason);
    }

    public void Stop(Guid mappingId)
    {
        if (_runtimes.Remove(mappingId, out var runtime)) runtime.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _runtimes.Keys.ToArray()) Stop(id);
    }

    private async Task SetStatusAsync(SyncMapping mapping, MappingStatus status, string? error, CancellationToken ct = default)
    {
        var current = await store.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        var updated = current with { Status = status, LastError = error, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateMappingAsync(updated, ct);
        Progress?.Invoke(updated, new ScanProgress(0, 0, 0));
    }

    private sealed class Runtime : IDisposable
    {
        private readonly SyncMapping _mapping;
        private readonly ISyncMappingStore _store;
        private readonly Action<string> _activity;
        private readonly Action<ScanProgress> _progress;
        private readonly FileSystemWatcher _watcher;
        private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private readonly List<MappingLocalItem> _batchItems = new(100);
        private readonly List<MappingPendingOperation> _batchOperations = new(100);
        private readonly SyncWakeSignal? _wake;
        private readonly LocalMutationSuppression? _suppression;
        private readonly Func<SyncMapping, CancellationToken, Task>? _consistency;
        private readonly IgnoreRules _ignores = new();
        private string _scanReason = "request";
        private bool _disposed;

        public bool Paused { get; set; }

        public Runtime(SyncMapping mapping, ISyncMappingStore store, Action<string> activity, Action<ScanProgress> progress,
            SyncWakeSignal? wake = null, LocalMutationSuppression? suppression = null,
            Func<SyncMapping, CancellationToken, Task>? consistency = null)
        {
            _mapping = mapping;
            _store = store;
            _activity = activity;
            _progress = progress;
            _wake = wake;
            _suppression = suppression;
            _consistency = consistency;
            _watcher = new FileSystemWatcher(mapping.LocalPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 65536
            };
            _watcher.Created += On;
            _watcher.Changed += On;
            _watcher.Deleted += On;
            _watcher.Renamed += OnRename;
            _watcher.Error += OnError;
        }

        public void Start()
        {
            _watcher.EnableRaisingEvents = true;
            StartScan("start");
        }

        public void StartScan(string reason = "request")
        {
            if (_disposed) return;
            _scanReason = reason;
            _ = Task.Run(() => ScanAsync(_lifetime.Token), CancellationToken.None);
        }

        private async Task ScanAsync(CancellationToken ct)
        {
            if (!await _scanGate.WaitAsync(0, ct)) return;
            try
            {
                // §4/§15: a full scan is the rare fallback (start / watcher overflow), never the answer to a
                // single file change. Logged so the load test can prove that a 20-file burst caused zero scans.
                SyncDiagnostics.Report("FULL_SCAN", $"mapping={_mapping.MappingId} reason={_scanReason} path={_mapping.LocalPath}");
                if (!Directory.Exists(_mapping.LocalPath))
                {
                    await UpdateStatusAsync(MappingStatus.LocalFolderUnavailable, "Local folder unavailable", ct);
                    return;
                }

                var oldItems = (await _store.GetItemsAsync(_mapping.MappingId, ct))
                    .ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ignores = _ignores;
                var files = 0;
                var folders = 0;
                var changes = 0;
                var lastProgress = DateTimeOffset.MinValue;
                _batchItems.Clear();
                _batchOperations.Clear();
                _progress(new ScanProgress(0, 0, 0));

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var path in Directory.EnumerateFileSystemEntries(_mapping.LocalPath, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = PathRules.ToRelative(_mapping.LocalPath, path);
                    if (ignores.IsIgnored(rel)) continue;

                    try
                    {
                        var isDirectory = Directory.Exists(path);
                        var info = isDirectory ? null : new FileInfo(path);
                        if (isDirectory) folders++; else files++;
                        seen.Add(rel);

                        if (!oldItems.TryGetValue(rel, out var old))
                        {
                            _batchItems.Add(new(_mapping.MappingId, rel,
                                isDirectory ? ItemType.Directory : ItemType.File,
                                isDirectory ? 0 : info!.Length,
                                isDirectory ? Directory.GetLastWriteTimeUtc(path) : info!.LastWriteTimeUtc));
                            _batchOperations.Add(NewOperation(isDirectory ? OperationType.CreateDirectory : OperationType.CreateFile, rel));
                            changes++;
                        }
                        else if (!isDirectory && (old.Size != info!.Length || old.Mtime != info.LastWriteTimeUtc))
                        {
                            _batchItems.Add(new(_mapping.MappingId, rel, ItemType.File, info.Length, info.LastWriteTimeUtc));
                            _batchOperations.Add(NewOperation(OperationType.UpdateFile, rel));
                            changes++;
                        }
                        if (_batchItems.Count >= 100) await FlushBatchAsync();
                    }
                    catch (IOException) { _activity($"Skipped inaccessible item: {rel}"); }
                    catch (UnauthorizedAccessException) { _activity($"Skipped protected item: {rel}"); }

                    if ((DateTimeOffset.UtcNow - lastProgress).TotalMilliseconds >= 250)
                    {
                        lastProgress = DateTimeOffset.UtcNow;
                        _progress(new ScanProgress(files, folders, changes));
                    }
                }

                await FlushBatchAsync();
                foreach (var missing in oldItems.Values.Where(x => !seen.Contains(x.RelativePath) && !ignores.IsIgnored(x.RelativePath)))
                {
                    ct.ThrowIfCancellationRequested();
                    // §1: keep a tombstone (DeletedLocal) instead of forgetting the item. The executor and
                    // the queue planner use it to send a server DELETE instead of re-downloading the file.
                    await _store.UpsertItemAsync(missing with { SyncState = SyncItemState.DeletedLocal }, ct);
                    await EnqueueAsync(OperationType.Delete, missing.RelativePath, ct);
                    changes++;
                }

                var current = await _store.GetMappingAsync(_mapping.MappingId, ct) ?? _mapping;
                var finalStatus = _mapping.ServerRootId is null
                    ? MappingStatus.ServerRootUnavailable
                    : current.Status is MappingStatus.Syncing or MappingStatus.Scanning
                        ? current.Status  // don't overwrite active sync states
                        : current.Status is MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync
                            ? MappingStatus.ReadyForPreflight  // internal pre-sync states: keep as-is
                            : MappingStatus.Syncing;
                await UpdateStatusAsync(finalStatus, null, ct);
                _progress(new ScanProgress(files, folders, changes, true));

                // §11 (recursive audit): a FileSystemWatcher only reports changes that happen *while it
                // runs*. Files that were already on disk before the watcher started and never changed again
                // are invisible to it — that is exactly how the local tree could drift from the NAS while
                // the UI stayed on "Everything is up to date". So every full scan (start, re-start,
                // overflow, explicit rescan — never the 60 s cadence) ends with a real local↔remote
                // consistency reconciliation. It re-derives the tracking states and queues CreateFile for
                // every non-ignored local item that has no verified remote counterpart.
                if (_consistency is not null && !Paused)
                {
                    try
                    {
                        SyncDiagnostics.Report("CONSISTENCY_SCAN", $"mapping={_mapping.MappingId} reason={_scanReason} action=start");
                        await _consistency(_mapping, ct);
                        // §4: the reconciliation queued real work — drain it now instead of waiting for the poll.
                        _wake?.Pulse();
                        SyncDiagnostics.Report("CONSISTENCY_SCAN", $"mapping={_mapping.MappingId} reason={_scanReason} action=done");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        SyncDiagnostics.Report("CONSISTENCY_SCAN", $"mapping={_mapping.MappingId} reason={_scanReason} action=failed error={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _activity($"Scan failed: {ex.GetType().Name}");
                try { await UpdateStatusAsync(MappingStatus.Error, "Unable to scan this folder", CancellationToken.None); }
                catch { /* shutdown or removed mapping */ }
            }
            finally { _scanGate.Release(); }
        }

        private async Task UpdateStatusAsync(MappingStatus status, string? error, CancellationToken ct)
        {
            var current = await _store.GetMappingAsync(_mapping.MappingId, ct) ?? _mapping;
            await _store.UpdateMappingAsync(current with
            {
                Status = status,
                LastError = error,
                LastScanAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
        }

        private MappingPendingOperation NewOperation(OperationType type, string rel) =>
            new(Guid.NewGuid(), _mapping.MappingId, type, rel, null, DateTimeOffset.UtcNow, 0,
                DateTimeOffset.UtcNow, OperationState.Pending, null);

        private async Task FlushBatchAsync()
        {
            if (_batchItems.Count == 0 && _batchOperations.Count == 0) return;
            var queued = _batchOperations.Count;
            await _store.IndexBatchAsync(_batchItems, _batchOperations, _lifetime.Token);
            _batchItems.Clear();
            _batchOperations.Clear();
            if (queued > 0) _wake?.Pulse(); // §9: the scan produced work - drain it now
        }

        private void On(object? sender, FileSystemEventArgs e)
        {
            if (!Paused) Schedule(e.ChangeType, e.FullPath, null);
        }

        private void OnRename(object? sender, RenamedEventArgs e)
        {
            if (!Paused) Schedule(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
        }

        private void OnError(object? sender, ErrorEventArgs e)
        {
            if (e.GetException() is InternalBufferOverflowException)
            {
                // §15: events were lost, so one full reconciliation is the only honest recovery.
                _activity("Watcher overflow; rescanning folder");
                StartScan("overflow");
            }
        }

        private void Schedule(WatcherChangeTypes type, string path, string? oldPath)
        {
            var rel = PathRules.ToRelative(_mapping.LocalPath, path);
            if (_ignores.IsIgnored(rel)) return;
            // §13: the file this client just downloaded raises Created/Changed as well. Without this check
            // the watcher would queue an upload of the server's own content straight back (feedback loop).
            if (_suppression is not null)
            {
                var created = File.Exists(path) ? new FileInfo(path) : null;
                if (_suppression.IsSuppressed(rel, created) || _suppression.IsMaterialisedRecently(rel))
                {
                    SyncDiagnostics.Report("FEEDBACK_SUPPRESSED", $"mapping={_mapping.MappingId} path={rel} exact={created is not null}");
                    return;
                }
            }
            if (_debounce.Remove(rel, out var old)) old.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounce[rel] = cts;
            _ = ApplyAsync(type, rel, oldPath, cts.Token);
        }

        /// <summary>
        /// §3: Word/Excel keep the file locked for a moment after the last write. Wait until size and mtime
        /// stop moving and the file can be read, so a half-saved document is never queued for upload.
        /// </summary>
        private static async Task<bool> WaitForStableFileAsync(string full, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (!File.Exists(full)) return true;
                var info = new FileInfo(full);
                var size = info.Length;
                var mtime = info.LastWriteTimeUtc;
                await Task.Delay(250, ct);
                info.Refresh();
                if (!info.Exists) return true;
                if (info.Length != size || info.LastWriteTimeUtc != mtime) continue;
                try
                {
                    using var probe = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }

        private void RescheduleLater(WatcherChangeTypes type, string rel, string? oldPath, int delayMs = 1000)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, _lifetime.Token);
                    Schedule(type, PathRules.ToAbsolute(_mapping.LocalPath, rel), oldPath);
                }
                catch (OperationCanceledException) { }
            });
        }

        /// <summary>§1/§3: persist a local delete intent (tombstone) so the sync loop propagates DELETE
        /// instead of downloading the file back. Cleared only by a successful server delete or by the user
        /// creating the file again.</summary>
        private async Task MarkDeleteIntentAsync(string rel, CancellationToken ct)
        {
            try
            {
                await _store.UpsertItemAsync(new(_mapping.MappingId, PathRules.NormalizeRelative(rel), ItemType.File, 0, DateTimeOffset.UtcNow, SyncItemState.DeletedLocal), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _activity($"Could not record local delete intent for {rel}: {e.Message}");
            }
        }

        private async Task ApplyAsync(WatcherChangeTypes type, string rel, string? oldPath, CancellationToken ct)
        {
            try
            {
                // §1: a local delete intent must be durable BEFORE the debounce window, otherwise a
                // remote-only pass that is already running can re-download the file we just deleted
                // (observed: proof file came back with the server's mtime).
                if (type == WatcherChangeTypes.Deleted && !_ignores.IsIgnored(rel))
                    await MarkDeleteIntentAsync(rel, ct);
                await Task.Delay(750, ct);
                _debounce.Remove(rel);
                var full = PathRules.ToAbsolute(_mapping.LocalPath, rel);
                if (type == WatcherChangeTypes.Deleted || (!File.Exists(full) && !Directory.Exists(full)))
                {
                    // §1: local delete intent survives the mistake-proofing in the store's EnqueueAsync
                    // (an unresolved error used to swallow the DELETE, and the file came back on the next poll).
                    await MarkDeleteIntentAsync(rel, ct);
                    await EnqueueAsync(OperationType.Delete, rel, ct);
                }
                else if (type == WatcherChangeTypes.Renamed && oldPath is not null)
                {
                    await EnqueueAsync(OperationType.Move, PathRules.ToRelative(_mapping.LocalPath, oldPath), ct, rel);
                }
                else
                {
                    var isDirectory = Directory.Exists(full);
                    if (!isDirectory && !await WaitForStableFileAsync(full, ct))
                    {
                        // §3: still locked after ~1.5 s of quiet — retry shortly instead of failing the upload.
                        _activity($"Still being written; will retry: {rel}");
                        RescheduleLater(type, rel, oldPath);
                        return;
                    }
                    var info = isDirectory ? null : new FileInfo(full);
                    await _store.UpsertItemAsync(new(_mapping.MappingId, rel,
                        isDirectory ? ItemType.Directory : ItemType.File,
                        isDirectory ? 0 : info!.Length,
                        isDirectory ? Directory.GetLastWriteTimeUtc(full) : info!.LastWriteTimeUtc), ct);
                    await EnqueueAsync(isDirectory ? OperationType.CreateDirectory : OperationType.UpdateFile, rel, ct);
                }
                _activity($"{rel} · {type} · Waiting");
            }
            catch (OperationCanceledException) { }
            catch (IOException) { _activity($"File is busy; will retry: {rel}"); }
        }

        private async Task EnqueueAsync(OperationType type, string rel, CancellationToken ct, string? second = null)
        {
            await _store.EnqueueAsync(new(Guid.NewGuid(), _mapping.MappingId, type, rel, second,
                DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, OperationState.Pending, null), ct);
            // §9: a local change woke the watcher - the scheduler must not wait for the next remote poll.
            _wake?.Pulse();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _lifetime.Cancel();
            foreach (var cts in _debounce.Values) cts.Cancel();
            _debounce.Clear();
            _lifetime.Dispose();
        }
    }
}
