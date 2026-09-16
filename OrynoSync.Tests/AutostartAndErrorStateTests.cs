using OrynoSync.Core;

namespace OrynoSync.Tests;

/// <summary>
/// Regression tests for the Oryno Sync Windows task: autostart, persistent desired state,
/// server-error diagnostics and the error/queue lifecycle (§20).
/// </summary>
public class AutostartAndErrorStateTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"oryno-test-{Guid.NewGuid():N}.db");
    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oryno-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<SqliteSyncMappingStore> NewStoreAsync(string? path = null)
    {
        var store = new SqliteSyncMappingStore(path ?? TempDbPath());
        await store.InitializeAsync();
        return store;
    }

    private static SyncMapping Mapping(string localPath, Guid? rootId, bool enabled, MappingStatus status, DateTimeOffset? lastSync = null) =>
        new(Guid.NewGuid(), localPath, rootId, rootId is null ? null : "Oryno NAS", enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, "Normalized", null, null, status, null, null, lastSync);

    private static MappingPendingOperation Op(Guid mappingId, OperationType type, string path, OperationState state, string? error = null) =>
        new(Guid.NewGuid(), mappingId, type, path, null, DateTimeOffset.UtcNow, 1, DateTimeOffset.UtcNow, state, error);

    // ---------------------------------------------------------------- autostart (§1, §14)

    [Fact]
    public void AutostartCommandIsPerUserRunEntryWithoutElevation()
    {
        var exe = Path.Combine(@"C:\Users\remo7\OrynoSync\OrynoSync.Windows\OrynoSync.App\bin\Debug\net10.0-windows\OrynoSync.App.exe");
        var command = AutostartPolicy.BuildCommand(exe);
        Assert.StartsWith("\"C:\\Users\\remo7", command);
        Assert.EndsWith("--autostart", command);
        Assert.True(AutostartPolicy.TryParseExecutable(command, out var parsed));
        Assert.Equal(exe, parsed);
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Run", AutostartPolicy.RegistryKeyPath);
    }

    [Fact]
    public async Task AutostartPrefersPublishedBuildOverTemporaryBuildOutput()
    {
        var devBuild = Path.Combine(@"C:\Users\remo7\OrynoSync\OrynoSync.Windows\OrynoSync.App\bin\Debug\net10.0-windows\OrynoSync.App.exe");
        Assert.False(AutostartPolicy.IsTransientPath(devBuild));
        Assert.True(AutostartPolicy.IsTransientPath(Path.Combine(@"C:\Users\remo7\OrynoSync\OrynoSync.App\obj\Debug\app.exe")));
        Assert.True(AutostartPolicy.IsTransientPath(Path.Combine(Path.GetTempPath(), "stage", "OrynoSync.App.exe")));

        // A registered build path that no longer exists is not valid.
        Assert.False(AutostartPolicy.CommandMatches($"\"C:\\gone\\OrynoSync.App.exe\" --autostart", devBuild, publishedExists: false));

        // A published build that exists always wins over a dev build that also exists (§14).
        // published-путь лежит вне %TEMP% (иначе он сам считается transient и правило не проверяется).
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrynoSync", $"app-test-{Guid.NewGuid():N}");
        var published = Path.Combine(dir, "OrynoSync.App.exe");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(published, "");
        var dev = Path.Combine(TempFolder(), "dev", "OrynoSync.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dev)!);
        await File.WriteAllTextAsync(dev, "");
        Assert.Equal(published, AutostartPolicy.PreferredExecutable(published, dev));
        // Нет published-билда → используется текущий процесс (dev-путь), а не выдуманный путь.
        Assert.Equal(dev, AutostartPolicy.PreferredExecutable(Path.Combine(dir, "missing", "OrynoSync.App.exe"), dev));
        // Регистрация на dev-путь считается устаревшей, когда published уже есть.
        Assert.False(AutostartPolicy.CommandMatches($"\"{dev}\" --autostart", published, publishedExists: true));
        Assert.True(AutostartPolicy.CommandMatches($"\"{published}\" --autostart", published, publishedExists: true));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task AutostartSettingPersistsAcrossRestart()
    {
        var db = TempDbPath();
        var store = await NewStoreAsync(db);
        Assert.Null(await store.GetSettingAsync("start_with_windows"));
        await store.SaveSettingAsync("start_with_windows", "false");

        // Simulated restart: a brand new store instance on the same database.
        var reopened = await NewStoreAsync(db);
        Assert.Equal("false", await reopened.GetSettingAsync("start_with_windows"));
        await reopened.SaveSettingAsync("start_with_windows", "true");
        Assert.Equal("true", await store.GetSettingAsync("start_with_windows"));
    }

    // ---------------------------------------------------------------- desired state (§2, §12, §15)

    [Fact]
    public async Task RunningMappingResumesOnAppRestart()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), enabled: true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);

        using var runtime = new SyncMappingRuntimeManager(store);
        var resumed = await runtime.RestoreAsync();

        Assert.Contains(mapping.MappingId, resumed);
        Assert.Contains(mapping.MappingId, runtime.ActiveMappings);
        // Возобновлённый движок начинает с инвентаризации — Stopped/Error уже недопустимы (§2).
        var status = (await store.GetMappingAsync(mapping.MappingId))!.Status;
        Assert.True(status is MappingStatus.Scanning or MappingStatus.Syncing, $"unexpected status {status}");
    }

    [Fact]
    public async Task StoppedMappingStaysStoppedAfterRestart()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), enabled: false, MappingStatus.Stopped);
        await store.AddMappingAsync(mapping);

        using var runtime = new SyncMappingRuntimeManager(store);
        var resumed = await runtime.RestoreAsync();

        Assert.DoesNotContain(mapping.MappingId, resumed);
        Assert.DoesNotContain(mapping.MappingId, runtime.ActiveMappings);
        Assert.Equal(MappingStatus.Stopped, (await store.GetMappingAsync(mapping.MappingId))!.Status);
    }

    [Fact]
    public async Task StopPersistsAndStartIsIdempotentWithASingleEngine()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), enabled: true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);

        using var runtime = new SyncMappingRuntimeManager(store);
        await runtime.StartAsync(mapping);
        await runtime.StartAsync(mapping);
        await runtime.StartSyncAsync(mapping);
        await runtime.StartSyncAsync(mapping);

        Assert.Single(runtime.ActiveMappings);
        var running = await store.GetMappingAsync(mapping.MappingId);
        Assert.True(running!.Enabled);
        // Повторный Start не ломает состояние: движок живёт, первая фаза — инвентаризация.
        Assert.True(running.Status is MappingStatus.Scanning or MappingStatus.Syncing, $"unexpected status {running.Status}");

        await runtime.StopAsync(running);
        await runtime.StopAsync(running);
        var stopped = await store.GetMappingAsync(mapping.MappingId);
        Assert.False(stopped!.Enabled);
        Assert.Equal(MappingStatus.Stopped, stopped.Status);
        Assert.Empty(runtime.ActiveMappings);
    }

    [Fact]
    public async Task ReconnectProcessesEnabledMappingsAutomaticallyAndSkipsStoppedOnes()
    {
        var mappings = new MockMappingStore();
        var remote = new AuditMockRemoteStateStore();
        var api = new AuditMockSyncApi();
        var coordinator = new MappingTransferCoordinator(mappings, remote, api, Path.GetTempPath());
        var rootId = Guid.NewGuid();
        remote.AddRemoteItem(new RemoteItemState(Guid.NewGuid(), rootId, null, "Dir", "Dir", "directory", null, null, null, 0, 0, RemotePlanningState.MetadataOnly));

        var enabledMapping = Mapping(Path.Combine(@"C", "missing-enabled"), rootId, enabled: true, MappingStatus.Syncing);
        var stoppedMapping = Mapping(Path.Combine(@"C", "missing-stopped"), rootId, enabled: false, MappingStatus.Stopped);
        await mappings.AddMappingAsync(enabledMapping);
        await mappings.AddMappingAsync(stoppedMapping);
        await mappings.EnqueueAsync(Op(enabledMapping.MappingId, OperationType.CreateDirectory, "Dir", OperationState.Pending));
        await mappings.EnqueueAsync(Op(stoppedMapping.MappingId, OperationType.CreateDirectory, "Dir", OperationState.Pending));

        // The reconnect loop hands both mappings to the engine; only the enabled one may transfer.
        await coordinator.ProcessAsync([enabledMapping, stoppedMapping], CancellationToken.None);

        Assert.Empty(await mappings.GetPendingAsync(enabledMapping.MappingId, DateTimeOffset.MaxValue, CancellationToken.None));
        Assert.Single(await mappings.GetPendingAsync(stoppedMapping.MappingId, DateTimeOffset.MaxValue, CancellationToken.None));
    }

    // ---------------------------------------------------------------- error counter (§7, §8, §9, §11)

    [Fact]
    public async Task UploadsLandInsideTheConfiguredDestinationNotAtTheRoot()
    {
        var mappings = new MockMappingStore();
        var remote = new AuditMockRemoteStateStore();
        var api = new AuditMockSyncApi();
        var coordinator = new MappingTransferCoordinator(mappings, remote, api, Path.GetTempPath());
        var rootId = Guid.NewGuid();
        var mapping = Mapping(TempFolder(), rootId, enabled: true, MappingStatus.Syncing)
            with { ServerDestinationRelativePath = "Работа" };
        await mappings.AddMappingAsync(mapping);
        await mappings.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateDirectory, "Архив", OperationState.Pending));

        // В удалённом кэше НЕТ папки-приёмника (прод-случай: FindParent возвращал null → upload в корень).
        await coordinator.ProcessAsync([mapping], CancellationToken.None);

        // Папка-приёмник должна быть создана/привязана, а «Архив» — внутри неё, не в корне.
        var ensure = api.FolderCalls.FirstOrDefault(c => c.Name == "Работа");
        Assert.NotNull(ensure.Name);
        Assert.Null(ensure.ParentItemId);
        var inner = api.FolderCalls.FirstOrDefault(c => c.Name == "Архив");
        Assert.NotNull(inner.Name);
        Assert.NotNull(inner.ParentItemId);
    }

    [Fact]
    public async Task ConflictResolutionRequestsInventoryWithinServerLimits()
    {
        // Сервер принимает limit ≤ 2000; клиент запрашивал 5000 → HTTP 422, из-за чего
        // разрешение конфликта имени (409) не работало и операции уходили в FailedPermanent.
        var mappings = new MockMappingStore();
        var remote = new AuditMockRemoteStateStore();
        var api = new AuditMockSyncApi { SimulateNameConflict = true, ExistingItemId = Guid.NewGuid() };
        var coordinator = new MappingTransferCoordinator(mappings, remote, api, Path.GetTempPath());
        var rootId = Guid.NewGuid();
        var mapping = Mapping(TempFolder(), rootId, enabled: true, MappingStatus.Syncing);
        await mappings.AddMappingAsync(mapping);
        await mappings.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateDirectory, "Конфликт", OperationState.Pending));

        await coordinator.ProcessAsync([mapping], CancellationToken.None);

        Assert.NotEmpty(api.InventoryLimits);
        Assert.All(api.InventoryLimits, l => Assert.InRange(l, 1, 2000));
    }

    [Fact]
    public async Task StaleQueueRowsAreNotCountedAsWaiting()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);
        await store.UpsertItemAsync(new MappingLocalItem(mapping.MappingId, "a.txt", ItemType.File, 10, DateTimeOffset.UtcNow));

        for (var i = 0; i < 100; i++) await store.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateFile, $"dead-{i}.txt", OperationState.Cancelled));
        for (var i = 0; i < 50; i++) await store.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateFile, $"done-{i}.txt", OperationState.Completed));
        await store.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateFile, "live.txt", OperationState.Pending));

        var summary = await store.GetDashboardSummaryAsync();
        Assert.Equal(1, summary.WaitingCount);
        Assert.Equal(0, summary.ErrorCount);
    }

    [Fact]
    public async Task ResolvedErrorsLeaveTheActiveCounterAndLandInHistory()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);
        var failed = Op(mapping.MappingId, OperationType.CreateFile, "report.pdf", OperationState.Failed, "name exists: report.pdf");
        await store.EnqueueAsync(failed);

        Assert.Equal(1, (await store.GetDashboardSummaryAsync()).ErrorCount);

        await store.ArchiveOperationsAsync([new ArchivedError(failed.OperationId, mapping.MappingId, "report.pdf", "CreateFile", "Resolved", "server already has this item", "closed", DateTimeOffset.UtcNow)]);

        var after = await store.GetDashboardSummaryAsync();
        Assert.Equal(0, after.ErrorCount);
        Assert.Equal(1, after.HistoricalErrorCount);
        Assert.Empty(await store.GetErrorsAsync());
        var history = await store.GetErrorHistoryAsync();
        Assert.Single(history);
        Assert.Equal("Resolved", history[0].Outcome);
    }

    [Fact]
    public async Task SuccessfulRetryReArmsOldErrorAndOrphanIsArchived()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);
        var transient = Op(mapping.MappingId, OperationType.CreateFile, "retry-me.txt", OperationState.Failed, "Oryno NAS is temporarily unavailable (503)");
        var orphan = Op(mapping.MappingId, OperationType.CreateFile, "files\\gone.txt", OperationState.FailedPermanent, "unsupported");
        await store.EnqueueAsync(transient);
        await store.EnqueueAsync(orphan);

        await store.ReArmOperationAsync(transient.OperationId, "transient failure");
        await store.ArchiveOperationsAsync([new ArchivedError(orphan.OperationId, mapping.MappingId, "files\\gone.txt", "CreateFile", "StaleOrphan", "legacy destination prefix", "orphan", DateTimeOffset.UtcNow)]);

        var summary = await store.GetDashboardSummaryAsync();
        Assert.Equal(0, summary.ErrorCount);
        Assert.Equal(1, summary.WaitingCount);
        Assert.Equal(1, summary.HistoricalErrorCount);
    }

    [Fact]
    public async Task ReconcileClassifiesResolvedRetryingConflictAndOrphan()
    {
        var mappingId = Guid.NewGuid();
        ErrorReconcileResult Classify(bool local, string? localHash, bool remote, string? remoteHash, string? error, OperationType type = OperationType.CreateFile, bool remoteDir = false, string path = "a.txt")
        {
            var input = new ErrorReconcileInput(Guid.NewGuid(), mappingId, type, path, "Work\\" + path, error, 1,
                local, local ? 100 : null, localHash, remote, remoteHash, remote ? 100 : null, remoteDir, AutostartAndErrorStateTests_Legacy(path));
            return ErrorReconciler.Classify(input);
        }

        Assert.Equal(ErrorOutcome.Resolved, Classify(true, "HASH", true, "hash", null).Outcome);
        Assert.Equal(ErrorOutcome.Resolved, Classify(false, null, true, "hash", "name exists: a.txt").Outcome);
        Assert.Equal(ErrorOutcome.Resolved, Classify(true, null, true, null, "name exists: Dir", OperationType.CreateDirectory, remoteDir: true, path: "Dir").Outcome);
        Assert.Equal(ErrorOutcome.PermanentConflict, Classify(true, "AAA", true, "BBB", "conflict").Outcome);
        Assert.Equal(ErrorOutcome.StaleOrphan, Classify(false, null, false, null, "gone").Outcome);
        Assert.Equal(ErrorOutcome.StaleOrphan, Classify(true, null, true, null, "x", OperationType.CreateFile, false, "files\\legacy.txt").Outcome);
        Assert.Equal(ErrorOutcome.Retrying, Classify(true, null, false, null, "Oryno NAS is temporarily unavailable (503)").Outcome);
    }

    private static bool AutostartAndErrorStateTests_Legacy(string path) => ErrorReconciler.HasLegacyPrefix(path);

    [Fact]
    public void ItemFoundOutsideDestinationIsResolvedInsteadOfReuploaded()
    {
        // Live client data: folders that exist on the NAS only under the pre-fix root-level layout
        // must not be re-uploaded (that would duplicate gigabytes) — the error is closed instead.
        var input = new ErrorReconcileInput(Guid.NewGuid(), Guid.NewGuid(), OperationType.CreateDirectory,
            "\u041b\u0430\u043c\u0438\u043d\u0430\u0442\\76 Harm\\\u0412\u0442\u043e\u0440\u0430\u044f \u043e\u043f\u043b\u0430\u0442\u0430",
            "\u0420\u0430\u0431\u043e\u0442\u0430\\\u041b\u0430\u043c\u0438\u043d\u0430\u0442\\76 Harm\\\u0412\u0442\u043e\u0440\u0430\u044f \u043e\u043f\u043b\u0430\u0442\u0430",
            "name exists: \u0412\u0442\u043e\u0440\u0430\u044f \u043e\u043f\u043b\u0430\u0442\u0430", 1,
            true, null, null, true, null, null, true, false, RemoteAtLegacyPath: true);
        var result = ErrorReconciler.Classify(input);
        Assert.Equal(ErrorOutcome.Resolved, result.Outcome);
        Assert.Contains("legacy", result.Reason);
    }

    [Fact]
    public async Task OrphanErrorIsArchivedAndDeadQueueRowsPurged()
    {
        var store = await NewStoreAsync();
        var remote = new AuditMockRemoteStateStore();
        var rootId = Guid.NewGuid();
        var mapping = Mapping(TempFolder(), rootId, true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);

        var orphan = Op(mapping.MappingId, OperationType.CreateFile, "files\\legacy.txt", OperationState.Failed, "unsupported");
        await store.EnqueueAsync(orphan);
        for (var i = 0; i < 10; i++) await store.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateDirectory, $"cancelled-{i}", OperationState.Cancelled));

        var report = await ErrorReconcileRunner.ReconcileAsync(store, remote, [mapping], hashLocal: null);
        var purged = await store.PurgeStaleOperationsAsync();

        Assert.Equal(1, report.StaleOrphan);
        Assert.Equal(10, purged);
        var summary = await store.GetDashboardSummaryAsync();
        Assert.Equal(0, summary.ErrorCount);
        Assert.Equal(1, summary.HistoricalErrorCount);
    }

    [Fact]
    public async Task QueueDoesNotDuplicateOperationsAfterReindex()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);

        var items = new List<MappingLocalItem> { new(mapping.MappingId, "docs", ItemType.Directory, 0, DateTimeOffset.UtcNow) };
        for (var pass = 0; pass < 5; pass++)
        {
            var operations = new List<MappingPendingOperation> { Op(mapping.MappingId, OperationType.CreateDirectory, "docs", OperationState.Pending) };
            await store.IndexBatchAsync(items, operations);
        }

        var breakdown = await store.GetQueueBreakdownAsync();
        Assert.Equal(1, breakdown.LiveCount);
        Assert.Equal(0, breakdown.DuplicateGroups);
        Assert.Empty(await store.GetDuplicateLiveOperationsAsync());
    }

    [Fact]
    public async Task UnresolvedErrorPathIsNotReQueuedAsADuplicate()
    {
        var store = await NewStoreAsync();
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);
        await store.EnqueueAsync(Op(mapping.MappingId, OperationType.UpdateFile, "locked.pdf", OperationState.FailedPermanent, "name already exists: locked.pdf"));

        await store.EnqueueAsync(Op(mapping.MappingId, OperationType.CreateFile, "locked.pdf", OperationState.Pending));
        await store.IndexBatchAsync([new MappingLocalItem(mapping.MappingId, "locked.pdf", ItemType.File, 5, DateTimeOffset.UtcNow)],
            [Op(mapping.MappingId, OperationType.CreateFile, "locked.pdf", OperationState.Pending)]);

        var breakdown = await store.GetQueueBreakdownAsync();
        Assert.Equal(0, breakdown.LiveCount);
        Assert.Equal(1, breakdown.ByState["FailedPermanent"]);
    }

    [Fact]
    public async Task LastSuccessfulSyncSurvivesRestart()
    {
        var db = TempDbPath();
        var store = await NewStoreAsync(db);
        var lastSync = new DateTimeOffset(2026, 9, 15, 3, 30, 39, TimeSpan.Zero);
        var mapping = Mapping(TempFolder(), Guid.NewGuid(), true, MappingStatus.Syncing, lastSync);
        await store.AddMappingAsync(mapping);

        var reopened = await NewStoreAsync(db);
        var summary = await reopened.GetDashboardSummaryAsync();
        Assert.NotNull(summary.LastSuccessfulFileSync);
        Assert.Equal(lastSync, summary.LastSuccessfulFileSync!.Value.ToUniversalTime());
        Assert.DoesNotContain("No successful file sync", summary.LastSuccessfulFileSync.ToString()!);
    }

    // ---------------------------------------------------------------- protocol diagnostics (§4, §5, §6)

    [Fact]
    public void ProtocolErrorReportsEndpointStatusContentTypeBodyAndParseError()
    {
        var ex = new System.Text.Json.JsonException("The JSON value could not be converted to System.Int32. Path: $.protocol_version | LineNumber: 0 | BytePositionInLine: 53.");
        var issue = ProtocolDiagnostics.Describe("/api/sync/capabilities", "GET", 200, "application/json; charset=utf-8",
            "{\"protocol_version\":\"one\",\"capabilities\":[]}", ex, critical: true, expectedSchema: "CapabilitiesDto", protocolVersion: "Oryno Sync API v1 (S.2.1)");

        Assert.False(issue.IsCritical == false);
        Assert.Contains("endpoint=/api/sync/capabilities", issue.SafeLogLine);
        Assert.Contains("method=GET", issue.SafeLogLine);
        Assert.Contains("status=200", issue.SafeLogLine);
        Assert.Contains("content_type=application/json", issue.SafeLogLine);
        Assert.Contains("exception_type=System.Text.Json.JsonException", issue.SafeLogLine);
        Assert.Contains("json_parse_error=", issue.SafeLogLine);
        Assert.Contains("expected_schema=CapabilitiesDto", issue.SafeLogLine);
        Assert.Contains("$.protocol_version", issue.SafeLogLine);
        Assert.Contains("protocol_version=Oryno Sync API v1", issue.SafeLogLine);
        Assert.Contains("body_sample={\"protocol_version\":\"one\"", issue.SafeLogLine);
        Assert.Contains("Sync is paused", issue.UserMessage);
    }

    [Fact]
    public void DiagnosticsNeverLeakTokensOrCookies()
    {
        var body = "{\"detail\":\"unauthorized\",\"token\":\"crm_live_SUPERSECRET\",\"cookie\":\"session=abcdef\",\"password\":\"hunter2\"}";
        var issue = ProtocolDiagnostics.Describe("/api/sync/roots", "GET", 200, "application/json", body, new System.Text.Json.JsonException("bad"), true, "List<SyncRootDto>", "v1");
        Assert.DoesNotContain("SUPERSECRET", issue.SafeLogLine);
        Assert.DoesNotContain("abcdef", issue.SafeLogLine);
        Assert.DoesNotContain("hunter2", issue.SafeLogLine);
        Assert.Contains("<redacted>", issue.SafeLogLine);
    }

    [Fact]
    public void HealthyServerWithASecondaryIssueDoesNotShowServerError()
    {
        var issue = ProtocolDiagnostics.Describe("/api/sync/roots/7f1/items", "GET", 200, "text/html", "<html>gateway</html>", new System.Text.Json.JsonException("'<' is an invalid start of a value."), critical: false, "InventoryPageDto", "v1");
        var result = new ConnectionResult(ConnectionStatus.Connected, null, null, null, "580f2eb", [issue]);

        Assert.False(ConnectionDiagnostics.IsServerError(result));
        Assert.Equal("Connected · sync issues", ConnectionDiagnostics.Label(result));
        var message = ConnectionDiagnostics.DescribeMessage(result);
        Assert.Contains("sync issue at /api/sync/roots/7f1/items", message);
        Assert.DoesNotContain("Server error", message);
    }

    [Fact]
    public void MalformedCriticalEndpointShowsPreciseServerError()
    {
        var issue = ProtocolDiagnostics.Describe("/api/sync/capabilities", "GET", 200, "application/json", "{\"protocol_version\":1}", new System.Text.Json.JsonException("The JSON value could not be converted to IReadOnlyList<String>. Path: $.capabilities."), critical: true, "CapabilitiesDto", "v1");
        var result = new ConnectionResult(ConnectionStatus.ProtocolError, "PROTOCOL_ERROR", null, issue, "580f2eb", null);

        Assert.True(ConnectionDiagnostics.IsServerError(result));
        Assert.Equal("Server error", ConnectionDiagnostics.Label(result));
        var message = ConnectionDiagnostics.DescribeMessage(result);
        Assert.Contains("/api/sync/capabilities", message);
        Assert.Contains("HTTP 200", message);
        Assert.Contains("$.capabilities", message);
        Assert.Contains("Sync is paused", message);
        Assert.NotEqual(ConnectionDiagnostics.GenericProtocolMessage, message);
    }

    [Fact]
    public void MissingRequiredCommitFieldIsNamedInsteadOfGenericInvalidResponse()
    {
        // §5: commit без item_id — это конкретная protocol issue (с именем поля и endpoint),
        // а не общий «response was invalid»: иначе блокируется вся sync без причины.
        var issue = ProtocolDiagnostics.MissingField("uploads/7f1c/commit", "POST", "item_id",
            ProtocolEndpoints.IsCritical("uploads/7f1c/commit"));

        Assert.Equal("PROTOCOL_ISSUE", issue.Event);
        Assert.False(issue.IsCritical, "отказ одного commit не должен переводить клиент в Server error");
        Assert.Contains("item_id", issue.JsonError);
        Assert.Contains("uploads/7f1c/commit", issue.UserMessage);
        Assert.True(ProtocolEndpoints.IsCritical("roots"));
        Assert.True(ProtocolEndpoints.IsCritical("/api/sync/capabilities"));
    }

    [Fact]
    public void CommitReplayWithoutContentHashStillDeserializes()
    {
        // §6 live finding: /api/sync/uploads/{id}/commit omits content_hash on idempotent replays
        // (uploads.py returns only item_id/version/revision/replayed). With a required string property
        // System.Text.Json threw JsonException, so a retried commit looked like a corrupt response.
        var json = "{\"item_id\":\"6f1f6f2e-0d4a-4a5e-9f2b-1c2d3e4f5a6b\",\"version\":3,\"revision\":42,\"replayed\":true}";
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
        var parsed = System.Text.Json.JsonSerializer.Deserialize<UploadCommitResponse>(json, options);
        Assert.NotNull(parsed);
        Assert.True(parsed!.Replayed);
        Assert.Null(parsed.ContentHash);
        Assert.Equal(3, parsed.Version);
    }

    [Fact]
    public void EndpointCriticalityIsExplicit()
    {
        Assert.True(ProtocolEndpoints.IsCritical("/api/health"));
        Assert.True(ProtocolEndpoints.IsCritical("/api/sync/capabilities"));
        Assert.True(ProtocolEndpoints.IsCritical("roots"));
        Assert.False(ProtocolEndpoints.IsCritical("/api/sync/roots/7f1/items?limit=1000"));
        Assert.False(ProtocolEndpoints.IsCritical("/api/sync/roots/7f1/changes?after=10"));
        Assert.False(ProtocolEndpoints.IsCritical("/api/sync/items/123"));
    }

    [Fact]
    public void RebuiltServerRevisionIsAcceptedAsCompatible()
    {
        // §6: the contract gate is protocol_version + capabilities, not a pinned commit hash,
        // so a rebuilt server still connects and the revision is reported for support only.
        Assert.Equal("95ea1cffbe58b367c5cc1a4c1cdfad002489ddf7", OrynoNasSyncApi.KnownGoodServerCommit);
        Assert.Equal(OrynoNasSyncApi.KnownGoodServerCommit, OrynoNasSyncApi.ExpectedServerCommit);
        Assert.NotEqual("580f2eb8af20c7a8eabcbbcd90f8671db30d4db7", OrynoNasSyncApi.KnownGoodServerCommit);
        Assert.Equal("Oryno Sync API v1 (S.2.1)", OrynoNasSyncApi.ProtocolVersion);
    }

    // ------------------------------------------- §9: dead rows не оживают и не «пачкают» remote state

    [Fact]
    public async Task CancelledOperationsDoNotMarkRemoteItemsAsConflict()
    {
        var db = TempDbPath();
        var store = await NewStoreAsync(db);
        var rootId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var mapping = Mapping(TempFolder(), rootId, true, MappingStatus.Syncing);
        await store.AddMappingAsync(mapping);
        await store.ReplacePendingAsync(mapping.MappingId, new[]
        {
            Op(mapping.MappingId, OperationType.CreateFile, "cancel.txt", OperationState.Cancelled),
        });
        // Отменённая операция не ожидает: счётчик её не видит (§8).
        Assert.Equal(0, await store.PendingCountAsync(mapping.MappingId));

        var remote = new RemoteStateStore(db);
        await remote.InitializeAsync();
        await remote.BeginInventoryAsync(rootId, 1);   // без состояния root changes не применяются
        await remote.ApplyChangesPageAsync(rootId, new[]
        {
            new RemoteChangeDto(5, "UPDATE", itemId, 1, null, "cancel.txt", "cancel.txt", "file", 10, DateTimeOffset.UtcNow, null, null),
        }, 5);
        var afterCancelled = Assert.Single(await remote.GetRemoteItemsAsync(rootId));
        // Раньше «локально в работе» считалось всё, кроме Completed/FailedPermanent, поэтому
        // тысячи исторических Cancelled держали пути в конфликте и блокировали transfers (§9/§11).
        Assert.NotEqual(RemotePlanningState.PotentialConflict, afterCancelled.PlanningState);
        Assert.Equal(RemotePlanningState.NeedsDownload, afterCancelled.PlanningState);
        Assert.Equal(0, await remote.CountPlanningAsync(rootId, RemotePlanningState.PotentialConflict));

        // Живая операция на том же пути по-прежнему защищает локальные правки.
        await store.ReplacePendingAsync(mapping.MappingId, new[]
        {
            Op(mapping.MappingId, OperationType.CreateFile, "cancel.txt", OperationState.Pending),
        });
        await remote.ApplyChangesPageAsync(rootId, new[]
        {
            new RemoteChangeDto(6, "UPDATE", itemId, 2, null, "cancel.txt", "cancel.txt", "file", 11, DateTimeOffset.UtcNow, null, null),
        }, 6);
        var live = Assert.Single(await remote.GetRemoteItemsAsync(rootId));
        Assert.Equal(RemotePlanningState.PotentialConflict, live.PlanningState);
    }
}
