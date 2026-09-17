using OrynoSync.Core;

namespace OrynoSync.Tests;

/// <summary>
/// Acceptance contract for the "no flicker" task: the primary status line must stay put during a background
/// poll, must never name an internal engine phase, and must raise exactly one change per real transition.
/// </summary>
public class UserStatusContractTests
{
    /// <summary>What an idle background poll looks like to the presenter: connected, no work, no errors.</summary>
    private static SyncStatusInput IdlePoll() => new(ConnectionState.Connected, 0, 0, false, 0, true, false, false);

    private static readonly string[] Forbidden = ["Checking", "Metadata", "Refreshing", "Scanning", "Polling", "Inventory", "Reconciling"];

    [Fact] public void BackgroundPollWithNoChangesKeepsUpToDate()
    {
        var p = new SyncUserStatusPresenter();
        Assert.True(p.Apply(IdlePoll(), "startup"));
        Assert.Equal(SyncUserStatus.UpToDate, p.Status);
        for (var cycle = 0; cycle < 12; cycle++) Assert.False(p.Apply(IdlePoll(), "poll-cycle"));
        Assert.Equal("Up to date", p.Text);
    }

    [Fact] public void MetadataRefreshDoesNotExposeMetadataStatus()
    {
        // The presenter has no phase input at all: a metadata refresh reaches it as "connected, nothing to do".
        var p = new SyncUserStatusPresenter();
        p.Apply(IdlePoll(), "startup");
        for (var refresh = 0; refresh < 20; refresh++)
        {
            Assert.False(p.Apply(new(ConnectionState.Connected, 0, 0, false, 0, true, false, false), "metadata-phase"));
            Assert.Equal("Up to date", p.Text);
        }
        Assert.DoesNotContain(Forbidden, w => p.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public void CheckingPhaseDoesNotExposeCheckingStatus()
    {
        var p = new SyncUserStatusPresenter();
        p.Apply(IdlePoll(), "startup");
        foreach (var internalPhase in new[] { ConnectionState.Checking, ConnectionState.Connecting, ConnectionState.ServerReachable })
        {
            Assert.False(p.Apply(new(internalPhase, 0, 0, false, 0, true, false, false), "internal-phase"));
            Assert.Equal("Up to date", p.Text);
        }
    }

    [Fact] public void WorkInProgressShowsSyncingWhileTheWorkRuns()
    {
        // §11: a local save that finishes in under two seconds must still be visible as "Syncing" while it runs.
        var p = new SyncUserStatusPresenter();
        p.Apply(IdlePoll(), "startup");
        Assert.True(p.Apply(new SyncStatusInput(ConnectionState.Connected, WorkInProgress: true), "drain-start"));
        Assert.Equal(SyncUserStatus.Syncing, p.Status);
        Assert.True(p.Apply(new SyncStatusInput(ConnectionState.Connected, WorkInProgress: false), "drain-done"));
        Assert.Equal(SyncUserStatus.UpToDate, p.Status);
        // ...and a background poll never sets it.
        Assert.False(p.Apply(IdlePoll(), "poll-cycle"));
    }

    [Fact] public void NoPropertyChangedOnSameVisibleStatus()
    {
        var p = new SyncUserStatusPresenter();
        var changes = new List<string>();
        p.Changed += (_, text, reason) => changes.Add($"{reason}:{text}");
        for (var i = 0; i < 25; i++) p.Apply(IdlePoll(), "poll-cycle");
        Assert.Single(changes);
        Assert.Equal("poll-cycle:Up to date", changes[0]);
    }

    [Fact] public void QueuedOperationMovesUpToDateToSyncing()
    {
        var queued = new SyncUserStatusPresenter();
        queued.Apply(IdlePoll(), "startup");
        Assert.True(queued.Apply(new(ConnectionState.Connected, 3, 0, false, 0, true, false, false), "local-change"));
        Assert.Equal(SyncUserStatus.Syncing, queued.Status);
        // A remote change that produced real work is the same signal, even before the queue row is written.
        var remote = new SyncUserStatusPresenter();
        remote.Apply(IdlePoll(), "startup");
        Assert.True(remote.Apply(new(ConnectionState.Connected, 0, 0, true, 0, true, false, false), "remote-change"));
        Assert.Equal(SyncUserStatus.Syncing, remote.Status);
        // ... and an in-flight transfer is work too.
        var active = new SyncUserStatusPresenter();
        active.Apply(IdlePoll(), "startup");
        Assert.True(active.Apply(new(ConnectionState.Connected, 0, 2, false, 0, true, false, false), "transfer"));
        Assert.Equal(SyncUserStatus.Syncing, active.Status);
    }

    [Fact] public void AfterQueueEmptyReturnsToUpToDate()
    {
        var p = new SyncUserStatusPresenter();
        p.Apply(new(ConnectionState.Connected, 2, 1, false, 0, true, false, false), "local-change");
        Assert.Equal(SyncUserStatus.Syncing, p.Status);
        Assert.True(p.Apply(new(ConnectionState.Connected, 0, 0, false, 0, true, false, false), "queue-drained"));
        Assert.Equal(SyncUserStatus.UpToDate, p.Status);
    }

    [Fact] public void ConnectionErrorOverridesUpToDate()
    {
        var p = new SyncUserStatusPresenter();
        p.Apply(IdlePoll(), "startup");
        Assert.True(p.Apply(new(ConnectionState.Reconnecting, 0, 0, false, 0, true, false, false), "connection"));
        Assert.Equal(SyncUserStatus.Reconnecting, p.Status);
        Assert.True(p.Apply(new(ConnectionState.ServerUnavailable, 0, 0, false, 0, true, false, false), "connection"));
        Assert.Equal(SyncUserStatus.Offline, p.Status);
        Assert.True(p.Apply(new(ConnectionState.ServerError, 0, 0, false, 0, true, false, false), "connection"));
        Assert.Equal(SyncUserStatus.ServerError, p.Status);
        Assert.True(p.Apply(IdlePoll(), "poll-cycle"));
        Assert.Equal(SyncUserStatus.UpToDate, p.Status);
    }

    [Fact] public void ConflictStillShowsSyncIssues()
    {
        var p = new SyncUserStatusPresenter();
        p.Apply(IdlePoll(), "startup");
        Assert.True(p.Apply(new(ConnectionState.Connected, 0, 0, false, 1, true, false, false), "poll-cycle"));
        Assert.Equal(SyncUserStatus.SyncIssues, p.Status);
        Assert.Equal("Sync issues need attention", p.Text);
    }

    [Fact] public void PriorityRanksServerErrorAboveOfflineAboveIssuesAboveSyncing()
    {
        SyncUserStatus Resolve(ConnectionState c, int errors, int pending) => SyncUserStatusPresenter.Resolve(new(c, pending, 0, false, errors, true, false, false)).Status;
        Assert.Equal(SyncUserStatus.ServerError, Resolve(ConnectionState.ServerError, 3, 5));
        Assert.Equal(SyncUserStatus.Offline, Resolve(ConnectionState.ServerUnavailable, 3, 5));
        Assert.Equal(SyncUserStatus.SyncIssues, Resolve(ConnectionState.Connected, 3, 5));
        Assert.Equal(SyncUserStatus.Syncing, Resolve(ConnectionState.Connected, 0, 5));
        Assert.Equal(SyncUserStatus.UpToDate, Resolve(ConnectionState.Connected, 0, 0));
    }

    [Fact] public void NoUserVisibleStatusEverNamesAnInternalPhase()
    {
        foreach (var connection in Enum.GetValues<ConnectionState>())
        foreach (var pending in new[] { 0, 4 })
        foreach (var errors in new[] { 0, 2 })
        foreach (var paused in new[] { false, true })
        foreach (var stopped in new[] { false, true })
        {
            var input = new SyncStatusInput(connection, pending, 0, false, errors, true, stopped, paused);
            var (status, text, keep) = SyncUserStatusPresenter.Resolve(input);
            Assert.DoesNotContain(Forbidden, w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (!keep) Assert.Contains(status, new[] { SyncUserStatus.Stopped, SyncUserStatus.Syncing, SyncUserStatus.UpToDate, SyncUserStatus.SyncIssues, SyncUserStatus.Reconnecting, SyncUserStatus.Offline, SyncUserStatus.ServerError });
        }
    }

    [Fact] public void UserStopKeepsTheStatusStopped()
    {
        // §16 of the earlier Stop semantics task: a mapping the user stopped must not look like it is syncing.
        var p = new SyncUserStatusPresenter();
        Assert.True(p.Apply(new(ConnectionState.Connected, 7, 0, false, 0, true, true, false), "counters"));
        Assert.Equal(SyncUserStatus.Stopped, p.Status);
        Assert.True(p.Apply(new(ConnectionState.Connected, 0, 0, false, 0, false, false, false), "counters"));
        Assert.Equal(SyncUserStatus.Stopped, p.Status);
        Assert.True(p.Apply(new(ConnectionState.Connected, 0, 0, false, 0, true, false, true), "paused"));
        Assert.Equal("Sync paused", p.Text);
    }
}
