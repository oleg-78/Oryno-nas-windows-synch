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
    private ConnectionState? _lastConnectionState;
    /// <summary>§1/§2/§6: single writer for the user-visible status. Internal phases never reach it.</summary>
    private readonly SyncUserStatusPresenter _statusPresenter = new();
    /// <summary>§9: pulsed by the local watcher so queued work never waits for the remote poll.</summary>
    private readonly SyncWakeSignal _wake = new();
    /// <summary>§8: startup/reconnect/user Start/next-destination refresh the server immediately.</summary>
    private readonly RemotePollSchedule _pollSchedule = new();
    /// <summary>§10: drains the queue at once while work exists.</summary>
    private readonly QueueDrainer _queueDrainer = new();
    /// <summary>§13: files this client materialises must not be uploaded straight back.</summary>
    private readonly LocalMutationSuppression _suppression = new();
    private SyncDashboardSummary? _lastSummary;
    /// <summary>§11: true only while queued work is being executed, so a real local change can show "Syncing".</summary>
    private bool _draining;
    private string _lastVisibleStatus = "";

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
        _mappingRuntime = new SyncMappingRuntimeManager(_mappingStore) { Wake = _wake, Suppression = _suppression };
        // §1/§2/§6/§7: the status line has exactly one writer, and it only speaks when the visible text changes.
        _statusPresenter.Changed += OnUserStatusChanged;
        _mappingRuntime.Activity += (mapping, text) => { AddActivity($"{Path.GetFileName(mapping.LocalPath)} - {text}"); if (mapping.ServerRootId is null) DiagnosticsLogger.Write("FOLDER_BLOCKED", $"folder_id={mapping.MappingId} local_path={mapping.LocalPath} remote_root_id=none remote_path=none state=ServerRootUnavailable reason=NAS destination missing"); };
        _mappingRuntime.Progress += (mapping, progress) => Dispatcher.BeginInvoke(() => ApplyMappingProgress(mapping, progress));
        _credentials = new WindowsCredentialStore(Path.Combine(_appData, "Credentials"));
        _activityVm.StartSync = () => _ = GlobalStartSyncAsync();
        _activityVm.StopSync = () => _ = GlobalStopSync();
        _activityVm.OpenFolder = OpenFolder_Click;
        _foldersVm.AddFolder = AddFolder;
        _foldersVm.OpenFolder = OpenFolder_Click;
        _settingsVm.Connect = ConnectAsync;
        _settingsVm.TestConnection = TestConnectionAsync;
        _settingsVm.Disconnect = DisconnectAsync;
        _settingsVm.Reauthorize = ReauthorizeAsync;
        _settingsVm.DatabasePath = DatabasePath;
        _settingsVm.InitializeStartup();
        // Core-layer diagnostics (§4) go to the same support log as the HTTP trace.
        SyncDiagnostics.Sink = DiagnosticsLogger.Write;
        _settingsVm.SaveSetting = (key, value) => _ = _mappingStore.SaveSettingAsync(key, value);
        _settingsVm.Device = Environment.MachineName + " - Windows";
        _activityVm.RetryErrors = () => _ = RetryActiveErrorsAsync();
        _activityVm.RetryOperation = id => _ = RetryOperationAsync(id);
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
        // §1/§14: the persisted autostart choice wins over the install default.
        var startWithWindows = await _mappingStore.GetSettingAsync("start_with_windows");
        if (startWithWindows is not null) _settingsVm.ApplyStartupSetting(string.Equals(startWithWindows, "true", StringComparison.OrdinalIgnoreCase));
        DiagnosticsLogger.Write("AUTOSTART_EFFECTIVE", WindowsAutostart.Describe().Replace(Environment.NewLine, " | "));
        // §7/§8/§9/§11: classify the errors the queue carried over and clean dead rows before the UI counts them.
        await ReconcileErrorsAsync("app-start");
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
        // §5: the metadata phase is internal. It may record that real changes arrived and may wake the
        // queue, but it never writes a "Checking…"/"Metadata…" string into the status line.
        _metadata.Progress += progress =>
        {
            if (progress.Changes == 0) return;
            // §4/§22: the server really returned changes — record it for the support log and wake the queue
            // so any downloads it produced start now. The status follows the *work* (pending/active), never
            // the poll itself; the client's own uploads echo back through /changes and must not flash "Syncing".
            DiagnosticsLogger.Write("REMOTE_CHANGE", $"applied={progress.Changes} revision={progress.Revision}");
            _wake.Pulse();
        };
        _transfer = new MappingTransferCoordinator(_mappingStore, _remoteStore, _api, Path.Combine(_appData, "Transfers")) { Wake = _wake, Suppression = _suppression };
        _transfer.Activity += activity => Dispatcher.BeginInvoke(() => _activityVm.Add($"{activity.Timestamp.LocalDateTime:t}  {activity.RelativePath}  {activity.Action}"));
    }

    private HttpClient CreateHttpClient(Uri uri, Func<string, CancellationToken, Task<string?>> reader)
    {
        var handler = new BearerTokenHandler(ct => reader(CredentialAccount(uri), ct))
        {
            InnerHandler = new HttpDiagnosticsHandler(uri) { InnerHandler = new SocketsHttpHandler { UseProxy = SystemProxyUsable(), ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(10) } }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static bool? _systemProxyUsable;

    /// <summary>
    /// §4: системный прокси может быть ЗАДАН в HKCU, но не работать (после reboot прокси-клиент
    /// не стартует автоматически) — тогда все запросы к NAS падают с ConnectionError, хотя сеть и
    /// сервер доступны напрямую. Проверяем прокси один раз и используем его только если он живой.
    /// </summary>
    private static bool SystemProxyUsable()
    {
        if (_systemProxyUsable is bool cached) return cached;
        var usable = true;
        var detail = "none";
        try
        {
            var target = new Uri("https://oryno-nas.remo78.ru");
            var candidate = System.Net.Http.HttpClient.DefaultProxy?.GetProxy(target);
            if (candidate is not null && !string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            {
                detail = $"{candidate.Host}:{candidate.Port}";
                using var probe = new System.Net.Sockets.TcpClient();
                var connect = probe.ConnectAsync(candidate.Host, candidate.Port);
                usable = connect.Wait(TimeSpan.FromSeconds(1)) && probe.Connected;
            }
        }
        catch { usable = true; }
        _systemProxyUsable = usable;
        DiagnosticsLogger.Write("PROXY_STATE", $"system_proxy={detail} reachable={usable} use_proxy={usable} note={(usable ? "traffic via proxy" : "proxy ignored, direct connection to NAS")}");
        return usable;
    }

    /// <summary>
    /// §7/§8/§9/§10/§11/§18: connected idle mode polls remote metadata once a minute. A local change pulses
    /// the wake signal, so the queue is drained immediately instead of waiting for the poll; startup,
    /// reconnect and a user Start still refresh the server at once (RemotePollSchedule).
    /// </summary>
    private async Task RemoteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_paused) { await Delay(1000, ct); continue; }
            try
            {
                if (_api is null || _metadata is null) { await Delay(1000, ct); continue; }
                var wait = _pollSchedule.WaitBeforeNextPoll();
                if (wait > TimeSpan.Zero)
                {
                    // §9/§10: no local work yet - sleep the idle interval, but wake the instant the watcher or a
                    // user action asks for the queue.
                    if (await _wake.WaitAsync(wait, ct))
                    {
                        DiagnosticsLogger.Write("POLL_SKIP", $"reason=local_change pulses={_wake.PulseCount} idle_poll_s={(int)SyncCadence.RemotePollInterval.TotalSeconds} pending={_lastSummary?.WaitingCount ?? 0}");
                        await DrainQueueAsync("local-change", ct);
                        continue;
                    }
                    if (ct.IsCancellationRequested) break;
                }
                await PollRemoteOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SyncApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                DiagnosticsLogger.Write("CONNECTION_FAILURE", $"old_state={_lastConnectionState?.ToString() ?? "None"} new_state=AuthenticationExpired reason=HTTP unauthorized exception_type={ex.GetType().Name} http_status={(int)ex.Status} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} retry_delay_ms={BackoffMs()} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")}");
                SetConnectionState(ConnectionState.AuthenticationExpired, "Authorization expired. Local changes are safe.");
                _pollSchedule.RequestImmediate("retry-after-auth");
                await Delay(SyncCadence.ErrorRetryInterval, ct);
            }
            catch (SyncApiException ex)
            {
                DiagnosticsLogger.Write("SYNC_FAILURE", $"reason={SafeError(ex)} exception_type={ex.GetType().Name} http_status={(int)ex.Status} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")}");
                AddActivity($"Sync error - {SafeError(ex)}");
                ApplyUserStatus("api-error");
                _pollSchedule.RequestImmediate("retry-after-error");
                await Delay(SyncCadence.ErrorRetryInterval, ct);
            }
            catch (Exception ex)
            {
                var errorInfo = ErrorClassifier.Classify(ex);

                // NETWORK FAILURE → Reconnecting (existing behavior)
                if (errorInfo.Category == ErrorClassifier.Category.Network ||
                    errorInfo.Category == ErrorClassifier.Category.ServerTransport)
                {
                    _failures++;
                    DiagnosticsLogger.Write(errorInfo.LogType, $"old_state={_lastConnectionState?.ToString() ?? "None"} new_state={(_failures >= 3 ? ConnectionState.ServerUnavailable : ConnectionState.Reconnecting)} reason={errorInfo.UserMessage} exception_type={ex.GetType().Name} http_status={(ex as SyncApiException is { } api ? (int)api.Status : 0)} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())} retry={_failures} retry_delay_ms={BackoffMs()} network={DiagnosticsLogger.NetworkState()} auth={(_settingsVm.HasCredential ? "present" : "missing")} stack={ex.StackTrace}");
                    SetConnectionState(_failures >= 3 ? ConnectionState.ServerUnavailable : ConnectionState.Reconnecting,
                        _failures >= 3 ? "Oryno NAS is unavailable. Local changes are safe." : "Reconnecting to Oryno NAS...");
                    AddActivity($"Connection error - {errorInfo.UserMessage}");
                    await Delay(BackoffMs(), ct);
                    continue;
                }

                // REVISION REGRESSION → Connection stays Connected, re-inventory
                if (errorInfo.Category == ErrorClassifier.Category.RevisionRegression)
                {
                    DiagnosticsLogger.Write(errorInfo.LogType, $"reason={errorInfo.UserMessage} exception_type={ex.GetType().Name}");
                    AddActivity(errorInfo.UserMessage);
                    // Reset inventory and continue (connection stays Connected)
                    await Delay(1000, ct);
                    continue;
                }

                // SERVER SEMANTIC (4xx/5xx) → Connection stays Connected, Sync issues
                if (errorInfo.Category == ErrorClassifier.Category.ServerSemantic)
                {
                    DiagnosticsLogger.Write(errorInfo.LogType, $"reason={errorInfo.UserMessage} exception_type={ex.GetType().Name} http_status={(ex as SyncApiException is { } api ? (int)api.Status : 0)} endpoint={DiagnosticsLogger.SafeEndpoint(TryGetServerUri())}");
                    AddActivity($"Sync error - {errorInfo.UserMessage}");
                    ApplyUserStatus("server-semantic");
                    // Connection remains Connected — do not increment _failures
                    _pollSchedule.RequestImmediate("retry-after-error");
                    await Delay(SyncCadence.ErrorRetryInterval, ct);
                    continue;
                }

                // APPLICATION ERROR → Connection stays Connected, Sync issues
                // This is the critical fix: ArgumentException, InvalidOperationException, etc.
                // must NOT trigger Reconnecting state.
                DiagnosticsLogger.Write(errorInfo.LogType, $"reason={errorInfo.UserMessage} exception_type={ex.GetType().Name} message={ex.Message} stack={ex.StackTrace}");
                AddActivity($"Sync error - {errorInfo.UserMessage}");
                ApplyUserStatus("app-error");
                // Connection remains Connected — do not increment _failures
                _pollSchedule.RequestImmediate("retry-after-error");
                await Delay(SyncCadence.ErrorRetryInterval, ct);
            }
        }
    }

    /// <summary>
    /// §7/§11: one full remote pass — the only place that asks the server for metadata. Called on the idle
    /// cadence (a minute), or immediately after startup/reconnect/a user Start.
    /// </summary>
    private async Task PollRemoteOnceAsync(CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var connection = await _api!.TestConnectionAsync(ct);
        if (connection.Status != ConnectionStatus.Connected)
        {
            _failures++;
            SetConnection(connection, _failures);
            _pollSchedule.RequestImmediate("reconnect");   // §8: try again as soon as the backoff expires
            await Delay(BackoffMs(), ct);
            return;
        }
        _failures = 0;
        _connectionTracker.Observe(ConnectionStatus.Connected);
        SetConnectionState(ConnectionState.Connected, "Connected to Oryno NAS.");
        var roots = await _metadata!.GetRootsAsync(ct);
        await Dispatcher.InvokeAsync(() => PopulateRoots(roots));
        var mappings = await _mappingStore.GetMappingsAsync(ct);
        _mappings = mappings;
        foreach (var mapping in mappings.Where(x => x.Enabled && x.ServerRootId is not null))
        {
            await _metadata.RunCycleAsync(mapping.ServerRootId!.Value, ct);
            await RefreshRemoteAsync(mapping.ServerRootId.Value, ct);
        }
        if (_transfer is not null) await _transfer.ProcessAsync(mappings, ct);
        await UpdateMappingStatusesAsync(mappings, ct);
        await RefreshCountersAsync();
        ApplyUserStatus("poll-cycle");
        DiagnosticsLogger.Write("POLL_CYCLE", $"cycle_ms={started.ElapsedMilliseconds} mappings={mappings.Count} pending={_lastSummary?.WaitingCount ?? 0} errors={_lastSummary?.ErrorCount ?? 0} visible=\"{_lastVisibleStatus}\"");
    }

    /// <summary>
    /// §9/§10: local work path. No remote metadata request happens here — the watcher already told us what
    /// changed, so this only drains the queue (and keeps draining while work is left).
    /// </summary>
    private async Task DrainQueueAsync(string reason, CancellationToken ct)
    {
        // §11: the user sees "Syncing" while our own work runs - but only for as long as it runs.
        _draining = true;
        ApplyUserStatus(reason);
        var result = await _queueDrainer.DrainAsync(async token =>
        {
            var mappings = await _mappingStore.GetMappingsAsync(token);
            _mappings = mappings;
            if (_transfer is not null) await _transfer.ProcessAsync(mappings, token);
            await UpdateMappingStatusesAsync(mappings, token);
            var summary = await RefreshCountersAsync();
            return new QueueDrainResult(summary.WaitingCount, _transfer?.ActiveTransfers ?? 0, false);
        }, ct);
        _draining = false;
        DiagnosticsLogger.Write("QUEUE_DRAIN", $"reason={reason} passes={_queueDrainer.Passes} pending={result.PendingOperations} active={result.ActiveTransfers}");
        ApplyUserStatus(reason);
    }

    /// <summary>Mapping rows follow the live queue depth (unchanged behaviour, shared by both cadences).</summary>
    private async Task UpdateMappingStatusesAsync(IReadOnlyList<SyncMapping> mappings, CancellationToken ct)
    {
        foreach (var mapping in mappings.Where(x => x.Enabled && x.ServerRootId is not null))
        {
            var pending = await _mappingStore.PendingCountAsync(mapping.MappingId, ct);
            var current = await _mappingStore.GetMappingAsync(mapping.MappingId, ct);
            if (current is not null && current.Status is not MappingStatus.Paused and not MappingStatus.ReadyForPreflight and not MappingStatus.ReadyToSync and not MappingStatus.Stopped)
                await _mappingStore.UpdateMappingAsync(current with { Status = pending == 0 ? MappingStatus.UpToDate : MappingStatus.Syncing, LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        }
        _mappings = mappings;
    }

    /// <summary>
    /// §3/§4/§7: the user-visible status is derived from the live queue/connection/error state — never from
    /// the fact that a background cycle is running.
    /// </summary>
    private void ApplyUserStatus(string reason)
    {
        if (_lastSummary is null) return;
        var mappings = _mappings;
        _statusPresenter.Apply(new SyncStatusInput(
            _lastConnectionState ?? ConnectionState.Connecting,
            _lastSummary.WaitingCount,
            _transfer?.ActiveTransfers ?? 0,
            // A remote change only becomes visible work when it queued something to transfer; the queue
            // itself is the signal (pending/active/work-in-progress), so no separate "changes arrived" hint.
            RemoteChangesApplied: false,
            _lastSummary.ErrorCount,
            HasMappings: mappings.Count > 0,
            AllMappingsStopped: mappings.Count > 0 && mappings.All(x => !x.Enabled),
            Paused: _paused,
            WorkInProgress: _draining), reason);
    }

    /// <summary>§6: fires only on a real visible change, so the subtitle does not flicker and stays a no-op otherwise.</summary>
    private void OnUserStatusChanged(SyncUserStatus status, string text, string reason)
    {
        DiagnosticsLogger.Write("STATUS_VISIBLE", $"old_visible=\"{_lastVisibleStatus}\" new_visible=\"{text}\" state={status} reason={reason}");
        _lastVisibleStatus = text;
        Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(StatusText.Text, text, StringComparison.Ordinal)) StatusText.Text = text;
            if (!string.Equals(_activityVm.Status, text, StringComparison.Ordinal)) _activityVm.Status = text;
            UpdateTrayStatus();
        });
    }

    /// <summary>
    /// §8: a user action, a reconnect or a changed destination must not wait out the idle minute. The pulse
    /// also unblocks a loop that is already sleeping on the 60 s interval.
    /// </summary>
    private void WakeForImmediateRefresh(string reason)
    {
        _pollSchedule.RequestImmediate(reason);
        _wake.Pulse();
    }

    private int BackoffMs() => Math.Min(60000, (int)(2000 * Math.Pow(2, Math.Min(_failures, 5))));
    private static async Task Delay(int ms, CancellationToken ct) { try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { } }
    private static Task Delay(TimeSpan delay, CancellationToken ct) => Delay((int)delay.TotalMilliseconds, ct);

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
        });
        // §1/§5: the subtitle is owned by the status presenter (one writer, stable while idle).
        _lastSummary = summary;
        ApplyUserStatus("counters");
        return summary;
    }

    /// <summary>
    /// §7/§8/§9/§11: decide what each carried-over error really is right now, archive the ones that are
    /// already resolved or orphaned, re-arm the transient ones and clean dead queue rows. Writes the
    /// BEFORE/AFTER numbers to the support log and the activity feed.
    /// </summary>
    public async Task<string> ReconcileErrorsAsync(string reason)
    {
        var before = await _mappingStore.GetDashboardSummaryAsync();
        var beforeBreakdown = await _mappingStore.GetQueueBreakdownAsync();
        var mappings = await _mappingStore.GetMappingsAsync();
        var report = await ErrorReconcileRunner.ReconcileAsync(_mappingStore, _remoteStore, mappings, HashLocalAsync);
        var purged = await _mappingStore.PurgeStaleOperationsAsync();
        var after = await _mappingStore.GetDashboardSummaryAsync();
        var afterBreakdown = await _mappingStore.GetQueueBreakdownAsync();
        var duplicates = await _mappingStore.GetDuplicateLiveOperationsAsync();
        var line = $"reason={reason} {report.Describe(before.ErrorCount)} purged_stale_rows={purged} " +
                   $"waiting={beforeBreakdown.LiveCount}->{afterBreakdown.LiveCount} duplicate_live_groups={duplicates.Count} " +
                   $"last_successful_sync={after.LastSuccessfulFileSync?.ToString("O") ?? "none"}";
        DiagnosticsLogger.Write("ERROR_RECONCILE_SUMMARY", line);
        if (report.Total > 0 || purged > 0)
            AddActivity($"Errors reconciled ({reason}): active {before.ErrorCount} → {after.ErrorCount}, resolved/archived {report.Resolved + report.StaleOrphan}, stale queue rows removed {purged}.");
        return line;
    }

    private async Task<string?> HashLocalAsync(string path, CancellationToken ct)
    {
        try { return await new Blake3ContentHasher().ComputeAsync(path, ct); }
        catch (Exception) { return null; }
    }

    /// <summary>§18: manual retry for the files the user still sees as active problems.</summary>
    private async Task RetryActiveErrorsAsync()
    {
        var errors = await _mappingStore.GetErrorsAsync(500);
        var retryable = errors.Where(x => x.Lifecycle != ErrorLifecycle.Historical).ToArray();
        foreach (var error in retryable) await _mappingStore.ReArmOperationAsync(error.OperationId, "manual retry");
        AddActivity(retryable.Length == 0 ? "Nothing to retry." : $"Retry queued for {retryable.Length:N0} file(s).");
        await ReconcileErrorsAsync("manual-retry");
        await RefreshCountersAsync();
    }

    private async Task RetryOperationAsync(Guid operationId)
    {
        await _mappingStore.ReArmOperationAsync(operationId, "manual retry");
        AddActivity("Retry queued for the selected file.");
        await RefreshCountersAsync();
    }

    private void SetConnection(ConnectionResult result, int failures)
    {
        failures = _connectionTracker.ConsecutiveFailures;
        var state = _connectionTracker.Observe(result.Status);
        if (result.Status == ConnectionStatus.TlsError) state = ConnectionState.ProtocolError;
        if (result.Status == ConnectionStatus.ProtocolError) state = ConnectionState.ServerError;
        // §4: full diagnostic (endpoint, status, content-type, safe body sample, parse error, expected
        // schema) goes to the support log; §5 decides between "Server error" and "Connected + issue".
        DiagnosticsLogger.Write("CONNECTION_DIAGNOSTIC", ConnectionDiagnostics.DescribeLogLine(result, failures));
        var message = ConnectionDiagnostics.DescribeMessage(result, failures);
        SetConnectionState(state, message);
        SyncDiagnostics.Report("CONNECTION_LABEL", $"label={ConnectionDiagnostics.Label(result)} status={result.Status} server_error={ConnectionDiagnostics.IsServerError(result)}");
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
            // §2/§5: the primary status line is written by SyncUserStatusPresenter only - the connection
            // detail stays in the muted line under it.
            _activityVm.ConnectionMessage = message;
            _activityVm.ConnectionTone = tone;
            UpdateTrayStatus();
        });
        // §3/§4: offline/reconnecting reaches the user through the presenter (connection state is its input),
        // so no status string is written from here.
        if (state is ConnectionState.ServerUnavailable or ConnectionState.Reconnecting or ConnectionState.Disconnected or ConnectionState.AuthenticationExpired) ApplyUserStatus("connection");
    }

    private void ApplyMappingProgress(SyncMapping mapping, ScanProgress progress)
    {
        _foldersVm.ApplyMapping(mapping, progress);
        if (progress.IsComplete) _ = RefreshCountersAsync();
    }

    private string? _lastActivityText;
    private int _lastActivityRepeatCount;
    private DateTimeOffset _lastActivityFirstSeen;
    /// <summary>§0/§16: queue-level detail is internal bookkeeping, NOT a user sync event.
    /// "<file> · Changed · Waiting" was written on every watcher/scan event (499 of the 500 stored
    /// activity rows), which is exactly what the user read as an endless "Sync / Sync / Sync" list.
    /// Real completions (Uploaded/Downloaded/Deleted/Moved/Created/Conflict/Failed) and the explicit
    /// started/stopped notices are recorded by the coordinator and transfer events instead.</summary>
    private static bool IsInternalActivity(string text) =>
        // EndsWith("Waiting") keeps this independent of the middle-dot character used by the runtime.
        text.EndsWith("Waiting", StringComparison.OrdinalIgnoreCase)
        || text.Contains("· Waiting", StringComparison.OrdinalIgnoreCase)
        || text.Contains("· Changed ·", StringComparison.OrdinalIgnoreCase)
        || text.Contains("ambiguous paths", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Rebuild", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Skipped ", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("File is busy", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Watcher overflow", StringComparison.OrdinalIgnoreCase);

    private readonly HashSet<string> _internalActivityLogged = new(StringComparer.Ordinal);

    private void AddActivity(string text)
    {
        var now = DateTimeOffset.Now;
        if (IsInternalActivity(text))
        {
            // §0: no Activity row and no UI row — diagnostics only, once per unique message per run.
            if (_internalActivityLogged.Add(text)) DiagnosticsLogger.Write("SYNC_INTERNAL_ACTIVITY", text);
            return;
        }
        _ = _mappingStore.RecordActivityAsync(new SyncActivityEvent(Guid.NewGuid(), null, null, "Activity", "Indexed", now, null, text));

        // Deduplication: if same message repeats, show "Occurrences: N" instead of spamming UI
        if (_lastActivityText == text && (now - _lastActivityFirstSeen).TotalMinutes < 5)
        {
            _lastActivityRepeatCount++;
            // Update the last activity entry with repeat count instead of adding new one
            Dispatcher.BeginInvoke(() => {
                if (_activityVm.Items.Count > 0 && _activityVm.Items[0].Contains("Occurrences:"))
                    _activityVm.Items.RemoveAt(0);
                _activityVm.Items.Insert(0, $"{now:t}  {text}  (Occurrences: {_lastActivityRepeatCount})");
            });
            return;
        }
        _lastActivityText = text;
        _lastActivityRepeatCount = 1;
        _lastActivityFirstSeen = now;
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
                card.StopSync = () => _ = StopMappingAsync(card.Mapping);
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
        var current = await _mappingStore.GetMappingAsync(mapping.MappingId) ?? mapping;
        // §12: Start is idempotent — a second click must not build a second plan or a second engine.
        if (_mappingRuntime.ActiveMappings.Contains(current.MappingId) && current.Status is MappingStatus.Syncing or MappingStatus.Scanning)
        {
            AddActivity($"Sync already running for {current.LocalPath} — Start ignored.");
            await RefreshMappingsAsync();
            return;
        }
        // If inventory not built yet (ReadyForPreflight or Stopped without plan), rebuild first
        if (current.Status is MappingStatus.ReadyForPreflight or MappingStatus.Stopped)
        {
            if (_transfer is not null && current.ServerRootId is not null)
            {
                var result = await _transfer.RebuildAsync(current);
                current = await _mappingStore.GetMappingAsync(current.MappingId) ?? current;
                AddActivity($"Sync plan built: {result.NormalizedPendingCount} operations queued.");
            }
        }
        // Now start: persists desired state (enabled=true) and begins transfers (§2).
        await _mappingRuntime.StartSyncAsync(current);
        AddActivity($"Sync started for {current.LocalPath}: transfers beginning.");
        WakeForImmediateRefresh("user-start");
        await RefreshMappingsAsync();
    }

    private async Task StopMappingAsync(SyncMapping mapping)
    {
        // §2/§15: Stop persists the user's decision — after an app restart or a reboot this mapping
        // stays Stopped, and queued local changes are kept for the next Start.
        await _mappingRuntime.StopAsync(mapping);
        AddActivity($"Sync stopped for {mapping.LocalPath}. Local changes stay queued for the next Start.");
        await RefreshMappingsAsync();
    }

    private async Task GlobalStartSyncAsync()
    {
        foreach (var mapping in _mappings.Where(x => x.ServerRootId is not null))
            await StartMappingAsync(mapping);
        _activityVm.SyncRunning = true;
        WakeForImmediateRefresh("user-start");
    }

    private async Task GlobalStopSync()
    {
        foreach (var mapping in _mappings)
            await _mappingRuntime.StopAsync(mapping);
        _activityVm.SyncRunning = false;
        AddActivity("All sync stopped. Mappings stay stopped after a restart.");
        await RefreshMappingsAsync();
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
        WakeForImmediateRefresh("destination-changed");
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
        WakeForImmediateRefresh("folder-added");
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
        new(new BearerTokenHandler(_ => Task.FromResult<string?>(token)) { InnerHandler = new HttpDiagnosticsHandler(uri) { InnerHandler = new SocketsHttpHandler { UseProxy = SystemProxyUsable(), ConnectTimeout = TimeSpan.FromSeconds(8) } } })
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
        // §8: a fresh loop (startup, connect, resume) always polls the server immediately.
        _pollSchedule.RequestImmediate("loop-restart");
        _ = RemoteLoopAsync(_remoteCts.Token);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _activityVm.SyncRunning = !_paused;
        ApplyUserStatus(_paused ? "paused" : "resumed");
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
        _trayPause = new Forms.ToolStripMenuItem("Stop syncing", null, (_, _) => { _ = GlobalStopSync(); });
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
            // §2/§6: the tray mirrors the single user-visible status instead of inventing its own.
            ConnectionState.Connected => string.IsNullOrEmpty(_lastVisibleStatus) ? "Up to date" : _lastVisibleStatus,
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
