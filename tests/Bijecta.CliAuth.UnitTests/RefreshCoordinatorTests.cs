using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

// ADR 0009: refresh under the session lease, retry instead of forced login, and the early-refresh margin.
public sealed class RefreshCoordinatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Inside the 60-second hard margin: the cached token must not be returned.
    private static StoredSession Expiring(OidcProfile profile, long generation = 2, Guid? epoch = null) =>
        Session(profile, expiry: CoreTestData.Now.AddSeconds(30), generation: generation, epoch: epoch);

    // Inside the 5-minute early margin of a one-hour token, outside the hard margin.
    private static StoredSession Early(OidcProfile profile) => Session(profile, expiry: CoreTestData.Now.AddMinutes(4),
        obtained: CoreTestData.Now.AddMinutes(-56), generation: 2);

    [Fact]
    public async Task ARefreshCommitsTheNextGenerationOfTheSameSessionAndReturnsTheNewToken()
    {
        var fixture = new Fixture();
        var epoch = Guid.NewGuid();
        await fixture.SeedAsync(Expiring(fixture.Profile, epoch: epoch));
        string? sent = null;
        fixture.Protocol.Refresh = (_, refreshToken, _) => { sent = refreshToken; return Task.FromResult(Refreshed()); };

        var token = await fixture.Auth().GetAccessTokenAsync("work", Ct);

        Assert.Equal("synthetic-refreshed-access", token.Value);
        Assert.Equal("synthetic-refresh", sent);
        var stored = (await fixture.ReadAsync())!;
        Assert.Equal(SessionLifecycleState.Active, stored.State);
        Assert.Equal(3, stored.Generation);
        Assert.Equal(epoch, stored.Epoch);
        Assert.Same(token, stored.AccessToken);
        Assert.Equal("synthetic-rotated-refresh", stored.RefreshToken);
        Assert.Equal("synthetic-cached-subject", stored.Subject);
        Assert.Equal(fixture.Profile.Issuer, stored.Issuer);
        Assert.Equal(SessionStatusKind.Available, (await fixture.Auth().GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task AProviderThatDoesNotRotateKeepsTheStoredRefreshToken()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Expiring(fixture.Profile));
        fixture.Protocol.Refresh = (_, _, _) => Task.FromResult(Refreshed(refreshToken: null));
        await fixture.Auth().GetAccessTokenAsync("work", Ct);
        Assert.Equal("synthetic-refresh", (await fixture.ReadAsync())!.RefreshToken);
    }

    [Fact]
    public async Task SimultaneousCallersShareOneRefresh()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Expiring(fixture.Profile));
        var entered = Signal<bool>();
        var release = Signal<bool>();
        fixture.Protocol.Refresh = async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return Refreshed();
        };
        var auth = fixture.Auth();
        var first = auth.GetAccessTokenAsync("work", Ct);
        await Bounded(entered.Task);
        var others = Enumerable.Range(0, 99).Select(_ => auth.GetAccessTokenAsync("work", Ct)).ToArray();
        release.SetResult(true);

        var tokens = await Bounded(Task.WhenAll(others.Prepend(first)));
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
        Assert.Equal(1, fixture.Protocol.RefreshCalls);
        Assert.Equal(3, (await fixture.ReadAsync())!.Generation);
    }

    [Fact]
    public async Task ARejectedRefreshTokenLeavesACredentialFreeMarkerThatLoginReplaces()
    {
        var fixture = new Fixture();
        var epoch = Guid.NewGuid();
        await fixture.SeedAsync(Expiring(fixture.Profile, epoch: epoch));
        fixture.Protocol.Refresh = (_, _, _) =>
            throw new CliAuthException(AuthErrorCode.ReauthenticationRequired, "invalid_grant");
        var auth = fixture.Auth();

        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, error.ErrorCode);
        Assert.Equal("invalid_grant", error.ProviderErrorCode);
        var marker = (await fixture.ReadAsync())!;
        Assert.Equal(SessionLifecycleState.ReauthenticationRequired, marker.State);
        Assert.Equal(3, marker.Generation);
        Assert.Equal(epoch, marker.Epoch);
        Assert.Null(marker.AccessToken);
        Assert.Null(marker.RefreshToken);
        Assert.Equal(SessionStatusKind.ReauthenticationRequired, (await auth.GetStatusAsync("work", Ct)).Kind);

        // No further refresh attempts: the marker holds no refresh token.
        await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", Ct));
        Assert.Equal(1, fixture.Protocol.RefreshCalls);
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Theory]
    [InlineData(AuthErrorCode.ProtocolFailure, "invalid_client")]
    [InlineData(AuthErrorCode.NetworkFailure, null)]
    [InlineData(AuthErrorCode.NetworkFailure, "temporarily_unavailable")]
    [InlineData(AuthErrorCode.DiscoveryFailed, null)]
    public async Task OtherFailuresInsideTheHardMarginKeepTheSessionForTheNextCall(AuthErrorCode code, string? provider)
    {
        var fixture = new Fixture();
        var original = Expiring(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Refresh = (_, _, _) => throw new CliAuthException(code, provider);
        var auth = fixture.Auth();

        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", Ct));
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(provider, error.ProviderErrorCode);
        Assert.Same(original, await fixture.ReadAsync());

        // ADR 0009: no forced login; the next call retries with the same refresh token.
        fixture.Protocol.Refresh = null;
        Assert.Equal("synthetic-refreshed-access", (await auth.GetAccessTokenAsync("work", Ct)).Value);
        Assert.Equal(2, fixture.Protocol.RefreshCalls);
    }

    [Fact]
    public async Task UnexpectedProtocolExceptionsBecomeAProtocolFailureAndKeepTheSession()
    {
        var fixture = new Fixture();
        var original = Expiring(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Refresh = (_, _, _) => throw new InvalidOperationException("synthetic-secret-detail");
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("refresh_control")]
    public async Task AnInvalidRefreshResultIsRejectedWithoutACommit(string fault)
    {
        var fixture = new Fixture();
        var original = Expiring(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Refresh = (_, _, _) => Task.FromResult(fault switch
        {
            "scope" => Refreshed(scopes: ["api.read", "admin"]),
            "expired" => Refreshed(expiry: CoreTestData.Now),
            _ => Refreshed(refreshToken: "synthetic\nrefresh")
        });
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task CancellationByTheCallerKeepsTheSession()
    {
        var fixture = new Fixture();
        var original = Expiring(fixture.Profile);
        await fixture.SeedAsync(original);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        fixture.Protocol.Refresh = async (_, _, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Auth().GetAccessTokenAsync("work", cancel.Token));
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task AFailedCommitNeverReturnsTheNewToken()
    {
        var fixture = new Fixture();
        var original = Expiring(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Store.WriteError = new IOException("synthetic-disk-full");
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Equal(1, fixture.Protocol.RefreshCalls);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task ATooShortRefreshedLifetimeIsCommittedButReportedOncePerCall()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Expiring(fixture.Profile));
        fixture.Protocol.Refresh = (_, _, _) => Task.FromResult(Refreshed(expiry: CoreTestData.Now.AddSeconds(45)));
        var auth = fixture.Auth();
        for (var call = 1; call <= 2; call++)
        {
            var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", Ct));
            Assert.Equal(AuthErrorCode.TokenLifetimeTooShort, error.ErrorCode);
            // Exactly one refresh per call, never a loop (TOK-01).
            Assert.Equal(call, fixture.Protocol.RefreshCalls);
            Assert.Equal(2 + call, (await fixture.ReadAsync())!.Generation);
        }
    }

    [Fact]
    public async Task LogoutAndLoginWaitForARunningRefreshAndWin()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Expiring(fixture.Profile));
        var entered = Signal<bool>();
        var release = Signal<bool>();
        fixture.Protocol.Refresh = async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return Refreshed();
        };
        var auth = fixture.Auth();
        var refresh = auth.GetAccessTokenAsync("work", Ct);
        await Bounded(entered.Task);
        var logout = auth.LogoutAsync("work", new LogoutOptions(), Ct);
        await Task.Delay(100, Ct);
        Assert.False(logout.IsCompleted);

        release.SetResult(true);
        Assert.Equal("synthetic-refreshed-access", (await Bounded(refresh)).Value);
        Assert.True((await Bounded(logout)).LocalSessionRemoved);
        Assert.Null(await fixture.ReadAsync());
    }

    [Fact]
    public async Task InsideTheEarlyMarginARefreshReplacesTheStillValidToken()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Early(fixture.Profile));
        Assert.Equal(SessionStatusKind.Available, (await fixture.Auth().GetStatusAsync("work", Ct)).Kind);
        Assert.Equal("synthetic-refreshed-access", (await fixture.Auth().GetAccessTokenAsync("work", Ct)).Value);
        Assert.Equal(3, (await fixture.ReadAsync())!.Generation);
    }

    [Theory]
    [InlineData(AuthErrorCode.NetworkFailure)]
    [InlineData(AuthErrorCode.DiscoveryFailed)]
    public async Task ATransientEarlyFailureReturnsTheStillValidToken(AuthErrorCode code)
    {
        var fixture = new Fixture();
        var original = Early(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Refresh = (_, _, _) => throw new CliAuthException(code);
        Assert.Same(original.AccessToken, await fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Fact]
    public async Task AnEarlyTimeoutReturnsTheStillValidToken()
    {
        var fixture = new Fixture();
        var original = Early(fixture.Profile);
        await fixture.SeedAsync(original);
        // A canceled token the caller did not cancel stands in for the coordinator's request deadline.
        fixture.Protocol.Refresh = (_, _, _) => throw new OperationCanceledException();
        Assert.Same(original.AccessToken, await fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(AuthErrorCode.ReauthenticationRequired)]
    [InlineData(AuthErrorCode.ProtocolFailure)]
    public async Task DefinitiveEarlyFailuresAreNotHidden(AuthErrorCode code)
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Early(fixture.Profile));
        fixture.Protocol.Refresh = (_, _, _) => throw new CliAuthException(code);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(code, error.ErrorCode);
        var expected = code == AuthErrorCode.ReauthenticationRequired
            ? SessionLifecycleState.ReauthenticationRequired : SessionLifecycleState.Active;
        Assert.Equal(expected, (await fixture.ReadAsync())!.State);
    }

    [Fact]
    public async Task TheEarlyMarginCoversOnlyTheLastHalfOfAShortLifetime()
    {
        var fixture = new Fixture();
        // A five-minute token, one minute old: four minutes left, but early refresh starts at two and a half.
        var original = Session(fixture.Profile, expiry: CoreTestData.Now.AddMinutes(4), obtained: CoreTestData.Now.AddMinutes(-1));
        await fixture.SeedAsync(original);
        var auth = fixture.Auth();
        Assert.Same(original.AccessToken, await auth.GetAccessTokenAsync("work", Ct));
        Assert.Equal(0, fixture.Protocol.RefreshCalls);

        fixture.Clock.Now = CoreTestData.Now.AddMinutes(1.5);
        Assert.Equal("synthetic-refreshed-access", (await auth.GetAccessTokenAsync("work", Ct)).Value);
        Assert.Equal(1, fixture.Protocol.RefreshCalls);
    }

    [Fact]
    public async Task RecordsWithoutAnObtainedTimeAndDisabledEarlyRefreshUseOnlyTheHardMargin()
    {
        var older = new Fixture();
        var original = Session(older.Profile, expiry: CoreTestData.Now.AddMinutes(4));
        await older.SeedAsync(original);
        Assert.Same(original.AccessToken, await older.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(0, older.Protocol.RefreshCalls);

        var disabled = new Fixture(CoreTestData.Profile(scopes: ["api.read"], refreshAhead: TimeSpan.FromSeconds(60)));
        var early = Early(disabled.Profile);
        await disabled.SeedAsync(early);
        Assert.Same(early.AccessToken, await disabled.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(0, disabled.Protocol.RefreshCalls);
    }

    [Fact]
    public async Task ASessionWithoutARefreshTokenIsNeverRefreshedEarly()
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile, expiry: CoreTestData.Now.AddMinutes(4),
            obtained: CoreTestData.Now.AddMinutes(-56), refreshToken: null);
        await fixture.SeedAsync(original);
        Assert.Same(original.AccessToken, await fixture.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(0, fixture.Protocol.RefreshCalls);
    }
}
