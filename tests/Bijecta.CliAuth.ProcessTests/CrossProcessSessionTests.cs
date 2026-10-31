using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.ProcessTests;

// Each child process is a separate CLI run using CliAuthenticator over a real FileTokenStore (ADRs 0006–0008).
public sealed class CrossProcessSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionsWrittenOrRemovedByOneProcessAreVisibleToAnother()
    {
        using var location = new ProcessTestDirectory();
        await using var first = await SessionProcess.StartAsync(location.Path);
        await using var second = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await first.RunAsync("login alice"));
        Assert.Equal("STATUS Available alice", await second.RunAsync("status"));
        Assert.Equal("TOKEN synthetic-access-alice", await second.RunAsync("token"));
        Assert.Equal("LOGGED-OUT True", await second.RunAsync("logout"));
        Assert.Equal("STATUS None -", await first.RunAsync("status"));
        Assert.Null(await ReadRecordAsync(location));
        await first.ExitAsync();
        await second.ExitAsync();
    }

    [Fact]
    public async Task ConcurrentLoginsFromTheSameStateCommitExactlyOne()
    {
        using var location = new ProcessTestDirectory();
        var children = new List<SessionProcess>();
        try
        {
            // Every login reads the same signed-out state before any of them commits.
            for (var index = 0; index < 4; index++)
            {
                var child = await SessionProcess.StartAsync(location.Path);
                children.Add(child);
                Assert.Equal("ENTERED", await child.RunAsync($"login-pause user{index}"));
            }
            await Task.WhenAll(children.Select(child => child.SendAsync("resume")));
            var results = await Task.WhenAll(children.Select(child => child.ReadAsync()));

            var winner = Assert.Single(results, result => result.StartsWith("LOGGED-IN ", StringComparison.Ordinal));
            Assert.All(results.Where(result => result != winner), result => Assert.Equal("ERROR OperationSuperseded", result));
            var record = await ReadRecordAsync(location);
            Assert.Equal(winner["LOGGED-IN ".Length..], record!.Subject);
            Assert.Equal(0, record.Generation);
            Assert.Empty(location.StrayFiles());
            foreach (var child in children) { await child.ExitAsync(); }
        }
        finally
        {
            foreach (var child in children) { await child.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ALoginOverwrittenMeanwhileIsSupersededAndLaterLoginsBuildOnTheLatestRecord()
    {
        using var location = new ProcessTestDirectory();
        await using var first = await SessionProcess.StartAsync(location.Path);
        await using var slow = await SessionProcess.StartAsync(location.Path);
        await using var other = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await first.RunAsync("login alice"));
        Assert.Equal("ENTERED", await slow.RunAsync("login-pause bob"));
        Assert.Equal("LOGGED-IN carol", await other.RunAsync("login carol"));
        // The slow login started from alice's session; committing it would silently discard carol's.
        Assert.Equal("ERROR OperationSuperseded", await slow.RunAsync("resume"));
        Assert.Equal("STATUS Available carol", await first.RunAsync("status"));

        Assert.Equal("LOGGED-IN dave", await slow.RunAsync("login dave"));
        var record = await ReadRecordAsync(location);
        Assert.Equal("dave", record!.Subject);
        Assert.Equal(2, record.Generation);
        await first.ExitAsync();
        await slow.ExitAsync();
        await other.ExitAsync();
    }

    [Fact]
    public async Task LogoutInAnotherProcessSupersedesALoginStartedWhileSignedIn()
    {
        using var location = new ProcessTestDirectory();
        await using var login = await SessionProcess.StartAsync(location.Path);
        await using var logout = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await login.RunAsync("login alice"));
        Assert.Equal("ENTERED", await login.RunAsync("login-pause bob"));
        Assert.Equal("LOGGED-OUT True", await logout.RunAsync("logout"));
        Assert.Equal("ERROR OperationSuperseded", await login.RunAsync("resume"));
        Assert.Equal("STATUS None -", await logout.RunAsync("status"));
        Assert.Null(await ReadRecordAsync(location));
        await login.ExitAsync();
        await logout.ExitAsync();
    }

    [Fact]
    public async Task ALoginStartedWhileSignedOutCommitsDespiteAConcurrentLogout()
    {
        // ADR 0007's accepted trade-off: with no record there is no epoch for logout to change.
        using var location = new ProcessTestDirectory();
        await using var login = await SessionProcess.StartAsync(location.Path);
        await using var logout = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("ENTERED", await login.RunAsync("login-pause bob"));
        Assert.Equal("LOGGED-OUT True", await logout.RunAsync("logout"));
        Assert.Equal("LOGGED-IN bob", await login.RunAsync("resume"));
        Assert.Equal("STATUS Available bob", await logout.RunAsync("status"));
        await login.ExitAsync();
        await logout.ExitAsync();
    }

    [Fact]
    public async Task AProcessKilledBeforePublicationLeavesTheOldRecordAndReleasesItsLease()
    {
        using var location = new ProcessTestDirectory();
        await using var first = await SessionProcess.StartAsync(location.Path);
        Assert.Equal("LOGGED-IN alice", await first.RunAsync("login alice"));
        await first.ExitAsync();

        await using (var crashing = await SessionProcess.StartAsync(location.Path))
        {
            // Killed while holding the commit lease with bob's staging file written but not published.
            Assert.Equal("STAGED", await crashing.RunAsync("login-stage bob"));
            Assert.Single(location.StrayFiles());
            await crashing.KillAsync();
        }

        await using var next = await SessionProcess.StartAsync(location.Path);
        // Acquiring the lease proves the operating system released the killed process's lock.
        Assert.Equal("STATUS Available alice", await next.RunAsync("status"));
        Assert.Single(location.StrayFiles());
        // The next successful write of this key removes the orphaned staging file.
        Assert.Equal("LOGGED-IN carol", await next.RunAsync("login carol"));
        Assert.Empty(location.StrayFiles());
        var record = await ReadRecordAsync(location);
        Assert.Equal("carol", record!.Subject);
        Assert.Equal(1, record.Generation);
        await next.ExitAsync();
    }

    [Fact]
    public async Task TwoProcessesSharingAnExpiringSessionMakeOneRefresh()
    {
        using var location = new ProcessTestDirectory();
        await using var first = await SessionProcess.StartAsync(location.Path);
        await using var second = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await first.RunAsync("login-expiring alice"));
        Assert.Equal("STATUS RefreshRequired alice", await second.RunAsync("status"));
        // The first process holds the session lease through its refresh request (ADR 0009).
        Assert.Equal("ENTERED", await first.RunAsync("token-pause"));
        await second.SendAsync("token");
        await Task.Delay(200, Ct);
        var refreshed = await first.RunAsync("resume");
        Assert.StartsWith("TOKEN synthetic-refreshed-", refreshed, StringComparison.Ordinal);
        // The waiting process reloads the committed record instead of refreshing again.
        Assert.Equal(refreshed, await second.ReadAsync());

        var record = await ReadRecordAsync(location);
        Assert.Equal(1, record!.Generation);
        Assert.Equal("synthetic-rotated-" + refreshed["TOKEN synthetic-refreshed-".Length..], record.RefreshToken);
        Assert.Empty(location.StrayFiles());
        await first.ExitAsync();
        await second.ExitAsync();
    }

    [Fact]
    public async Task AProcessKilledMidRefreshLeavesTheSessionForTheNextRunToRefresh()
    {
        using var location = new ProcessTestDirectory();
        await using (var login = await SessionProcess.StartAsync(location.Path))
        {
            Assert.Equal("LOGGED-IN alice", await login.RunAsync("login-expiring alice"));
            await login.ExitAsync();
        }

        await using (var crashing = await SessionProcess.StartAsync(location.Path))
        {
            Assert.Equal("ENTERED", await crashing.RunAsync("token-pause"));
            await crashing.KillAsync();
        }

        // No in-progress marker was written: the session and its refresh token are untouched.
        var survivor = await ReadRecordAsync(location);
        Assert.Equal(SessionLifecycleState.Active, survivor!.State);
        Assert.Equal(0, survivor.Generation);
        Assert.Equal("synthetic-refresh-alice", survivor.RefreshToken);

        await using var next = await SessionProcess.StartAsync(location.Path);
        Assert.StartsWith("TOKEN synthetic-refreshed-", await next.RunAsync("token"), StringComparison.Ordinal);
        Assert.Equal(1, (await ReadRecordAsync(location))!.Generation);
        await next.ExitAsync();
    }

    [Fact]
    public async Task LogoutInAnotherProcessWaitsForARunningRefreshAndStillWins()
    {
        using var location = new ProcessTestDirectory();
        await using var refresh = await SessionProcess.StartAsync(location.Path);
        await using var logout = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await refresh.RunAsync("login-expiring alice"));
        Assert.Equal("ENTERED", await refresh.RunAsync("token-pause"));
        await logout.SendAsync("logout");
        await Task.Delay(200, Ct);
        Assert.StartsWith("TOKEN synthetic-refreshed-", await refresh.RunAsync("resume"), StringComparison.Ordinal);
        Assert.Equal("LOGGED-OUT True", await logout.ReadAsync());
        // The refresh committed first, so it cannot resurrect the session afterwards.
        Assert.Equal("STATUS None -", await refresh.RunAsync("status"));
        Assert.Null(await ReadRecordAsync(location));
        await refresh.ExitAsync();
        await logout.ExitAsync();
    }

    [Fact]
    public async Task ALoginInAnotherProcessDuringARefreshReplacesTheRefreshedSession()
    {
        using var location = new ProcessTestDirectory();
        await using var refresh = await SessionProcess.StartAsync(location.Path);
        await using var login = await SessionProcess.StartAsync(location.Path);

        Assert.Equal("LOGGED-IN alice", await refresh.RunAsync("login-expiring alice"));
        Assert.Equal("ENTERED", await refresh.RunAsync("token-pause"));
        await login.SendAsync("login carol");
        await Task.Delay(200, Ct);
        Assert.StartsWith("TOKEN synthetic-refreshed-", await refresh.RunAsync("resume"), StringComparison.Ordinal);
        Assert.Equal("LOGGED-IN carol", await login.ReadAsync());
        var record = await ReadRecordAsync(location);
        Assert.Equal("carol", record!.Subject);
        Assert.Equal(2, record.Generation);
        Assert.Equal("TOKEN synthetic-access-carol", await refresh.RunAsync("token"));
        await refresh.ExitAsync();
        await login.ExitAsync();
    }

    private static async Task<StoredSession?> ReadRecordAsync(ProcessTestDirectory location) =>
        await new FileTokenStore(location.Path).ReadAsync(SessionProcess.Key, Ct);
}
