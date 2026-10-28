using System.Security.Cryptography;
using System.Text;
using Bijecta.CliAuth.Core;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class BrowserLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("valid")]
    [InlineData("valid_azp")]
    [InlineData("valid_at_hash")]
    [InlineData("rotation")]
    [InlineData("es256")]
    public async Task SignedLoginCommitsOnlyValidatedSessionAndUsesExactPkceExchange(string scenario)
    {
        using var f = new BrowserFixture
        {
            Scenario = scenario
        };
        var auth = f.Auth();
        var result = await auth.LoginAsync("work", new LoginOptions(loginHint: "test@example.test", forceReauthentication: true), Ct);
        Assert.Equal("test-subject", result.Subject);
        Assert.Equal("code", f.Front!["response_type"]);
        Assert.Equal("S256", f.Front["code_challenge_method"]);
        Assert.Equal("login", f.Front["prompt"]);
        Assert.Equal("test@example.test", f.Front["login_hint"]);
        Assert.Equal(32, Base64UrlEncoder.DecodeBytes(f.Front["state"]).Length);
        Assert.Equal(32, Base64UrlEncoder.DecodeBytes(f.Front["nonce"]).Length);
        Assert.Equal(32, Base64UrlEncoder.DecodeBytes(f.Exchange!["code_verifier"]).Length);
        Assert.Equal(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(f.Exchange["code_verifier"]))), f.Front["code_challenge"]);
        Assert.NotEqual(f.Front["state"], f.Front["nonce"]);
        Assert.NotEqual(f.Front["state"], f.Exchange["code_verifier"]);
        Assert.Equal(f.Front["redirect_uri"], f.Exchange["redirect_uri"]);
        Assert.Equal(BrowserFixture.ClientId, f.Exchange["client_id"]);
        Assert.Equal("authorization_code", f.Exchange["grant_type"]);
        Assert.False(f.Exchange.ContainsKey("client_secret"));
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(scenario == "rotation" ? 2 : 1, f.KeyCalls);
        var token = await auth.GetAccessTokenAsync("work", Ct);
        Assert.Equal("synthetic-access", token.Value);
        Assert.Equal(f.Time.GetUtcNow().AddSeconds(300), token.ExpiresAtUtc);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", Ct)).Kind);
        Assert.Equal(1, f.MetadataCalls);
        Assert.True((await auth.LogoutAsync("work", new LogoutOptions(), Ct)).LocalSessionRemoved);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Theory]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("audience_slash")]
    [InlineData("wrong_azp")]
    [InlineData("missing_azp")]
    [InlineData("missing_sub")]
    [InlineData("wrong_nonce")]
    [InlineData("missing_nonce")]
    [InlineData("expired")]
    [InlineData("future_nbf")]
    [InlineData("unsigned")]
    [InlineData("wrong_algorithm")]
    [InlineData("bad_signature")]
    [InlineData("wrong_at_hash")]
    public async Task InvalidIdentityIsRejectedWithoutSavingCredentials(string scenario)
    {
        using var f = new BrowserFixture
        {
            Scenario = scenario
        };
        var auth = f.Auth();
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString());
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(1, f.KeyCalls);
    }

    [Theory]
    [InlineData("missing_id")]
    [InlineData("missing_access")]
    [InlineData("missing_expiry")]
    [InlineData("invalid_expiry")]
    [InlineData("zero_expiry")]
    [InlineData("negative_expiry")]
    [InlineData("wrong_type")]
    [InlineData("expanded_scopes")]
    [InlineData("empty_refresh")]
    public async Task InvalidTokenEnvelopeCannotCreateSession(string scenario)
    {
        using var f = new BrowserFixture();
        f.ChangeTokens = token =>
        {
            switch (scenario)
            {
                case "missing_id":
                    token.Remove("id_token");
                    break;
                case "missing_access":
                    token.Remove("access_token");
                    break;
                case "missing_expiry":
                    token.Remove("expires_in");
                    break;
                case "invalid_expiry":
                    token["expires_in"] = "300";
                    break;
                case "zero_expiry":
                    token["expires_in"] = 0;
                    break;
                case "negative_expiry":
                    token["expires_in"] = -1;
                    break;
                case "wrong_type":
                    token["token_type"] = "DPoP";
                    break;
                case "expanded_scopes":
                    token["scope"] = "openid admin";
                    break;
                case "empty_refresh":
                    token["refresh_token"] = "";
                    break;
            }

            return token;
        };
        var auth = f.Auth();
        Assert.Equal(AuthErrorCode.ProtocolFailure, (await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task FractionalExpiryAndNarrowedGrantsArePreserved()
    {
        using var f = new BrowserFixture();
        f.ChangeTokens = t =>
        {
            t["expires_in"] = 120.5;
            t["scope"] = "openid";
            t.Remove("refresh_token");
            return t;
        };
        var auth = f.Auth();
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        var token = await auth.GetAccessTokenAsync("work", Ct);
        Assert.Equal(f.Time.GetUtcNow().AddSeconds(120.5), token.ExpiresAtUtc);
        Assert.Equal(["openid"], token.GrantedScopes);
    }

    [Fact]
    public async Task LifetimeUsesRequestStartRatherThanResponseReceipt()
    {
        using var f = new BrowserFixture();
        f.ChangeTokens = t =>
        {
            f.Time.Advance(TimeSpan.FromSeconds(5));
            return t;
        };
        var auth = f.Auth();
        var started = f.Time.GetUtcNow();
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(started.AddSeconds(300), (await auth.GetAccessTokenAsync("work", Ct)).ExpiresAtUtc);
    }

    [Fact]
    public async Task ShortLifetimeFailsWithoutCommit()
    {
        using var f = new BrowserFixture();
        f.ChangeTokens = t =>
        {
            t["expires_in"] = 60;
            return t;
        };
        Assert.Equal(AuthErrorCode.TokenLifetimeTooShort, (await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
    }

    [Fact]
    public async Task BrowserLaunchFailureIsSafeAndClosesListener()
    {
        using var f = new BrowserFixture();
        f.OnBrowser = (_, _) => throw new InvalidOperationException("secret-from-host");
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.BrowserUnavailable, error.ErrorCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(0, f.TokenCalls);
        await AssertClosedAsync(f.LastCallback!);
    }

    internal static async Task AssertClosedAsync(Uri uri)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(uri, Ct));
    }
}
