using System.Net;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProtocolTests;

// The refresh_token grant through the real protocol adapter and coordinator, against the fake provider (ADR 0009).
public sealed class RefreshProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<StoredSession> SeedAsync(BrowserFixture f, TimeSpan remaining, TimeSpan? age = null)
    {
        var now = f.Time.GetUtcNow();
        var profile = f.Profile();
        var session = new StoredSession(4, Guid.NewGuid(), SessionLifecycleState.Active,
            new AccessToken("stored-access", now.Add(remaining), profile.Scopes, obtainedAtUtc: age is null ? null : now - age),
            "stored-refresh", new Uri(BrowserFixture.Issuer), BrowserFixture.ClientId, "test-subject");
        await f.Store.WriteAsync(SessionKey.ForProfile(profile), session, Ct);
        return session;
    }

    private static async Task<StoredSession?> ReadAsync(BrowserFixture f) =>
        await f.Store.ReadAsync(SessionKey.ForProfile(f.Profile()), Ct);

    [Fact]
    public async Task ARotatingProviderReplacesTheRefreshTokenAndTheSubjectIsChecked()
    {
        using var f = new BrowserFixture();
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));

        var token = await f.Auth().GetAccessTokenAsync("work", Ct);

        Assert.Equal("synthetic-access", token.Value);
        Assert.Equal(f.Time.GetUtcNow().AddSeconds(300), token.ExpiresAtUtc);
        Assert.Equal(f.Time.GetUtcNow(), token.ObtainedAtUtc);
        Assert.Equal("refresh_token", f.Exchange!["grant_type"]);
        Assert.Equal("stored-refresh", f.Exchange["refresh_token"]);
        Assert.Equal(BrowserFixture.ClientId, f.Exchange["client_id"]);
        Assert.False(f.Exchange.ContainsKey("scope"));
        var stored = (await ReadAsync(f))!;
        Assert.Equal("synthetic-refresh", stored.RefreshToken);
        Assert.Equal(seeded.Epoch, stored.Epoch);
        Assert.Equal(5, stored.Generation);
        Assert.Equal(seeded.AccessToken!.GrantedScopes, stored.AccessToken!.GrantedScopes);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Fact]
    public async Task ANonRotatingResponseWithoutAnIdTokenKeepsTheRefreshToken()
    {
        using var f = new BrowserFixture();
        await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.ChangeTokens = t => { t.Remove("refresh_token"); t.Remove("id_token"); return t; };
        await f.Auth().GetAccessTokenAsync("work", Ct);
        Assert.Equal("stored-refresh", (await ReadAsync(f))!.RefreshToken);
    }

    [Fact]
    public async Task ANarrowerScopeIsStoredAndAWiderScopeIsRejected()
    {
        using var f = new BrowserFixture();
        await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.ChangeTokens = t => { t["scope"] = "openid"; return t; };
        await f.Auth().GetAccessTokenAsync("work", Ct);
        Assert.Equal(["openid"], (await ReadAsync(f))!.AccessToken!.GrantedScopes);

        using var wider = new BrowserFixture();
        var seeded = await SeedAsync(wider, TimeSpan.FromSeconds(30));
        wider.ChangeTokens = t => { t["scope"] = "openid profile admin"; return t; };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => wider.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Equal(seeded.Generation, (await ReadAsync(wider))!.Generation);
    }

    [Theory]
    [InlineData("other_sub")]
    [InlineData("wrong_audience")]
    [InlineData("bad_signature")]
    public async Task AnInvalidRefreshedIdTokenIsAProtocolFailureThatKeepsTheSession(string scenario)
    {
        using var f = new BrowserFixture { Scenario = scenario };
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Equal(seeded.Generation, (await ReadAsync(f))!.Generation);
        Assert.Equal("stored-refresh", (await ReadAsync(f))!.RefreshToken);
    }

    // RFC 6749 specifies 400, but some providers send invalid_grant with 401 or 403.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task InvalidGrantEndsTheSessionWithACredentialFreeMarker(HttpStatusCode status)
    {
        using var f = new BrowserFixture();
        await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.OnTokenRequest = _ => Task.FromResult(BrowserFixture.Json(new { error = "invalid_grant" }, status));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, error.ErrorCode);
        Assert.Equal("invalid_grant", error.ProviderErrorCode);
        var marker = (await ReadAsync(f))!;
        Assert.Equal(SessionLifecycleState.ReauthenticationRequired, marker.State);
        Assert.Null(marker.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client", AuthErrorCode.ProtocolFailure)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_scope", AuthErrorCode.ProtocolFailure)]
    [InlineData(HttpStatusCode.BadRequest, "temporarily_unavailable", AuthErrorCode.NetworkFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, AuthErrorCode.NetworkFailure)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", AuthErrorCode.NetworkFailure)]
    [InlineData(HttpStatusCode.Found, null, AuthErrorCode.ProtocolFailure)]
    public async Task OtherProviderFailuresAreClassifiedAndKeepTheSession(HttpStatusCode status, string? code, AuthErrorCode expected)
    {
        using var f = new BrowserFixture();
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.OnTokenRequest = _ => Task.FromResult(code is null
            ? new HttpResponseMessage(status)
            : BrowserFixture.Json(new { error = code, error_description = "synthetic-secret-detail" }, status));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(code, error.ProviderErrorCode);
        Assert.DoesNotContain("synthetic", error.Message);
        var stored = (await ReadAsync(f))!;
        Assert.Equal(seeded.Generation, stored.Generation);
        Assert.Equal("stored-refresh", stored.RefreshToken);
        Assert.Equal(1, f.TokenCalls);
    }

    [Theory]
    [InlineData("missing_expiry")]
    [InlineData("wrong_type")]
    [InlineData("not_json")]
    public async Task AMalformedSuccessResponseIsAProtocolFailure(string fault)
    {
        using var f = new BrowserFixture();
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.ChangeTokens = t =>
        {
            if (fault == "missing_expiry") { t.Remove("expires_in"); }
            else if (fault == "wrong_type") { t["token_type"] = "DPoP"; }
            return t;
        };
        if (fault == "not_json")
        {
            f.OnTokenRequest = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("synthetic") });
        }
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Equal(seeded.Generation, (await ReadAsync(f))!.Generation);
    }

    [Fact]
    public async Task AProviderWithoutTheRefreshGrantIsUnsupported()
    {
        using var f = new BrowserFixture();
        await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.ChangeMetadata = m => { m["grant_types_supported"] = new[] { "authorization_code" }; return m; };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.UnsupportedCapability, error.ErrorCode);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact]
    public async Task AHungTokenRequestTimesOutInsideTheHardMarginAndKeepsTheSession()
    {
        using var f = new BrowserFixture();
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));
        f.OnTokenRequest = async ct => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        var acquisition = f.Auth().GetAccessTokenAsync("work", Ct);
        // The coordinator's 20-second budget expires before the transport's 30-second deadline.
        await f.Time.AdvanceDelayAsync(20, Ct);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(AuthErrorCode.NetworkFailure, error.ErrorCode);
        Assert.Equal(seeded.Generation, (await ReadAsync(f))!.Generation);
        Assert.Equal(1, f.TokenCalls);
    }

    [Fact]
    public async Task AnAnswerReceivedBeforeTheCallerCancelsIsStillValidatedAndCommitted()
    {
        using var f = new BrowserFixture { Scenario = "rotation" };
        var seeded = await SeedAsync(f, TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // The refreshed ID token needs a signing-key reload; the caller cancels while it runs, after the provider
        // has answered and possibly rotated the refresh token.
        f.ChangeJwks = keys =>
        {
            if (f.KeyCalls > 1) { cancel.Cancel(); }
            return keys;
        };
        var token = await f.Auth().GetAccessTokenAsync("work", cancel.Token);
        Assert.Equal("synthetic-access", token.Value);
        var stored = (await ReadAsync(f))!;
        Assert.Equal("synthetic-refresh", stored.RefreshToken);
        Assert.Equal(seeded.Generation + 1, stored.Generation);
    }

    [Fact]
    public async Task AHungEarlyRefreshReturnsTheStillValidTokenAfterFiveSeconds()
    {
        using var f = new BrowserFixture();
        var seeded = await SeedAsync(f, TimeSpan.FromMinutes(4), age: TimeSpan.FromMinutes(56));
        f.OnTokenRequest = async ct => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        var acquisition = f.Auth().GetAccessTokenAsync("work", Ct);
        await f.Time.AdvanceDelayAsync(5, Ct);
        var token = await acquisition.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("stored-access", token.Value);
        Assert.Equal(seeded.Generation, (await ReadAsync(f))!.Generation);
    }

    [Fact]
    public async Task AnUnreachableProviderInsideTheEarlyMarginReturnsTheStillValidToken()
    {
        using var f = new BrowserFixture();
        await SeedAsync(f, TimeSpan.FromMinutes(4), age: TimeSpan.FromMinutes(56));
        f.Override = (_, _) => throw new HttpRequestException("synthetic-connection-refused");
        Assert.Equal("stored-access", (await f.Auth().GetAccessTokenAsync("work", Ct)).Value);
    }
}
