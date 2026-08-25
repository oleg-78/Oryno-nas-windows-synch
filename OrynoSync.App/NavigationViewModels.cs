using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OrynoSync.Core;

namespace OrynoSync.App;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Raise(name); }
}

public sealed class ActivityViewModel : ViewModelBase
{
    private string _status = "Checking Oryno NAS...", _connectionMessage = "Starting connection check.", _connectionTone = "Warning", _indexed = "0", _waiting = "0", _errors = "0", _lastSync = "No successful file sync yet", _queue = "0 changes waiting safely", _errorSummary = "No sync errors";
    private bool _paused, _hasErrors, _errorsExpanded;
    public string Status { get => _status; set => Set(ref _status, value); } public string ConnectionMessage { get => _connectionMessage; set => Set(ref _connectionMessage, value); } public string ConnectionTone { get => _connectionTone; set => Set(ref _connectionTone, value); } public string IndexedText { get => _indexed; set => Set(ref _indexed, value); } public string WaitingText { get => _waiting; set => Set(ref _waiting, value); } public string ErrorsText { get => _errors; set => Set(ref _errors, value); } public string LastSyncText { get => _lastSync; set => Set(ref _lastSync, value); } public string QueueText { get => _queue; set => Set(ref _queue, value); } public string ErrorSummary { get => _errorSummary; set => Set(ref _errorSummary, value); } public bool IsPaused { get => _paused; set => Set(ref _paused, value); } public bool HasErrors { get => _hasErrors; set => Set(ref _hasErrors, value); } public bool ErrorsExpanded { get => _errorsExpanded; set => Set(ref _errorsExpanded, value); }
    public ObservableCollection<string> Items { get; } = []; public ObservableCollection<SyncFileErrorRowViewModel> ErrorItems { get; } = [];
    public Action? PauseOrResume { get; set; } public Action? OpenFolder { get; set; }
    public void Add(string value) { Items.Insert(0, value); while (Items.Count > 20) Items.RemoveAt(Items.Count - 1); }
    public void ApplySummary(SyncDashboardSummary s) { IndexedText = s.IndexedFiles.ToString("N0"); WaitingText = s.WaitingCount.ToString("N0"); ErrorsText = s.ErrorCount.ToString("N0"); QueueText = s.WaitingCount == 0 ? "No local changes waiting" : $"{s.WaitingCount:N0} changes waiting safely"; LastSyncText = s.LastSuccessfulFileSync is null ? "No successful file sync yet" : s.LastSuccessfulFileSync.Value.LocalDateTime.ToString("g"); HasErrors = s.ErrorCount > 0; ErrorSummary = HasErrors ? $"{s.ErrorCount:N0} files need attention" : "No sync errors"; }
    public void SetErrors(IReadOnlyList<SyncFileError> errors) { var ids = errors.Select(x => x.OperationId).ToHashSet(); foreach (var row in ErrorItems.Where(x => !ids.Contains(x.OperationId)).ToArray()) ErrorItems.Remove(row); for (var i = 0; i < errors.Count; i++) { var old = ErrorItems.FirstOrDefault(x => x.OperationId == errors[i].OperationId); if (old is null) ErrorItems.Insert(i, new SyncFileErrorRowViewModel(errors[i])); else old.Apply(errors[i]); } }
}
public sealed class SyncFileErrorRowViewModel(SyncFileError error) : ViewModelBase { private SyncFileError _error = error; public Guid OperationId => _error.OperationId; public string RelativePath => _error.RelativePath; public string UserMessage => _error.UserMessage; public string AttemptsText => $"Attempts: {_error.AttemptCount:N0} · Last attempt: {_error.LastAttemptAt.LocalDateTime:g}"; public void Apply(SyncFileError error) { _error = error; Raise(nameof(RelativePath)); Raise(nameof(UserMessage)); Raise(nameof(AttemptsText)); } }

public sealed class FoldersViewModel : ViewModelBase
{
    private string _localFolder = "Not configured", _rootName = "Unavailable while server is offline", _localStatus = "Scanning...", _stats = "Files: -   Folders: -   Pending changes: -";
    public string LocalFolder { get => _localFolder; set => Set(ref _localFolder, value); } public string RootName { get => _rootName; set => Set(ref _rootName, value); } public string LocalStatus { get => _localStatus; set => Set(ref _localStatus, value); } public string Stats { get => _stats; set => Set(ref _stats, value); }
    public Action? ChangeFolder { get; set; } public Action? OpenFolder { get; set; } public Action? AddFolder { get; set; } public IReadOnlyList<SyncRootDto> Roots { get; private set; } = []; public ObservableCollection<MappingCardViewModel> Mappings { get; } = [];
    public void SetRoots(IReadOnlyList<SyncRootDto> roots, Guid? selected) { Roots = roots; RootName = selected is Guid id ? roots.FirstOrDefault(x => x.RootId == id)?.Name ?? "Selected root unavailable" : roots.Count == 0 ? "Unavailable while server is offline" : "Select a sync root"; Raise(nameof(Roots)); }
    public void SetMappings(IReadOnlyList<SyncMapping> mappings) { Mappings.Clear(); foreach (var mapping in mappings) Mappings.Add(new MappingCardViewModel(mapping)); }
    public void ApplyMapping(SyncMapping mapping, ScanProgress? progress = null) { var card = Mappings.FirstOrDefault(x => x.MappingId == mapping.MappingId); if (card is null) { Mappings.Add(new MappingCardViewModel(mapping)); card = Mappings[^1]; } card.Apply(mapping, progress); }
    public void ApplySummaries(IReadOnlyList<SyncMappingSummary> summaries) { foreach (var summary in summaries) Mappings.FirstOrDefault(x => x.MappingId == summary.MappingId)?.ApplySummary(summary); }
}

public sealed class MappingCardViewModel : ViewModelBase
{
    private SyncMapping _mapping; private string _status = "Offline", _pending = "0 changes waiting", _rootName = "NAS folder not selected", _indexed = "0 indexed", _errors = "0 errors", _lastSync = "Last sync: Never"; private bool _paused, _isScanning;
    public SyncMapping Mapping => _mapping; public Guid MappingId => _mapping.MappingId; public string LocalPath => _mapping.LocalPath; public string RootName { get => _rootName; set => Set(ref _rootName, value); } public string Status { get => _status; set => Set(ref _status, value); } public string PendingText { get => _pending; set => Set(ref _pending, value); } public string IndexedText { get => _indexed; set => Set(ref _indexed, value); } public string ErrorsText { get => _errors; set => Set(ref _errors, value); } public string LastSyncText { get => _lastSync; set => Set(ref _lastSync, value); } public bool IsPaused { get => _paused; set => Set(ref _paused, value); } public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public Action? Open { get; set; } public Action? Pause { get; set; } public Action? Remove { get; set; } public MappingCardViewModel(SyncMapping mapping) { _mapping = mapping; Apply(mapping); }
    public void Apply(SyncMapping mapping, ScanProgress? progress = null) { _mapping = mapping; Raise(nameof(Mapping)); Raise(nameof(LocalPath)); RootName = mapping.ServerRootName ?? "Choose an Oryno NAS folder"; var status = progress?.IsComplete == true && mapping.Status == MappingStatus.Scanning ? mapping.ServerRootId is null ? MappingStatus.ServerRootUnavailable : MappingStatus.Syncing : mapping.Status; Status = FriendlyStatus(status); IsScanning = mapping.Status == MappingStatus.Scanning || progress is not null && !progress.IsComplete; if (progress is not null) PendingText = progress.IsComplete ? $"{progress.FilesFound:N0} files indexed locally" : $"Scanning... {progress.FilesFound:N0} files found"; }
    public void ApplySummary(SyncMappingSummary s) { IndexedText = $"{s.IndexedFiles:N0} indexed"; PendingText = s.WaitingCount == 0 ? "No changes waiting" : $"{s.WaitingCount:N0} changes waiting"; ErrorsText = $"{s.ErrorCount:N0} errors"; LastSyncText = s.LastSuccessfulFileSync is null ? "Last sync: Never" : $"Last sync: {s.LastSuccessfulFileSync.Value.LocalDateTime:g}"; }
    private static string FriendlyStatus(MappingStatus status) => status switch { MappingStatus.Scanning => "Scanning...", MappingStatus.Syncing => "Syncing files...", MappingStatus.UpToDate => "Up to date", MappingStatus.WaitingForContentSupport => "Syncing files...", MappingStatus.ServerRootUnavailable => "NAS folder not selected", MappingStatus.LocalFolderUnavailable => "Folder unavailable", MappingStatus.Paused => "Paused", MappingStatus.AuthenticationRequired => "Sign in required", MappingStatus.Conflict => "Conflicts need attention", MappingStatus.Error => "Unable to sync this folder", MappingStatus.Offline => "Offline - local changes are safe", _ => "Waiting to sync" };
}

public sealed class SettingsViewModel : ViewModelBase
{
    private string _serverUrl = "https://oryno-nas.remo78.ru", _connectionStatus = "Disconnected", _connectionTone = "Error", _device = Environment.MachineName + " - Windows", _database = "-", _watcher = "Starting...", _queue = "0", _testResult = ""; private bool _startWithWindows = true, _hasCredential, _isTesting, _isConnecting;
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); } public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); } public string ConnectionTone { get => _connectionTone; set => Set(ref _connectionTone, value); } public bool StartWithWindows { get => _startWithWindows; set { Set(ref _startWithWindows, value); WindowsAutostart.Set(value); } } public string Device { get => _device; set => Set(ref _device, value); } public string DatabasePath { get => _database; set => Set(ref _database, value); } public string WatcherStatus { get => _watcher; set => Set(ref _watcher, value); } public string QueueLength { get => _queue; set => Set(ref _queue, value); } public bool HasCredential { get => _hasCredential; set => Set(ref _hasCredential, value); } public bool IsTesting { get => _isTesting; set => Set(ref _isTesting, value); } public bool IsConnecting { get => _isConnecting; set => Set(ref _isConnecting, value); } public string TestResult { get => _testResult; set => Set(ref _testResult, value); }
    public Action? Disconnect { get; set; } public Action? Reauthorize { get; set; } public Func<string, string, Task>? Connect { get; set; } public Func<string, Task>? TestConnection { get; set; }
}
