using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.KeycloakTests;

// Silent refresh against real Keycloak with strict refresh-token rotation (ADR 0009). Instead of waiting for the
// access token to expire, the authenticator's clock is moved forward; the provider still issues and checks real tokens.
[Collection(KeycloakCollection.Name)]
public sealed class RefreshIntegrationTests(KeycloakFixture fixture, ITestOutputHelper output)
{
    [Fact(Timeout = 90_000)]
    public async Task ExpiringTokensAreRefreshedWithRotationAndAReusedRefreshTokenIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture);
        var clock = new ShiftedClock();
        var store = new MemoryTokenStore();
        var profile = fixture.Profile();
        var key = SessionKey.ForProfile(profile);
        var auth = new CliAuthenticator([profile], store, browser: browser, timeProvider: clock);
        LoginResult login;
        try { login = await auth.LoginAsync("work", new LoginOptions(), ct); }
        catch
        {
            output.WriteLine($"Browser stage: {browser.Stage}");
            throw;
        }
        var loggedIn = (await store.ReadAsync(key, ct))!;
        Assert.NotNull(loggedIn.RefreshToken);
        Assert.NotNull(loggedIn.AccessToken!.ObtainedAtUtc);

        // Inside the hard expiry margin: acquisition must refresh.
        clock.Offset = login.ExpiresAtUtc - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        Assert.Equal(SessionStatusKind.RefreshRequired, (await auth.GetStatusAsync("work", ct)).Kind);
        var refreshed = await auth.GetAccessTokenAsync("work", ct);
        Assert.NotEqual(loggedIn.AccessToken.Value, refreshed.Value);
        Assert.True(refreshed.ExpiresAtUtc > login.ExpiresAtUtc);
        var afterRefresh = (await store.ReadAsync(key, ct))!;
        Assert.Equal(loggedIn.Generation + 1, afterRefresh.Generation);
        Assert.Equal(loggedIn.Epoch, afterRefresh.Epoch);
        Assert.Equal(login.Subject, afterRefresh.Subject);
        Assert.NotEqual(loggedIn.RefreshToken, afterRefresh.RefreshToken);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Same(refreshed, await auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(1, browser.OpenCount);

        // Put the rotated (now single-use, already used) refresh token back: Keycloak rejects it with invalid_grant.
        await store.WriteAsync(key, new StoredSession(afterRefresh.Generation + 1, afterRefresh.Epoch,
            SessionLifecycleState.Active, loggedIn.AccessToken, loggedIn.RefreshToken, afterRefresh.Issuer,
            afterRefresh.ClientId, afterRefresh.Subject), ct);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, error.ErrorCode);
        Assert.Equal("invalid_grant", error.ProviderErrorCode);
        Assert.Equal(SessionStatusKind.ReauthenticationRequired, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Null((await store.ReadAsync(key, ct))!.RefreshToken);
    }

    private sealed class ShiftedClock : TimeProvider
    {
        internal TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
