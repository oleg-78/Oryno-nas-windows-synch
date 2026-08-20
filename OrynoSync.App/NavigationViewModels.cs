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
    public IReadOnlyList<SyncRootDto> Roots { get; private set; } = [];
    public void SetRoots(IReadOnlyList<SyncRootDto> roots, Guid? selected)
    {
        Roots = roots;
        RootName = selected is Guid id ? roots.FirstOrDefault(x => x.RootId == id)?.Name ?? "Selected root unavailable" : roots.Count == 0 ? "Unavailable while server is offline" : "Select a sync root";
        Raise(nameof(Roots));
    }
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
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }
    public bool StartWithWindows { get => _startWithWindows; set { Set(ref _startWithWindows, value); WindowsAutostart.Set(value); } }
    public string Device { get => _device; set => Set(ref _device, value); }
    public string DatabasePath { get => _database; set => Set(ref _database, value); }
    public string WatcherStatus { get => _watcher; set => Set(ref _watcher, value); }
    public string QueueLength { get => _queue; set => Set(ref _queue, value); }
    public Action<string, string>? Connect { get; set; }
    public Action<string>? TestConnection { get; set; }
}
