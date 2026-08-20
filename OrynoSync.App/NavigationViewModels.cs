using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OrynoSync.Core;

namespace OrynoSync.App;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ActivityViewModel : ViewModelBase
{
    private string _status = "Starting…";
    private string _queue = "0 local changes waiting";
    private bool _paused;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string QueueText { get => _queue; set => Set(ref _queue, value); }
    public bool IsPaused { get => _paused; set => Set(ref _paused, value); }
    public ObservableCollection<string> Items { get; } = [];
    public Action? PauseOrResume { get; set; }
    public Action? OpenFolder { get; set; }
    public void Add(string value)
    {
        Items.Insert(0, value);
        while (Items.Count > 1000) Items.RemoveAt(Items.Count - 1);
    }
}

public sealed class FoldersViewModel : ViewModelBase
{
    private string _localFolder = "Not configured";
    private string _rootName = "Unavailable while server is offline";
    private string _localStatus = "Scanning…";
    private string _stats = "Files: —   Folders: —   Pending changes: —";
    public string LocalFolder { get => _localFolder; set => Set(ref _localFolder, value); }
    public string RootName { get => _rootName; set => Set(ref _rootName, value); }
    public string LocalStatus { get => _localStatus; set => Set(ref _localStatus, value); }
    public string Stats { get => _stats; set => Set(ref _stats, value); }
    public Action? ChangeFolder { get; set; }
    public Action? OpenFolder { get; set; }
    public Action? AddFolder { get; set; }
    public IReadOnlyList<SyncRootDto> Roots { get; private set; } = [];
    public ObservableCollection<MappingCardViewModel> Mappings { get; } = [];
    public void SetRoots(IReadOnlyList<SyncRootDto> roots, Guid? selected)
    {
        Roots = roots;
        RootName = selected is Guid id ? roots.FirstOrDefault(x => x.RootId == id)?.Name ?? "Selected root unavailable" : roots.Count == 0 ? "Unavailable while server is offline" : "Select a sync root";
        Raise(nameof(Roots));
    }
    public void SetMappings(IReadOnlyList<SyncMapping> mappings)
    {
        Mappings.Clear();
        foreach (var mapping in mappings) Mappings.Add(new MappingCardViewModel(mapping));
    }
    public void ApplyMapping(SyncMapping mapping, ScanProgress? progress = null)
    {
        var card = Mappings.FirstOrDefault(x => x.MappingId == mapping.MappingId);
        if (card is null) { Mappings.Add(new MappingCardViewModel(mapping)); card = Mappings[^1]; }
        card.Apply(mapping, progress);
    }
}

public sealed class MappingCardViewModel : ViewModelBase
{
    private SyncMapping _mapping;
    private string _status = "Offline";
    private string _pending = "0 changes waiting";
    private string _rootName = "Oryno NAS root not configured";
    private bool _paused;
    private bool _isScanning;
    public SyncMapping Mapping => _mapping;
    public Guid MappingId => _mapping.MappingId;
    public string LocalPath => _mapping.LocalPath;
    public string RootName { get => _rootName; set => Set(ref _rootName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string PendingText { get => _pending; set => Set(ref _pending, value); }
    public bool IsPaused { get => _paused; set => Set(ref _paused, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public Action? Open { get; set; }
    public Action? Pause { get; set; }
    public Action? Remove { get; set; }
    public MappingCardViewModel(SyncMapping mapping) { _mapping = mapping; Apply(mapping); }
    public void Apply(SyncMapping mapping, ScanProgress? progress = null)
    {
        _mapping = mapping;
        Raise(nameof(Mapping));
        Raise(nameof(LocalPath));
        RootName = mapping.ServerRootName ?? "Choose an Oryno NAS folder";
        var effectiveStatus = progress?.IsComplete == true && mapping.Status == MappingStatus.Scanning
            ? mapping.ServerRootId is null ? MappingStatus.ServerRootUnavailable : MappingStatus.WaitingForContentSupport
            : mapping.Status;
        Status = FriendlyStatus(effectiveStatus);
        IsScanning = mapping.Status == MappingStatus.Scanning || (progress is not null && !progress.IsComplete);
        if (progress is not null)
        {
            var p = progress;
            PendingText = progress.IsComplete
                ? $"{p.FilesFound:N0} files found · {p.ChangesIndexed:N0} changes indexed"
                : $"Scanning local files…  {p.FilesFound:N0} files found · {p.ChangesIndexed:N0} changes indexed";
        }
    }
    private static string FriendlyStatus(MappingStatus status) => status switch
    {
        MappingStatus.Scanning => "Scanning…",
        MappingStatus.UpToDate => "Up to date",
        MappingStatus.WaitingForContentSupport => "Waiting for server content sync",
        MappingStatus.ServerRootUnavailable => "Choose a NAS folder",
        MappingStatus.LocalFolderUnavailable => "Folder unavailable",
        MappingStatus.Paused => "Paused",
        MappingStatus.AuthenticationRequired => "Sign in required",
        MappingStatus.Conflict => "Conflicts need attention",
        MappingStatus.Error => "Unable to sync this folder",
        MappingStatus.Offline => "Offline — local changes are safe",
        _ => "Waiting to sync"
    };
}

public sealed class SettingsViewModel : ViewModelBase
{
    private string _serverUrl = "https://oryno-nas.remo78.ru";
    private string _connectionStatus = "Offline";
    private bool _startWithWindows = true;
    private string _device = Environment.MachineName + " · Windows";
    private string _database = "—";
    private string _watcher = "Starting…";
    private string _queue = "0";
    private bool _hasCredential;
    private bool _isTesting;
    private bool _isConnecting;
    private string _testResult = "";
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }
    public bool StartWithWindows { get => _startWithWindows; set { Set(ref _startWithWindows, value); WindowsAutostart.Set(value); } }
    public string Device { get => _device; set => Set(ref _device, value); }
    public string DatabasePath { get => _database; set => Set(ref _database, value); }
    public string WatcherStatus { get => _watcher; set => Set(ref _watcher, value); }
    public string QueueLength { get => _queue; set => Set(ref _queue, value); }
    public bool HasCredential { get => _hasCredential; set => Set(ref _hasCredential, value); }
    public bool IsTesting { get => _isTesting; set => Set(ref _isTesting, value); }
    public bool IsConnecting { get => _isConnecting; set => Set(ref _isConnecting, value); }
    public string TestResult { get => _testResult; set => Set(ref _testResult, value); }
    public Action? Disconnect { get; set; }
    public Action? Reauthorize { get; set; }
    public Func<string, string, Task>? Connect { get; set; }
    public Func<string, Task>? TestConnection { get; set; }
}
