using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

public sealed class CoordinatorLogoutTests
{
    [Fact]
    public async Task LocalLogoutDoesNotReadCredentialsOrContactTheProvider()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        await fixture.SeedAsync(Session(fixture.Profile));
        fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageCorrupt);
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(), ct);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.NotRequested, result.RevocationOutcome);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
        Assert.Null(await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(RevocationOutcome.Succeeded)]
    [InlineData(RevocationOutcome.Failed)]
    [InlineData(RevocationOutcome.Unsupported)]
    public async Task RemoteOutcomeIsSeparateAndLocalRemovalIsCommittedFirst(RevocationOutcome outcome)
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        await fixture.SeedAsync(Session(fixture.Profile));
        fixture.Protocol.Revoke = async (profile, token, _) =>
        {
            Assert.Same(fixture.Profile, profile);
            Assert.Equal("synthetic-refresh", token);
            Assert.Null(await fixture.ReadAsync());
            await using var lease = await fixture.Store.Inner.AcquireAsync(fixture.Key, TimeSpan.FromSeconds(1), ct);
            return outcome;
        };
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(true), ct);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(outcome, result.RevocationOutcome);
        Assert.Equal(1, fixture.Protocol.RevokeCalls);
    }

    [Fact]
    public async Task ProviderFailureCannotRestoreCredentialsOrExposeItsRawException()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile));
        fixture.Protocol.Revoke = (_, _, _) => throw new IOException("synthetic-secret-response");
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(true), TestContext.Current.CancellationToken);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.Failed, result.RevocationOutcome);
        Assert.Null(await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(AuthErrorCode.StorageCorrupt)]
    [InlineData(AuthErrorCode.UnsupportedSchema)]
    [InlineData(AuthErrorCode.StorageUnavailable)]
    public async Task ExplicitLogoutCanRemoveUnreadableCredentialsEvenWhenRevocationWasRequested(AuthErrorCode code)
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile));
        fixture.Store.ReadError = new CliAuthException(code);
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(true), TestContext.Current.CancellationToken);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.Failed, result.RevocationOutcome);
        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
    }

    [Fact]
    public async Task DeleteFailureNeverReportsSuccessOrAttemptsRevocation()
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Store.DeleteError = new IOException("synthetic-secret-storage-error");
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            fixture.Auth().LogoutAsync("work", new LogoutOptions(true), TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret-storage-error", error.ToString());
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task RemoteWorkCannotDeleteANewerLoginThatCommitsWhileItWaits()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        await fixture.SeedAsync(Session(fixture.Profile));
        var entered = Signal<bool>();
        var completion = Signal<RevocationOutcome>();
        fixture.Protocol.Revoke = async (_, _, token) =>
        {
            entered.SetResult(true);
            return await completion.Task.WaitAsync(token);
        };
        var logout = fixture.Auth().LogoutAsync("work", new LogoutOptions(true), ct);
        await Bounded(entered.Task);
        var login = await Bounded(fixture.Auth().LoginAsync("work", new LoginOptions(), ct));
        completion.SetResult(RevocationOutcome.Succeeded);
        await logout;
        Assert.Equal(login.Subject, (await fixture.ReadAsync())!.Subject);
        Assert.Equal(SessionLifecycleState.Active, (await fixture.ReadAsync())!.State);
    }

    [Fact]
    public async Task CancellationDuringRevocationDoesNotUndoAlreadyCommittedLocalRemoval()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile));
        var entered = Signal<bool>();
        fixture.Protocol.Revoke = async (_, _, token) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RevocationOutcome.Succeeded;
        };
        using var cancellation = new CancellationTokenSource();
        var logout = fixture.Auth().LogoutAsync("work", new LogoutOptions(true), cancellation.Token);
        await Bounded(entered.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logout);
        Assert.Null(await fixture.ReadAsync());
    }

    [Fact]
    public async Task RevocationDeadlineCancelsRemoteWorkWithoutRestoringTheSession()
    {
        var fixture = new Fixture();
        var clock = new DeadlineClock();
        var ct = TestContext.Current.CancellationToken;
        await fixture.SeedAsync(Session(fixture.Profile));
        var reply = Signal<RevocationOutcome>();
        CancellationToken remoteToken = default;
        fixture.Protocol.Revoke = (_, _, token) =>
        {
            remoteToken = token;
            return reply.Task.WaitAsync(token);
        };
        var auth = new CliAuthenticator([fixture.Profile], fixture.Store, fixture.Protocol,
            fixture.Store.Inner, fixture.Browser, clock);
        var pending = auth.LogoutAsync("work", new LogoutOptions(true), ct);
        Assert.False(pending.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.DueTime);
        clock.Expire();
        var result = await Bounded(pending);
        Assert.True(remoteToken.IsCancellationRequested);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.Failed, result.RevocationOutcome);
        Assert.Null(await fixture.ReadAsync());
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private DeadlineTimer? _timer;
        internal TimeSpan DueTime { get; private set; }
        public override DateTimeOffset GetUtcNow() => CoreTestData.Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            _timer = new DeadlineTimer(callback, state);
            return _timer;
        }
        internal void Expire() => _timer!.Expire();

        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Expire()
            {
                if (!_disposed) { callback(state); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task NoRefreshTokenMeansNoRevocationAttempt()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile, refreshToken: null));
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(true), TestContext.Current.CancellationToken);
        Assert.Equal(RevocationOutcome.Unsupported, result.RevocationOutcome);
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
    }
}
