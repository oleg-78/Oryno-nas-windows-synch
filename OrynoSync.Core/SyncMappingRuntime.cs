namespace OrynoSync.Core;

public sealed class SyncMappingRuntimeManager(ISyncMappingStore store)
{
    private readonly Dictionary<Guid, Runtime> _runtimes = [];
    public event Action<SyncMapping, string>? Activity;
    public IReadOnlyCollection<Guid> ActiveMappings => _runtimes.Keys.ToArray();

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        foreach (var mapping in await store.GetMappingsAsync(ct)) if (mapping.Enabled) await StartAsync(mapping, ct);
    }

    public async Task StartAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        Stop(mapping.MappingId);
        if (!Directory.Exists(mapping.LocalPath)) { await SetStatusAsync(mapping, MappingStatus.LocalFolderUnavailable, "Local folder unavailable"); return; }
        var runtime = new Runtime(mapping, store, text => Activity?.Invoke(mapping, text));
        _runtimes[mapping.MappingId] = runtime;
        await runtime.ScanAsync(ct);
        runtime.Start();
        await SetStatusAsync(mapping, MappingStatus.UpToDate, null);
    }

    public async Task SetPausedAsync(SyncMapping mapping, bool paused, CancellationToken ct = default)
    {
        if (paused) { if (_runtimes.TryGetValue(mapping.MappingId, out var runtime)) runtime.Paused = true; await SetStatusAsync(mapping, MappingStatus.Paused, null, ct); }
        else { if (_runtimes.TryGetValue(mapping.MappingId, out var runtime)) runtime.Paused = false; await SetStatusAsync(mapping, MappingStatus.UpToDate, null, ct); }
    }

    public void Stop(Guid mappingId) { if (_runtimes.Remove(mappingId, out var runtime)) runtime.Dispose(); }
    public void Dispose() { foreach (var id in _runtimes.Keys.ToArray()) Stop(id); }

    private async Task SetStatusAsync(SyncMapping mapping, MappingStatus status, string? error, CancellationToken ct = default)
    {
        var current = await store.GetMappingAsync(mapping.MappingId, ct) ?? mapping;
        await store.UpdateMappingAsync(current with { Status = status, LastError = error, UpdatedAt = DateTimeOffset.UtcNow }, ct);
    }

    private sealed class Runtime : IDisposable
    {
        private readonly SyncMapping _mapping; private readonly ISyncMappingStore _store; private readonly Action<string> _activity; private readonly FileSystemWatcher _watcher; private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);
        public bool Paused { get; set; }
        public Runtime(SyncMapping mapping, ISyncMappingStore store, Action<string> activity)
        {
            _mapping=mapping;_store=store;_activity=activity;_watcher=new FileSystemWatcher(mapping.LocalPath){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size,InternalBufferSize=65536};_watcher.Created+=On;_watcher.Changed+=On;_watcher.Deleted+=On;_watcher.Renamed+=OnRename;_watcher.Error+=OnError;
        }
        public async Task ScanAsync(CancellationToken ct)
        {
            if (!Directory.Exists(_mapping.LocalPath)) return;
            await _store.UpdateMappingAsync(_mapping with { Status=MappingStatus.Scanning, LastScanAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow },ct);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var oldItems=(await _store.GetItemsAsync(_mapping.MappingId,ct)).ToDictionary(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase);var ignores=new IgnoreRules();
            foreach(var path in Directory.EnumerateDirectories(_mapping.LocalPath,"*",SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(_mapping.LocalPath,"*",SearchOption.AllDirectories))){ct.ThrowIfCancellationRequested();var rel=PathRules.ToRelative(_mapping.LocalPath,path);if(ignores.IsIgnored(rel))continue;var isDir=Directory.Exists(path);var info=isDir?null:new FileInfo(path);seen.Add(rel);if(!oldItems.TryGetValue(rel,out var old)){await _store.UpsertItemAsync(new(_mapping.MappingId,rel,isDir?ItemType.Directory:ItemType.File,isDir?0:info!.Length,isDir?Directory.GetLastWriteTimeUtc(path):info!.LastWriteTimeUtc),ct);await EnqueueAsync(isDir?OperationType.CreateDirectory:OperationType.CreateFile,rel,ct);}else if(!isDir&&(old.Size!=info!.Length||old.Mtime!=info.LastWriteTimeUtc)){await _store.UpsertItemAsync(new(_mapping.MappingId,rel,ItemType.File,info.Length,info.LastWriteTimeUtc),ct);await EnqueueAsync(OperationType.UpdateFile,rel,ct);}}
            foreach(var missing in oldItems.Values.Where(x=>!seen.Contains(x.RelativePath))){await _store.RemoveItemAsync(_mapping.MappingId,missing.RelativePath,ct);await EnqueueAsync(OperationType.Delete,missing.RelativePath,ct);}
            await _store.UpdateMappingAsync(_mapping with { Status=MappingStatus.UpToDate, LastScanAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow },ct);
        }
        public void Start()=>_watcher.EnableRaisingEvents=true;
        private void On(object? s,FileSystemEventArgs e){if(Paused)return;Schedule(e.ChangeType,e.FullPath,null);}
        private void OnRename(object? s,RenamedEventArgs e){if(Paused)return;Schedule(WatcherChangeTypes.Renamed,e.FullPath,e.OldFullPath);}
        private void OnError(object? s,ErrorEventArgs e){if(e.GetException() is InternalBufferOverflowException)_activity("Watcher overflow; reconciliation required");}
        private void Schedule(WatcherChangeTypes type,string path,string? oldPath){var rel=PathRules.ToRelative(_mapping.LocalPath,path);if(new IgnoreRules().IsIgnored(rel))return;if(_debounce.Remove(rel,out var old))old.Cancel();var c=new CancellationTokenSource();_debounce[rel]=c;_=ApplyAsync(type,rel,oldPath,c.Token);}
        private async Task ApplyAsync(WatcherChangeTypes type,string rel,string? oldPath,CancellationToken ct){try{await Task.Delay(750,ct);_debounce.Remove(rel);var full=PathRules.ToAbsolute(_mapping.LocalPath,rel);if(type==WatcherChangeTypes.Deleted||(!File.Exists(full)&&!Directory.Exists(full))){await _store.RemoveItemAsync(_mapping.MappingId,rel,ct);await EnqueueAsync(OperationType.Delete,rel,ct);}else if(type==WatcherChangeTypes.Renamed&&oldPath is not null){await EnqueueAsync(OperationType.Move,PathRules.ToRelative(_mapping.LocalPath,oldPath),ct,rel);}else{var info=new FileInfo(full);var dir=Directory.Exists(full);await _store.UpsertItemAsync(new(_mapping.MappingId,rel,dir?ItemType.Directory:ItemType.File,dir?0:info.Length,dir?Directory.GetLastWriteTimeUtc(full):info.LastWriteTimeUtc),ct);await EnqueueAsync(dir?OperationType.CreateDirectory:OperationType.UpdateFile,rel,ct);}_activity($"{rel} · {type} · Waiting");}catch(OperationCanceledException){}}
        private async Task EnqueueAsync(OperationType type,string rel,CancellationToken ct,string? second=null){await _store.EnqueueAsync(new(Guid.NewGuid(),_mapping.MappingId,type,rel,second,DateTimeOffset.UtcNow,0,DateTimeOffset.UtcNow,OperationState.Pending,null),ct);}
        public void Dispose(){_watcher.Dispose();foreach(var c in _debounce.Values)c.Cancel();_debounce.Clear();}
    }
}
