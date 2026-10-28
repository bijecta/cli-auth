using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

public sealed class SessionCoordinatorTests
{
    [Theory]
    [InlineData(LoginFlow.Browser)]
    [InlineData(LoginFlow.Device)]
    public async Task LoginCommitsTheValidatedResultAndForwardsTheSelectedInteraction(LoginFlow flow)
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        Func<DeviceAuthorizationPrompt, CancellationToken, Task> present = (_, _) => Task.CompletedTask;
        var options = flow == LoginFlow.Browser ? new LoginOptions() : new LoginOptions(flow, present);
        fixture.Protocol.Login = (profile, received, browser, token) =>
        {
            Assert.Same(fixture.Profile, profile);
            Assert.Same(options, received);
            Assert.Same(fixture.Browser, browser);
            Assert.Equal(ct, token);
            return Task.FromResult(Candidate(profile));
        };
        var result = await fixture.Auth().LoginAsync("work", options, ct);
        var stored = Assert.IsType<StoredSession>(await fixture.ReadAsync());
        Assert.Equal(SessionLifecycleState.Active, stored.State);
        Assert.Equal("synthetic-subject", result.Subject);
        Assert.Equal(fixture.Profile.Persistence, result.Persistence);
        Assert.Equal("synthetic-refresh", stored.RefreshToken);
        Assert.Equal(0, stored.Generation);
        Assert.NotEqual(Guid.Empty, stored.Epoch);
        Assert.Equal(1, fixture.Protocol.LoginCalls);
        Assert.Equal(0, fixture.Browser.Calls);
    }

    [Fact]
    public async Task LoginCannotUndoLogoutEvenWhenTheOriginalSessionWasMissing()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var entered = Signal<bool>();
        var result = Signal<ValidatedLogin>();
        fixture.Protocol.Login = async (_, _, _, token) =>
        {
            entered.SetResult(true);
            return await result.Task.WaitAsync(token);
        };
        var login = fixture.Auth().LoginAsync("work", new LoginOptions(), ct);
        await Bounded(entered.Task);
        // This would deadlock if the login held its session lease across user interaction.
        var logout = await Bounded(fixture.Auth().LogoutAsync("work", new LogoutOptions(), ct));
        Assert.True(logout.LocalSessionRemoved);
        result.SetResult(Candidate(fixture.Profile));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => login);
        Assert.Equal(AuthErrorCode.OperationSuperseded, error.ErrorCode);
        Assert.Equal(SessionLifecycleState.None, (await fixture.ReadAsync())!.State);
    }

    [Fact]
    public async Task FirstSuccessfulLoginCommitWinsAcrossAuthenticatorsSharingAStore()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var bothEntered = Signal<bool>();
        var replies = new[] { Signal<ValidatedLogin>(), Signal<ValidatedLogin>() };
        var ordinal = 0;
        fixture.Protocol.Login = async (_, _, _, token) =>
        {
            var index = Interlocked.Increment(ref ordinal) - 1;
            if (index == 1) { bothEntered.SetResult(true); }
            return await replies[index].Task.WaitAsync(token);
        };
        var first = fixture.Auth().LoginAsync("work", new LoginOptions(), ct);
        var second = fixture.Auth().LoginAsync("work", new LoginOptions(), ct);
        await Bounded(bothEntered.Task);
        replies[1].SetResult(Candidate(fixture.Profile, "second"));
        Assert.Equal("second", (await second).Subject);
        replies[0].SetResult(Candidate(fixture.Profile, "first"));
        var failure = await Assert.ThrowsAsync<CliAuthException>(() => first);
        Assert.Equal(AuthErrorCode.OperationSuperseded, failure.ErrorCode);
        Assert.Equal("second", (await fixture.ReadAsync())!.Subject);
    }

    [Fact]
    public async Task GenerationChangesWithTheSameEpochDoNotSupersedeExplicitLogin()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var original = Session(fixture.Profile, generation: 3);
        await fixture.SeedAsync(original);
        var entered = Signal<bool>();
        var reply = Signal<ValidatedLogin>();
        fixture.Protocol.Login = async (_, _, _, token) =>
        {
            entered.SetResult(true);
            return await reply.Task.WaitAsync(token);
        };
        var login = fixture.Auth().LoginAsync("work", new LoginOptions(), ct);
        await Bounded(entered.Task);
        await using (var lease = await fixture.Store.Inner.AcquireAsync(fixture.Key, TimeSpan.FromSeconds(1), ct))
        {
            await fixture.SeedAsync(Session(fixture.Profile, generation: 4, epoch: original.Epoch));
        }
        reply.SetResult(Candidate(fixture.Profile));
        await login;
        var current = (await fixture.ReadAsync())!;
        Assert.Equal(5, current.Generation);
        Assert.NotEqual(original.Epoch, current.Epoch);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("subject")]
    public async Task InvalidProtocolResultsCannotReplaceAnExistingSession(string fault)
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Login = (profile, _, _, _) => Task.FromResult(Candidate(profile,
            subject: fault == "subject" ? "" : "synthetic-subject",
            issuer: fault == "issuer" ? new Uri("https://other.example.test") : null,
            clientId: fault == "client" ? "wrong-client" : null,
            scopes: fault == "scope" ? ["admin"] : null,
            expiry: fault == "expired" ? CoreTestData.Now : null));
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            fixture.Auth().LoginAsync("work", new LoginOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task CancellationAfterAnAdapterReturnsCannotCommit()
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        using var cancellation = new CancellationTokenSource();
        fixture.Protocol.Login = (profile, _, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Candidate(profile));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Auth().LoginAsync("work", new LoginOptions(), cancellation.Token));
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task FailedCommitAndUnsafeProtocolExceptionsNeverEscapeWithCredentials()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        fixture.Protocol.Login = (_, _, _, _) => throw new InvalidOperationException("synthetic-secret-response");
        var protocolError = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().LoginAsync("work", new LoginOptions(), ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, protocolError.ErrorCode);
        Assert.Null(protocolError.InnerException);
        Assert.DoesNotContain("synthetic-secret-response", protocolError.ToString());
        fixture.Protocol.Login = null;
        fixture.Store.WriteError = new IOException("synthetic-secret-on-write");
        var commitError = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().LoginAsync("work", new LoginOptions(), ct));
        Assert.Equal(AuthErrorCode.StorageUnavailable, commitError.ErrorCode);
        Assert.Null(commitError.InnerException);
        Assert.Null(await fixture.ReadAsync());
        Assert.DoesNotContain("synthetic-secret-on-write", commitError.ToString());
    }

    [Fact]
    public async Task UnavailableStoreFailsBeforeBeginningProtocolInteraction()
    {
        var fixture = new Fixture();
        fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageUnavailable);
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            fixture.Auth().LoginAsync("work", new LoginOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Equal(0, fixture.Protocol.LoginCalls);
    }

    [Fact]
    public async Task StatusAndCachedAcquisitionStaySilentAndReloadLocalState()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        var auth = fixture.Auth();
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", ct)).Kind);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => auth.GetAccessTokenAsync("work", ct)));
        Assert.All(tokens, token => Assert.Same(original.AccessToken, token));
        fixture.Clock.Now = CoreTestData.Now.AddMinutes(5);
        Assert.Equal(SessionStatusKind.RefreshRequired, (await auth.GetStatusAsync("work", ct)).Kind);
        var unsupported = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(AuthErrorCode.UnsupportedCapability, unsupported.ErrorCode);
        Assert.Same(original, await fixture.ReadAsync());
        Assert.Equal(0, fixture.Protocol.LoginCalls);
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
        Assert.Equal(0, fixture.Browser.Calls);
    }

    [Fact]
    public async Task ExpiredSessionWithoutRefreshAndOrphanedRefreshRequireLogin()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var auth = fixture.Auth();
        await fixture.SeedAsync(Session(fixture.Profile, expiry: CoreTestData.Now, refreshToken: null));
        var expired = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, expired.ErrorCode);
        var refreshing = Session(fixture.Profile, state: SessionLifecycleState.RefreshInProgress, generation: 3);
        await fixture.SeedAsync(refreshing);
        Assert.Equal(SessionStatusKind.Uncertain, (await auth.GetStatusAsync("work", ct)).Kind);
        var orphan = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, orphan.ErrorCode);
        var uncertain = (await fixture.ReadAsync())!;
        Assert.Equal(SessionLifecycleState.Uncertain, uncertain.State);
        Assert.Equal(4, uncertain.Generation);
        Assert.Equal(refreshing.Epoch, uncertain.Epoch);
        Assert.Null(uncertain.AccessToken);
        Assert.Null(uncertain.RefreshToken);
    }

    [Fact]
    public async Task MultipleProfilesUseIsolatedRecordsInOneStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var first = Profile("work");
        var second = Profile("development");
        var protocol = new Protocol();
        var auth = new CliAuthenticator([first, second], store, protocol, timeProvider: new Clock());
        await auth.LoginAsync("work", new LoginOptions(), ct);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("development", ct)).Kind);
        await auth.LoginAsync("development", new LoginOptions(), ct);
        await auth.LogoutAsync("work", new LogoutOptions(), ct);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("development", ct)).Kind);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("scope")]
    public async Task ProfileBindingMismatchIsCorruptionRatherThanAnAvailableToken(string fault)
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var token = new AccessToken("synthetic-wrong-binding", CoreTestData.Now.AddMinutes(5),
            fault == "scope" ? ["admin"] : fixture.Profile.Scopes);
        await fixture.SeedAsync(new StoredSession(0, Guid.NewGuid(), SessionLifecycleState.Active,
            token, null, fault == "issuer" ? new Uri("https://other.example.test") : fixture.Profile.Issuer,
            fault == "client" ? "wrong-client" : fixture.Profile.ClientId, "subject"));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().GetStatusAsync("work", ct));
        Assert.Equal(AuthErrorCode.StorageCorrupt, error.ErrorCode);
    }

    [Theory]
    [InlineData(LoginFlow.Browser)]
    [InlineData(LoginFlow.Device)]
    public async Task UnsupportedFlowDoesNotSwitchOrOverwriteTheExistingSession(LoginFlow flow)
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Login = (_, options, _, _) =>
        {
            Assert.Equal(flow, options.Flow);
            throw new CliAuthException(AuthErrorCode.UnsupportedCapability);
        };
        var options = flow == LoginFlow.Browser ? new LoginOptions() :
            new LoginOptions(flow, (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            fixture.Auth().LoginAsync("work", options, TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.UnsupportedCapability, error.ErrorCode);
        Assert.Equal(1, fixture.Protocol.LoginCalls);
        Assert.Same(original, await fixture.ReadAsync());
        Assert.Equal(0, fixture.Browser.Calls);
    }

    [Fact]
    public async Task WaitingFacadeOperationCanCancelWithoutReleasingAnotherOwnersLock()
    {
        var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var auth = fixture.Auth();
        await using var owner = await fixture.Store.Inner.AcquireAsync(fixture.Key, TimeSpan.FromSeconds(1), ct);
        using var cancellation = new CancellationTokenSource();
        var waiting = auth.GetStatusAsync("work", cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, fixture.Store.Reads);
    }
}
