using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using OrynoSync.App.Views;
using OrynoSync.Core;

namespace OrynoSync.App;

public partial class MainWindow : Window
{
    private readonly string _appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oryno Sync");
    private readonly ILocalStateStore _store;
    private readonly IRemoteStateStore _remoteStore;
    private readonly ICredentialStore _credentials;
    private readonly ISyncMappingStore _mappingStore;
    private readonly SyncMappingRuntimeManager _mappingRuntime;
    private readonly ActivityViewModel _activityVm = new();
    private readonly FoldersViewModel _foldersVm = new();
    private readonly SettingsViewModel _settingsVm = new();
    private CancellationTokenSource? _remoteCts;
    private HttpClient? _http;
    private OrynoNasSyncApi? _api;
    private MetadataSyncCoordinator? _metadata;
    private MappingTransferCoordinator? _transfer;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayStatus;
    private Forms.ToolStripMenuItem? _trayPause;
    private Forms.ToolStripMenuItem? _traySyncNow;
    private bool _paused;
    private bool _allowClose;
    private int _failures;
    private readonly ConnectionStateTracker _connectionTracker = new();
    private IReadOnlyList<SyncMapping> _mappings = [];
    private Guid? _selectedRootId;
    private AppPage _currentPage = AppPage.Activity;
    private readonly ActivityView _activityView;
    private readonly FoldersView _foldersView;
    private readonly SettingsView _settingsView;
    private string _lastConnectionVisual = "";
    private string _lastSyncVisual = "";
    private ConnectionState? _lastConnectionState;

    private string DatabasePath => Path.Combine(_appData, "oryno-sync.db");

    public MainWindow()
    {
        InitializeComponent();
        _activityView = new ActivityView { DataContext = _activityVm };
        _foldersView = new FoldersView { DataContext = _foldersVm };
        _settingsView = new SettingsView { DataContext = _settingsVm };
        _store = new SqliteLocalStateStore(DatabasePath);
        _remoteStore = new RemoteStateStore(DatabasePath);
        _mappingStore = new SqliteSyncMappingStore(DatabasePath);
        _mappingRuntime = new SyncMappingRuntimeManager(_mappingStore);
        _mappingRuntime.Activity += (mapping, text) => { AddActivity($"{Path.GetFileName(mapping.LocalPath)} - {text}"); if (mapping.ServerRootId is null) DiagnosticsLogger.Write("FOLDER_BLOCKED", $"folder_id={mapping.MappingId} local_path={mapping.LocalPath} remote_root_id=none remote_path=none state=ServerRootUnavailable reason=NAS destination missing"); };
        _mappingRuntime.Progress += (mapping, progress) => Dispatcher.BeginInvoke(() => ApplyMappingProgress(mapping, progress));
        _credentials = new WindowsCredentialStore(Path.Combine(_appData, "Credentials"));
        _activityVm.StartSync = () => _ = GlobalStartSyncAsync();
        _activityVm.StopSync = () => GlobalStopSync();
        _activityVm.OpenFolder = OpenFolder_Click;
        _foldersVm.AddFolder = AddFolder;
        _foldersVm.OpenFolder = OpenFolder_Click;
        _settingsVm.Connect = ConnectAsync;
        _settingsVm.TestConnection = TestConnectionAsync;
        _settingsVm.Disconnect = DisconnectAsync;
        _settingsVm.Reauthorize = ReauthorizeAsync;
        _settingsVm.DatabasePath = DatabasePath;
        _settingsVm.InitializeStartup();
        _settingsVm.Device = Environment.MachineName + " - Windows";
        Loaded += LoadedAsync;
        NavigateTo(AppPage.Activity);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { if (App.ActivateEvent.WaitOne(0)) ShowFromTray(); };
        timer.Start();
    }

    private async void LoadedAsync(object? sender, RoutedEventArgs e)
    {
        await _store.InitializeAsync();
        await _remoteStore.InitializeAsync();
        await _mappingStore.InitializeAsync();
        SetupTray();
        DeviceText.Text = _settingsVm.Device;
        var server = await _store.GetSettingAsync("server_url") ?? "https://oryno-nas.remo78.ru";
        _settingsVm.ServerUrl = server;
        if (Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
            _settingsVm.HasCredential = !string.IsNullOrWhiteSpace(await _credentials.ReadAsync(CredentialAccount(serverUri)));
        if (Guid.TryParse(await _store.GetSettingAsync("selected_root_id"), out var selectedRoot)) _selectedRootId = selectedRoot;
        var mappings = await _mappingStore.GetMappingsAsync();
        if (App.CleanupSampleMappings)
        {
            foreach (var sample in mappings.Where(x => x.LocalPath.Contains("OrynoSync-W11-", StringComparison.OrdinalIgnoreCase)).ToArray())
                await _mappingStore.RemoveMappingAsync(sample.MappingId);
            mappings = await _mappingStore.GetMappingsAsync();
        }
        if (App.SampleMappings && mappings.Count == 0)
        {
            foreach (var pair in new[] { ("Documents", "Documents"), ("Projects", "Projects"), ("Photos", "Photos") })
            {
                var local = Path.Combine(Path.GetTempPath(), "OrynoSync-W11-" + pair.Item1);
                Directory.CreateDirectory(local);
                var now = DateTimeOffset.UtcNow;
                var sample = new SyncMapping(Guid.NewGuid(), local, null, pair.Item2, true, now, now, null, "Sample", null, null, MappingStatus.UpToDate);
                await _mappingStore.AddMappingAsync(sample);
            }
            mappings = await _mappingStore.GetMappingsAsync();
        }
        await RefreshMappingsAsync(mappings);
        await _mappingRuntime.RestoreAsync();
        InitializeProductionClient();
        RestartRemoteLoop();
        await RefreshCountersAsync();
    }

    private string CredentialAccount(Uri uri) => $"oryno-sync:{uri.Scheme}://{uri.Host}:{uri.Port}";

    private void InitializeProductionClient()
    {
        _http?.Dispose();
        if (!TryServerUri(_settingsVm.ServerUrl, out var uri)) return;
        _http = CreateHttpClient(uri, _credentials.ReadAsync);
        _api = new OrynoNasSyncApi(_http, uri);
        _metadata = new MetadataSyncCoordinator(_api, _remoteStore);
        _metadata.Progress += progress => Dispatcher.BeginInvoke(() => SetSyncStatus(progress.State, progress.Message));
        _transfer = new MappingTransferCoordinator(_mappingStore, _remoteStore, _api, Path.Combine(_appData, "Transfers"));
        _transfer.Activity += activity => Dispatcher.BeginInvoke(() => _activityVm.Add($"{activity.Timestamp.LocalDateTime:t}  {activity.RelativePath}  {activity.Action}"));
    }

    private HttpClient CreateHttpClient(Uri uri, Func<string, CancellationToken, Task<string?>> reader)
    {
        var handler = new BearerTokenHandler(ct => reader(CredentialAccount(uri), ct))
        {
            InnerHandler = new HttpDiagnosticsHandler(uri) { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(10) } }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task RemoteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_paused) { await Delay(1000, ct); continue; }
            try
            {
                if (_api is null || _metadata is null) { await Delay(4000, ct); continue; }
                var connection = await _api.TestConnectionAsync(ct);
                if (connection.Status != ConnectionStatus.Connected)
                {
                    _failures++;
                    SetConnection(connection, _failures);
                    await Delay(BackoffMs(), ct);
                    continue;
                }
                _failures = 0;
                _connectionTracker.Observe(ConnectionStatus.Connected);
                SetConnectionState(ConnectionState.Connected, "Connected to Oryno NAS.");
                var roots = await _metadata.GetRootsAsync(ct);
                await Dispatcher.InvokeAsync(() => PopulateRoots(roots));
                var mappings = await _mappingStore.GetMappingsAsync(ct);
                foreach (var mapping in mappings.Where(x => x.Enabled && x.ServerRootId is not null))
                {
                    await _metadata.RunCycleAsync(mapping.ServerRootId!.Value, ct);
                    await RefreshRemoteAsync(mapping.ServerRootId.Value, ct);
                }
                if (_transfer is not null) await _transfer.ProcessAsync(mappings, ct);
                foreach (var mapping in mappings.Where(x => x.Enabled && x.ServerRootId is not null))
                {
                    var pending = await _mappingStore.PendingCountAsync(mapping.MappingId, ct);
                    var current = await _mappingStore.GetMappingAsync(mapping.MappingId, ct);
                    if (current is not null && current.Status is not MappingStatus.Paused and not MappingStatus.ReadyForPreflight and not MappingStatus.ReadyToSync and not MappingStatus.Stopped)
                        await _mappingStore.UpdateMappingAsync(current with { Status = pending == 0 ? MappingStatus.UpToDate : MappingStatus.Syncing, LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, ct);
                }
                var summary = await RefreshCountersAsync();
                // Determine sync status for display
                var anySyncing = mappings.Any(x => x.Status is MappingStatus.Syncing or MappingStatus.Scanning);
                var anyStopped = mappings.Any(x => x.Status is MappingStatus.Stopped or MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync);
                if (mappings.Count == 0) SetSyncStatus(EngineState.OnlineIdle, "Add a local folder to start syncing.");
                else if (summary.ErrorCount > 0) SetSyncStatus(EngineState.Error, "Sync issues need attention");
                else if (anySyncing && summary.WaitingCount > 0) SetSyncStatus(EngineState.Syncing, "Syncing files...");
                else if (anySyncing) SetSyncStatus(EngineState.Syncing, "Syncing files...");
                else if (anyStopped && summary.WaitingCount > 0) SetSyncStatus(EngineState.OnlineIdle, "Changes waiting");
                else if (summary.WaitingCount > 0) SetSyncStatus(EngineState.Syncing, "Changes waiting safely");
                else SetSyncStatus(EngineState.UpToDate, "Up to date");
                await Delay(4000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SyncApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                DiagnosticsLogger.Write("CONNECTION_FAILURE", $"old_state={_lastConnectionState?.ToString() ?? "None"} new_state=AuthenticationExpired reason=HTTP unauthorized exception_type={ex.GetType().Name} http_status={(int)ex.Status} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} retry_delay_ms={BackoffMs()} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")}");
                SetConnectionState(ConnectionState.AuthenticationExpired, "Authorization expired. Local changes are safe.");
                await Delay(BackoffMs(), ct);
            }
            catch (SyncApiException ex)
            {
                DiagnosticsLogger.Write("SYNC_FAILURE", $"reason={SafeError(ex)} exception_type={ex.GetType().Name} http_status={(int)ex.Status} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")}");
                SetSyncStatus(EngineState.Error, $"Sync issue: {ex.Code ?? "server rejected an operation"}");
                AddActivity($"Sync error - {SafeError(ex)}");
                await Delay(4000, ct);
            }
            catch (Exception ex)
            {
                _failures++;
                DiagnosticsLogger.Write("CONNECTION_FAILURE", $"old_state={_lastConnectionState?.ToString() ?? "None"} new_state={(_failures >= 3 ? ConnectionState.ServerUnavailable : ConnectionState.Reconnecting)} reason={SafeError(ex)} exception_type={ex.GetType().Name} http_status={(ex as SyncApiException is { } api ? (int)api.Status : 0)} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} retry_delay_ms={BackoffMs()} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")}");
                SetConnectionState(_failures >= 3 ? ConnectionState.ServerUnavailable : ConnectionState.Reconnecting,
                    _failures >= 3 ? "Oryno NAS is unavailable. Local changes are safe." : "Reconnecting to Oryno NAS...");
                AddActivity($"Connection error - {SafeError(ex)}");
                await Delay(BackoffMs(), ct);
            }
        }
    }

    private int BackoffMs() => Math.Min(60000, (int)(2000 * Math.Pow(2, Math.Min(_failures, 5))));
    private static async Task Delay(int ms, CancellationToken ct) { try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { } }

    private void PopulateRoots(IReadOnlyList<SyncRootDto> roots)
    {
        var enabled = roots.Where(x => x.Enabled).ToArray();
        _foldersVm.SetRoots(enabled, _selectedRootId);
        foreach (var card in _foldersVm.Mappings)
            if (card.Mapping.ServerRootId is Guid id)
                card.RootName = enabled.FirstOrDefault(x => x.RootId == id)?.Name ?? "Oryno NAS root unavailable";
    }

    private async Task RefreshRemoteAsync(Guid rootId, CancellationToken ct)
    {
        var state = await _remoteStore.GetRootStateAsync(rootId, ct);
        var count = await _remoteStore.CountRemoteItemsAsync(rootId, ct);
        var downloads = await _remoteStore.CountPlanningAsync(rootId, RemotePlanningState.NeedsDownload, ct);
        await Dispatcher.InvokeAsync(() => _foldersVm.Stats = $"Remote metadata: {count:N0} items - Waiting for content: {downloads:N0} - Revision: {state.LastRevision:N0}");
    }

    private async Task<SyncDashboardSummary> RefreshCountersAsync()
    {
        var summary = await _mappingStore.GetDashboardSummaryAsync();
        var mappingSummaries = await _mappingStore.GetMappingSummariesAsync();
        var errors = await _mappingStore.GetErrorsAsync(50);
        await Dispatcher.InvokeAsync(() =>
        {
            _activityVm.ApplySummary(summary);
            _activityVm.SetErrors(errors);
            _foldersVm.ApplySummaries(mappingSummaries);
            _foldersVm.Stats = $"{summary.IndexedFiles:N0} indexed · {summary.WaitingCount:N0} waiting · {summary.ErrorCount:N0} errors";
            _settingsVm.QueueLength = summary.WaitingCount.ToString("N0");
            SidebarSyncText.Text = summary.WaitingCount == 0 ? "Everything is up to date" : $"{summary.WaitingCount:N0} changes waiting safely";
            StatusText.Text = summary.ErrorCount > 0
                ? "Sync issues need attention"
                : summary.WaitingCount > 0
                    ? "Changes waiting safely"
                    : "Up to date";
            UpdateTrayStatus();
        });
        return summary;
    }

    private void SetConnection(ConnectionResult result, int failures)
    {
        var state = _connectionTracker.Observe(result.Status);
        if (result.Status == ConnectionStatus.TlsError) state = ConnectionState.ProtocolError;
        if (result.Status == ConnectionStatus.ProtocolError) state = ConnectionState.ServerError;
        failures = _connectionTracker.ConsecutiveFailures;
        var message = result.Status switch
        {
            ConnectionStatus.AuthenticationRequired => "Server is reachable. Authorization is required.",
            ConnectionStatus.AuthenticationRevoked => "Authorization expired. Local changes are safe.",
            ConnectionStatus.TlsError => "TLS certificate or handshake error.",
            ConnectionStatus.ProtocolError => "Server reached, but its response was invalid.",
            _ when failures < 3 => "Reconnecting to Oryno NAS...",
            _ => "Oryno NAS is unavailable. Local changes are safe."
        };
        SetConnectionState(state, message);
    }

    private void SetConnectionState(ConnectionState state, string message)
    {
        if (_lastConnectionState != state)
            DiagnosticsLogger.Write("CONNECTION_STATE", $"old_state={_lastConnectionState?.ToString() ?? "None"} new_state={state} reason={message} exception_type=none http_status=none endpoint={DiagnosticsLogger.SafeEndpoint(_api is null ? null : new Uri(_settingsVm.ServerUrl))} retry={_failures} retry_delay_ms={BackoffMs()} network={DiagnosticsLogger.NetworkState()} auth={( _settingsVm.HasCredential ? "present" : "missing")} sync_folder_id={_selectedRootId?.ToString() ?? "none"}");
        _lastConnectionState = state;
        var label = state switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Reconnecting => "Reconnecting...",
            ConnectionState.AuthenticationRequired => "Sign in required",
            ConnectionState.AuthenticationExpired => "Authorization expired",
            ConnectionState.ProtocolError or ConnectionState.ServerError => "Server error",
            ConnectionState.ServerUnavailable => "Server unavailable",
            ConnectionState.Checking => "Checking...",
            _ => "Disconnected"
        };
        var visual = $"{label}|{message}";
        if (visual == _lastConnectionVisual) return;
        _lastConnectionVisual = visual;
        var tone = state switch { ConnectionState.Connected or ConnectionState.ServerReachable => "Success", ConnectionState.Reconnecting or ConnectionState.Checking or ConnectionState.Connecting => "Warning", _ => "Error" };
        Dispatcher.BeginInvoke(() =>
        {
            SidebarStatus.Text = label;
            SidebarStatus.Foreground = tone == "Success" ? System.Windows.Media.Brushes.LightGreen : tone == "Warning" ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.Salmon;
            _settingsVm.ConnectionStatus = message;
            _settingsVm.ConnectionTone = tone;
            _activityVm.Status = label;
            _activityVm.ConnectionMessage = message;
            _activityVm.ConnectionTone = tone;
            UpdateTrayStatus();
        });
        if (state is ConnectionState.ServerUnavailable or ConnectionState.Reconnecting)
            SetSyncStatus(EngineState.Offline, "Offline - local changes are safe");
    }

    private void SetSyncStatus(EngineState state, string message)
    {
        var label = state switch
        {
            EngineState.OnlineIdle or EngineState.UpToDate => message,
            EngineState.InitialInventory or EngineState.Reconciling => "Updating metadata...",
            EngineState.SyncingMetadata or EngineState.Syncing => message,
            EngineState.Paused => "Sync paused",
            EngineState.AuthenticationRequired => "Waiting for authorization",
            EngineState.Offline => "Offline - local changes are safe",
            _ => message
        };
        if (label == _lastSyncVisual) return;
        _lastSyncVisual = label;
        Dispatcher.BeginInvoke(() => { StatusText.Text = label; });
    }

    private void ApplyMappingProgress(SyncMapping mapping, ScanProgress progress)
    {
        _foldersVm.ApplyMapping(mapping, progress);
        if (progress.IsComplete) _ = RefreshCountersAsync();
    }

    private void AddActivity(string text)
    {
        var now = DateTimeOffset.Now;
        _ = _mappingStore.RecordActivityAsync(new SyncActivityEvent(Guid.NewGuid(), null, null, "Activity", "Indexed", now, null, text));
        Dispatcher.BeginInvoke(() => _activityVm.Add($"{now:t}  {text}"));
    }
    private static string SafeError(Exception ex) => ex is SyncApiException api ? $"{api.Code} ({(int)api.Status})" : ex.GetType().Name;

    private async Task RefreshMappingsAsync(IReadOnlyList<SyncMapping>? mappings = null)
    {
        mappings ??= await _mappingStore.GetMappingsAsync();
        _mappings = mappings;
        await Dispatcher.InvokeAsync(() =>
        {
            _foldersVm.SetMappings(mappings);
            foreach (var card in _foldersVm.Mappings)
            {
                card.Open = () => OpenMapping(card.LocalPath);
                card.StartSync = () => _ = StartMappingAsync(card.Mapping);
                card.StopSync = () => StopMapping(card.Mapping);
                card.Rebuild = () => RebuildMapping(card.Mapping);
                card.Remove = () => RemoveMapping(card.Mapping);
                card.Repair = () => RepairMapping(card.Mapping);
                card.CreateRoot = () => CreateNasFolderForMapping(card.Mapping);
                card.RefreshRoots = () => _ = RefreshRootsAsync();
                card.ChooseDestination = () => ChooseDestinationAsync(card.Mapping);
            }
        });
    }

    private async Task<IReadOnlyList<SyncRootDto>> RefreshRootsAsync()
    {
        if (_metadata is null) return [];
        var roots = await _metadata.GetRootsAsync();
        await Dispatcher.InvokeAsync(() => PopulateRoots(roots));
        AddActivity($"NAS roots refreshed: {roots.Count:N0} available");
        return roots;
    }

    private async Task CreateNasFolderViaAsync(Guid rootId, Guid? parentItemId, string name)
    {
        if (_api is null) throw new InvalidOperationException("Sync API is not initialised.");
        var folder = await _api.CreateFolderAsync(rootId, parentItemId, name, Guid.NewGuid());
        await RefreshRootsAsync();
        AddActivity($"NAS folder \"{folder.Name}\" created");
    }

    private async Task<IReadOnlyList<RemoteItemDto>> LoadInventoryAsync(Guid rootId)
    {
        if (_api is null) throw new InvalidOperationException("Sync API is not initialised.");
        var all = new List<RemoteItemDto>();
        var cursor = (string?)null;
        do
        {
            var page = await _api.GetInventoryPageAsync(rootId, cursor, 1000);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return all;
    }

    private async void CreateNasFolderForMapping(SyncMapping mapping)
    {
        if (mapping.ServerRootId is null) { System.Windows.MessageBox.Show(this, "Select a NAS destination first.", "NAS destination required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dlg = new CreateNasFolderDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.FolderName ?? "").Trim(); if (name.Length == 0) return;
        try { await CreateNasFolderViaAsync(mapping.ServerRootId.Value, null, name); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, $"Unable to create the NAS folder.\n\n{ex.Message}", "Create NAS folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RepairMapping(SyncMapping mapping)
    {
        if (mapping.ServerRootId is not null) { await RefreshRootsAsync(); return; }
        await ChooseDestinationAsync(mapping);
    }

    private void OpenMapping(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private async void ToggleMappingPause(MappingCardViewModel card)
    {
        var current = await _mappingStore.GetMappingAsync(card.MappingId);
        if (current is null) return;
        // StartSync handles the transition from preflight → syncing.
        // Pause/Resume only applies when already syncing or paused.
        if (current.Status is MappingStatus.ReadyForPreflight or MappingStatus.ReadyToSync or MappingStatus.Stopped)
        {
            await StartMappingAsync(current);
            return;
        }
        await _mappingRuntime.SetPausedAsync(current, current.Status != MappingStatus.Paused);
    }

    private async Task StartMappingAsync(SyncMapping mapping)
    {
        // If inventory not built yet (ReadyForPreflight or Stopped without plan), rebuild first
        if (mapping.Status is MappingStatus.ReadyForPreflight or MappingStatus.Stopped)
        {
            if (_transfer is not null && mapping.ServerRootId is not null)
            {
                var result = await _transfer.RebuildAsync(mapping);
                mapping = await _mappingStore.GetMappingAsync(mapping.MappingId) ?? mapping;
                AddActivity($"Sync plan built: {result.NormalizedPendingCount} operations queued.");
            }
        }
        // Now start: set status to Syncing and start runtime
        await _mappingRuntime.StartSyncAsync(mapping);
        AddActivity($"Sync started for {mapping.LocalPath}: transfers beginning.");
        await RefreshMappingsAsync();
    }

    private void StopMapping(SyncMapping mapping)
    {
        // Stop = disable scheduler; keep watcher running to detect changes
        _mappingRuntime.Stop(mapping.MappingId);
        AddActivity($"Sync stopped for {mapping.LocalPath}.");
        _ = RefreshMappingsAsync();
    }

    private async Task GlobalStartSyncAsync()
    {
        foreach (var mapping in _mappings.Where(x => x.Enabled && x.ServerRootId is not null))
            await StartMappingAsync(mapping);
        _activityVm.SyncRunning = true;
    }

    private void GlobalStopSync()
    {
        foreach (var mapping in _mappings)
            _mappingRuntime.Stop(mapping.MappingId);
        _activityVm.SyncRunning = false;
        AddActivity("All sync stopped.");
        _ = RefreshMappingsAsync();
    }

    private async Task ChooseDestinationAsync(SyncMapping mapping)
    {
        // If currently syncing, stop first
        if (mapping.Status is MappingStatus.Syncing or MappingStatus.Scanning)
            _mappingRuntime.Stop(mapping.MappingId);

        var dialog = new AddFolderWindow(_foldersVm.Roots, RefreshRootsAsync, (rootId, parent, name) => _ = CreateNasFolderViaAsync(rootId, parent, name), LoadInventoryAsync, mapping.LocalPath, destinationOnly: true) { Owner = this, Title = "Choose NAS destination" };
        if (dialog.ShowDialog() != true || dialog.SelectedRoot is not SyncRootDto root) return;
        var current = await _mappingStore.GetMappingAsync(mapping.MappingId);
        if (current is null) return;
        var updated = current with { ServerRootId = root.RootId, ServerRootName = root.Name, ServerDestinationItemId = dialog.DestinationItemId, ServerDestinationRelativePath = dialog.DestinationRelativePath, LastServerRevision = null, InventoryState = "NotStarted", LastError = null, Status = MappingStatus.Stopped, UpdatedAt = DateTimeOffset.UtcNow };
        await _mappingStore.UpdateMappingAsync(updated);
        await RefreshMappingsAsync();
        AddActivity($"NAS destination changed to {root.Name} / {dialog.DestinationRelativePath}");
    }

    private async void RemoveMapping(SyncMapping mapping)
    {
        if (System.Windows.MessageBox.Show(this, $"Stop syncing {mapping.LocalPath}?\n\nFiles on Windows and Oryno NAS will not be deleted.",
            "Remove from Oryno Sync", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        _mappingRuntime.Stop(mapping.MappingId);
        if (_transfer is not null) await _transfer.RemoveMappingAsync(mapping);
        else await _mappingStore.RemoveMappingAsync(mapping.MappingId);
        await RefreshMappingsAsync();
    }

    private async void RebuildMapping(SyncMapping mapping)
    {
        if (_transfer is null || mapping.ServerRootId is null) { System.Windows.MessageBox.Show(this, "Select an Oryno NAS folder before rebuilding sync state.", "NAS folder required", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); return; }
        _mappingRuntime.Stop(mapping.MappingId);
        try
        {
            var result = await _transfer.RebuildAsync(mapping);
            await RefreshMappingsAsync();
            await _mappingRuntime.StartAsync(await _mappingStore.GetMappingAsync(mapping.MappingId) ?? mapping);
            AddActivity($"Sync state rebuilt: {result.OldPendingCount:N0} old changes → {result.NormalizedPendingCount:N0} current operations");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Unable to rebuild sync state", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); }
    }

    private async void AddFolder()
    {
        var dialog = new AddFolderWindow(_foldersVm.Roots, RefreshRootsAsync, (rootId, parent, name) => _ = CreateNasFolderViaAsync(rootId, parent, name), LoadInventoryAsync) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var path = SyncMappingRules.CanonicalLocalPath(dialog.LocalPath);
        var existing = await _mappingStore.GetMappingsAsync();
        if (existing.Any(x => SyncMappingRules.Overlaps(x.LocalPath, path)))
        {
            System.Windows.MessageBox.Show(this, "This folder overlaps an existing sync folder.", "Folder already covered", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (dialog.SelectedRoot is not null && existing.Any(x => x.ServerRootId == dialog.SelectedRoot.RootId))
        {
            var current = existing.First(x => x.ServerRootId == dialog.SelectedRoot.RootId);
            System.Windows.MessageBox.Show(this, $"This NAS folder is already linked.\n\nNAS root: {dialog.SelectedRoot.Name}\nExisting local path: {current.LocalPath}", "NAS folder already mapped", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var mapping = new SyncMapping(Guid.NewGuid(), dialog.LocalPath, dialog.SelectedRoot?.RootId, dialog.SelectedRoot?.Name, true,
            now, now, null, "NotStarted", null, null, dialog.SelectedRoot is null ? MappingStatus.ServerRootUnavailable : MappingStatus.Scanning,
            dialog.DestinationItemId, dialog.DestinationRelativePath);
        try { await _mappingStore.AddMappingAsync(mapping); }
        catch (MappingAlreadyLinkedException)
        {
            var current = existing.FirstOrDefault(x => x.ServerRootId == mapping.ServerRootId);
            System.Windows.MessageBox.Show(this, $"This NAS folder is already linked.\n\nNAS root: {mapping.ServerRootName}\nExisting local path: {current?.LocalPath ?? "another mapping"}", "NAS folder already mapped", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (mapping.ServerRootId is Guid selectedRoot)
        {
            _selectedRootId = selectedRoot;
            await _store.SaveSettingAsync("selected_root_id", selectedRoot.ToString());
        }
        await RefreshMappingsAsync();
        await _mappingRuntime.StartAsync(mapping);
        AddActivity($"Folder added - scan started in background: {mapping.LocalPath}");
    }

    private async Task ConnectAsync(string serverUrl, string token)
    {
        _settingsVm.IsConnecting = true;
        SetConnectionState(ConnectionState.Connecting, "Connecting to Oryno NAS...");
        try
        {
            if (!TryServerUri(serverUrl, out var uri)) { SetConnectionState(ConnectionState.ProtocolError, "Production server URL must use HTTPS."); return; }
            using var http = CreateTokenHttpClient(uri, token);
            var result = await new OrynoNasSyncApi(http, uri).TestConnectionAsync();
            if (result.Status != ConnectionStatus.Connected)
            {
                SetConnection(result, 3);
                _settingsVm.TestResult = result.Status == ConnectionStatus.AuthenticationRequired ? "Authorization failed. The device token is invalid or expired." : "Cannot connect to Oryno NAS.";
                return;
            }
            await _credentials.SaveAsync(CredentialAccount(uri), token);
            await _store.SaveSettingAsync("server_url", uri.GetLeftPart(UriPartial.Authority));
            _settingsVm.ServerUrl = uri.GetLeftPart(UriPartial.Authority);
            _settingsVm.HasCredential = true;
            InitializeProductionClient();
            RestartRemoteLoop();
            SetConnectionState(ConnectionState.Connected, "Connected to Oryno NAS.");
        }
        finally { _settingsVm.IsConnecting = false; }
    }

    private HttpClient CreateTokenHttpClient(Uri uri, string token) =>
        new(new BearerTokenHandler(_ => Task.FromResult<string?>(token)) { InnerHandler = new HttpDiagnosticsHandler(uri) { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) } } })
        { Timeout = TimeSpan.FromSeconds(20) };

    private Uri? TryGetServerUri() => Uri.TryCreate(_settingsVm.ServerUrl, UriKind.Absolute, out var uri) ? uri : null;

    private async Task TestConnectionAsync(string serverUrl)
    {
        _settingsVm.IsTesting = true;
        _settingsVm.TestResult = "Checking server...";
        try
        {
            if (!TryServerUri(serverUrl, out var uri)) { _settingsVm.TestResult = "Enter an HTTPS server address."; return; }
            using var http = CreateHttpClient(uri, _credentials.ReadAsync);
            var result = await new OrynoNasSyncApi(http, uri).TestConnectionAsync();
            _settingsVm.TestResult = result.Status switch
            {
                ConnectionStatus.Connected => "✓ Server is reachable and authorization is valid.",
                ConnectionStatus.AuthenticationRequired or ConnectionStatus.AuthenticationRevoked => "✓ Server is reachable. Authorization is required.",
                ConnectionStatus.TlsError => "TLS error. Check the server certificate.",
                _ => "Server is unavailable. Try again."
            };
        }
        finally { _settingsVm.IsTesting = false; }
    }

    private async void DisconnectAsync()
    {
        if (TryServerUri(_settingsVm.ServerUrl, out var uri)) await _credentials.DeleteAsync(CredentialAccount(uri));
        _settingsVm.HasCredential = false;
        _settingsVm.TestResult = "";
        SetConnectionState(ConnectionState.Disconnected, "Enter a device token to connect.");
        _remoteCts?.Cancel();
    }

    private async void ReauthorizeAsync()
    {
        if (TryServerUri(_settingsVm.ServerUrl, out var uri)) await _credentials.DeleteAsync(CredentialAccount(uri));
        _settingsVm.HasCredential = false;
        _settingsVm.TestResult = "Enter a new device authorization token.";
    }

    private bool TryServerUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps) { uri = parsed; return true; }
        uri = null!;
        return false;
    }

    private void RestartRemoteLoop()
    {
        _remoteCts?.Cancel();
        _remoteCts?.Dispose();
        _remoteCts = new CancellationTokenSource();
        _failures = 0;
        _ = RemoteLoopAsync(_remoteCts.Token);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _activityVm.SyncRunning = !_paused;
        SetSyncStatus(_paused ? EngineState.Paused : EngineState.Connecting, _paused ? "Sync paused" : "Resuming...");
        UpdateTrayStatus();
        if (_paused) _remoteCts?.Cancel();
        else RestartRemoteLoop();
    }

    private void OpenFolder_Click()
    {
        var path = _mappings.FirstOrDefault(x => Directory.Exists(x.LocalPath))?.LocalPath;
        if (path is not null) OpenMapping(path);
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Icon = LoadAppIcon(), Text = "Oryno Sync — Starting", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        _trayStatus = new Forms.ToolStripMenuItem("Oryno Sync — Starting") { Enabled = false };
        _traySyncNow = new Forms.ToolStripMenuItem("Start syncing", null, (_, _) => { if (!_paused) { _ = GlobalStartSyncAsync(); } });
        _trayPause = new Forms.ToolStripMenuItem("Stop syncing", null, (_, _) => { GlobalStopSync(); });
        menu.Items.Add(_trayStatus);
        menu.Items.Add("Open Oryno Sync", null, (_, _) => ShowFromTray());
        menu.Items.Add(_traySyncNow);
        menu.Items.Add(_trayPause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Oryno Sync", null, (_, _) => { _allowClose = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        UpdateTrayStatus();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("Assets/OrynoSync.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = new MemoryStream();
                resource.Stream.CopyTo(stream);
                stream.Position = 0;
                using var icon = new System.Drawing.Icon(stream);
                return (System.Drawing.Icon)icon.Clone();
            }
            return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); Topmost = true; Topmost = false; }

    private void UpdateTrayStatus()
    {
        if (_tray is null) return;
        var status = _paused ? "Paused" : _connectionTracker.State switch
        {
            ConnectionState.Connected when _activityVm.ErrorsText != "0" => "Errors",
            ConnectionState.Connected when _activityVm.WaitingText != "0" => "Syncing",
            ConnectionState.Connected => "Up to date",
            ConnectionState.AuthenticationRequired or ConnectionState.AuthenticationExpired or ConnectionState.ServerUnavailable => "Disconnected",
            _ => "Connecting"
        };
        var text = $"Oryno Sync — {status}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (_trayStatus is not null) _trayStatus.Text = text;
        if (_trayPause is not null) _trayPause.Text = _paused ? "Start syncing" : "Stop syncing";
        if (_traySyncNow is not null) _traySyncNow.Enabled = !_paused;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); return; }
        _remoteCts?.Cancel();
        _mappingRuntime.Dispose();
        _http?.Dispose();
        _tray?.Dispose();
        Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < 650;
        NavColumn.Width = narrow ? new GridLength(0) : new GridLength(190);
        Sidebar.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        CompactMenu.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactMenu_Click(object sender, RoutedEventArgs e) => NavigateTo(_currentPage);
    private void Activity_Click(object sender, RoutedEventArgs e) => NavigateTo(AppPage.Activity);
    private void Folders_Click(object sender, RoutedEventArgs e) => NavigateTo(AppPage.Folders);
    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateTo(AppPage.Settings);
    public void SelectPage(AppPage page) => NavigateTo(page);
    public void ShowAddFolderDialogForCapture() => new AddFolderWindow(_foldersVm.Roots) { Owner = this }.Show();

    private void NavigateTo(AppPage page)
    {
        _currentPage = page;
        PageTitle.Text = page.ToString();
        CurrentView.Content = page switch { AppPage.Activity => _activityView, AppPage.Folders => _foldersView, AppPage.Settings => _settingsView, _ => null };
        var selected = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 50, 56));
        ActivityNav.Background = page == AppPage.Activity ? selected : System.Windows.Media.Brushes.Transparent;
        FoldersNav.Background = page == AppPage.Folders ? selected : System.Windows.Media.Brushes.Transparent;
        SettingsNav.Background = page == AppPage.Settings ? selected : System.Windows.Media.Brushes.Transparent;
    }
}

public enum AppPage { Activity, Folders, Settings }
