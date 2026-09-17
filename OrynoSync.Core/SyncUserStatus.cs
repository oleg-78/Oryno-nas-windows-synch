namespace OrynoSync.Core;

/// <summary>
/// §2 (UX contract): the ONLY sync states a user may ever see in the primary status line.
/// Internal engine phases — polling, metadata refresh, inventory, reconciliation, scanning — are
/// deliberately NOT part of this set: they belong in the support log, never in the UI.
/// </summary>
public enum SyncUserStatus
{
    Stopped,
    Syncing,
    UpToDate,
    SyncIssues,
    Reconnecting,
    Offline,
    ServerError
}

/// <summary>
/// §3/§4: everything the user-visible status is allowed to depend on. There is no engine-phase field on
/// purpose — a background poll that changed nothing cannot move the status, because the presenter never
/// sees the phase at all. <c>RemoteChangesApplied</c> means "real server changes were applied", never
/// "a poll started"; the app answers "Syncing" from the queue itself (pending/active/draining), so this
/// hint stays optional and is used by the presenter contract tests.
/// </summary>
public sealed record SyncStatusInput(
    ConnectionState Connection,
    int PendingOperations = 0,
    int ActiveTransfers = 0,
    bool RemoteChangesApplied = false,
    int ErrorCount = 0,
    bool HasMappings = true,
    bool AllMappingsStopped = false,
    bool Paused = false,
    // Set while the loop is actually executing queued work (a drain pass); never set by merely starting a poll.
    bool WorkInProgress = false);

/// <summary>
/// §1/§3/§6/§7/§8: single writer for the user-visible status. It resolves the input to one of the seven
/// user states with a fixed priority and raises <see cref="Changed"/> only when the visible text really
/// changes — a no-op background cycle produces zero notifications.
/// </summary>
public sealed class SyncUserStatusPresenter
{
    public const string UpToDateText = "Up to date";
    public const string SyncingText = "Syncing files...";
    public const string SyncIssuesText = "Sync issues need attention";
    public const string ReconnectingText = "Reconnecting to Oryno NAS...";
    public const string ConnectingText = "Connecting to Oryno NAS...";
    public const string OfflineText = "Offline - local changes are safe";
    public const string ServerErrorText = "Server error";
    public const string PausedText = "Sync paused";
    public const string StoppedText = "Sync stopped - changes stay queued";
    public const string NoFoldersText = "Add a local folder to start syncing.";

    private SyncUserStatus _status = SyncUserStatus.Reconnecting;
    private string _text = ConnectingText;
    private bool _hasValue;

    public SyncUserStatus Status => _status;
    public string Text => _text;
    public bool HasValue => _hasValue;

    /// <summary>§6: raised only when the visible state+text actually differ from the current one.</summary>
    public event Action<SyncUserStatus, string, string>? Changed;

    public bool Apply(SyncStatusInput input, string reason)
    {
        var resolved = Resolve(input);
        // §3/§5: an internal phase carries no user-visible information — keep whatever is on screen.
        if (resolved.KeepCurrent) return false;
        if (_hasValue && resolved.Status == _status && string.Equals(resolved.Text, _text, StringComparison.Ordinal)) return false;
        _status = resolved.Status;
        _text = resolved.Text;
        _hasValue = true;
        Changed?.Invoke(_status, _text, reason);
        return true;
    }

    /// <summary>
    /// §7 priority: server error &gt; offline/reconnecting &gt; sync issues &gt; user Stop &gt; syncing &gt; up to date.
    /// A user Stop (paused, or every mapping disabled) outranks "Syncing": nothing is moving, so the UI
    /// must not claim otherwise — that is also what keeps a user Stop from being silently reset.
    /// </summary>
    public static (SyncUserStatus Status, string Text, bool KeepCurrent) Resolve(SyncStatusInput i) => i.Connection switch
    {
        ConnectionState.ServerError or ConnectionState.ProtocolError => (SyncUserStatus.ServerError, ServerErrorText, false),
        ConnectionState.AuthenticationRequired => (SyncUserStatus.ServerError, "Sign in required", false),
        ConnectionState.AuthenticationExpired => (SyncUserStatus.ServerError, "Authorization expired - local changes are safe", false),
        ConnectionState.ServerUnavailable => (SyncUserStatus.Offline, OfflineText, false),
        ConnectionState.Reconnecting => (SyncUserStatus.Reconnecting, ReconnectingText, false),
        ConnectionState.Disconnected => (SyncUserStatus.Reconnecting, ConnectingText, false),
        // §5: "Checking"/"Connecting" are internal phases. They never become a user status.
        ConnectionState.Checking or ConnectionState.Connecting or ConnectionState.ServerReachable => (SyncUserStatus.Reconnecting, ConnectingText, true),
        _ => Idle(i)
    };

    private static (SyncUserStatus Status, string Text, bool KeepCurrent) Idle(SyncStatusInput i)
    {
        if (i.Paused) return (SyncUserStatus.Stopped, PausedText, false);
        if (i.ErrorCount > 0) return (SyncUserStatus.SyncIssues, SyncIssuesText, false);
        if (!i.HasMappings) return (SyncUserStatus.Stopped, NoFoldersText, false);
        if (i.AllMappingsStopped) return (SyncUserStatus.Stopped, StoppedText, false);
        if (i.PendingOperations > 0 || i.ActiveTransfers > 0 || i.RemoteChangesApplied || i.WorkInProgress) return (SyncUserStatus.Syncing, SyncingText, false);
        return (SyncUserStatus.UpToDate, UpToDateText, false);
    }
}
