namespace OrynoSync.Core;

public sealed class SyncMappingRuntimeManager(ISyncMappingStore store)
{
    private readonly Dictionary<Guid, Runtime> _runtimes = [];

    public event Action<SyncMapping, string>? Activity;
    public event Action<SyncMapping, ScanProgress>? Progress;
    public IReadOnlyCollection<Guid> ActiveMappings => _runtimes.Keys.ToArray();

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        foreach (var mapping in await store.GetMappingsAsync(ct))
            if (mapping.Enabled) await StartAsync(mapping, ct);
    }

    // Starts the watcher and schedules reconciliation. It intentionally does not
    // await the scan: callers can close a dialog and keep using the app.
    public async Task StartAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        Stop(mapping.MappingId);
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
            progress => Progress?.Invoke(mapping, progress));
        _runtimes[mapping.MappingId] = runtime;
        if (mapping.Status != MappingStatus.ReadyForPreflight)
            await SetStatusAsync(mapping, MappingStatus.Scanning, null, ct);
        runtime.Start();
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
        private bool _disposed;

        public bool Paused { get; set; }

        public Runtime(SyncMapping mapping, ISyncMappingStore store, Action<string> activity, Action<ScanProgress> progress)
        {
            _mapping = mapping;
            _store = store;
            _activity = activity;
            _progress = progress;
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
            StartScan();
        }

        public void StartScan()
        {
            if (_disposed) return;
            _ = Task.Run(() => ScanAsync(_lifetime.Token), CancellationToken.None);
        }

        private async Task ScanAsync(CancellationToken ct)
        {
            if (!await _scanGate.WaitAsync(0, ct)) return;
            try
            {
                if (!Directory.Exists(_mapping.LocalPath))
                {
                    await UpdateStatusAsync(MappingStatus.LocalFolderUnavailable, "Local folder unavailable", ct);
                    return;
                }

                var oldItems = (await _store.GetItemsAsync(_mapping.MappingId, ct))
                    .ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ignores = new IgnoreRules();
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
                    await _store.RemoveItemAsync(_mapping.MappingId, missing.RelativePath, ct);
                    await EnqueueAsync(OperationType.Delete, missing.RelativePath, ct);
                    changes++;
                }

                var current = await _store.GetMappingAsync(_mapping.MappingId, ct) ?? _mapping;
                var finalStatus = _mapping.ServerRootId is null
                    ? MappingStatus.ServerRootUnavailable
                    : current.Status == MappingStatus.ReadyForPreflight
                        ? MappingStatus.ReadyForPreflight
                        : MappingStatus.Syncing;
                await UpdateStatusAsync(finalStatus, null, ct);
                _progress(new ScanProgress(files, folders, changes, true));
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
            await _store.IndexBatchAsync(_batchItems, _batchOperations, _lifetime.Token);
            _batchItems.Clear();
            _batchOperations.Clear();
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
                _activity("Watcher overflow; rescanning folder");
                StartScan();
            }
        }

        private void Schedule(WatcherChangeTypes type, string path, string? oldPath)
        {
            var rel = PathRules.ToRelative(_mapping.LocalPath, path);
            if (new IgnoreRules().IsIgnored(rel)) return;
            if (_debounce.Remove(rel, out var old)) old.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounce[rel] = cts;
            _ = ApplyAsync(type, rel, oldPath, cts.Token);
        }

        private async Task ApplyAsync(WatcherChangeTypes type, string rel, string? oldPath, CancellationToken ct)
        {
            try
            {
                await Task.Delay(750, ct);
                _debounce.Remove(rel);
                var full = PathRules.ToAbsolute(_mapping.LocalPath, rel);
                if (type == WatcherChangeTypes.Deleted || (!File.Exists(full) && !Directory.Exists(full)))
                {
                    await _store.RemoveItemAsync(_mapping.MappingId, rel, ct);
                    await EnqueueAsync(OperationType.Delete, rel, ct);
                }
                else if (type == WatcherChangeTypes.Renamed && oldPath is not null)
                {
                    await EnqueueAsync(OperationType.Move, PathRules.ToRelative(_mapping.LocalPath, oldPath), ct, rel);
                }
                else
                {
                    var isDirectory = Directory.Exists(full);
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
