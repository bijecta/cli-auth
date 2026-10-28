using System.Net;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class TokenFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SigningKeyReloadTimeCountsAgainstUsableTokenLifetime()
    {
        using var f = new BrowserFixture { Scenario = "rotation" };
        f.ChangeTokens = t => { t["expires_in"] = 75; return t; };
        f.ChangeJwks = keys =>
        {
            if (f.KeyCalls == 2) { f.Time.Advance(TimeSpan.FromSeconds(20)); }
            return keys;
        };
        var auth = f.Auth();
        Assert.Equal(AuthErrorCode.TokenLifetimeTooShort, (await Assert.ThrowsAsync<CliAuthException>(
            () => auth.LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task UnknownKeyGetsOneReloadAndNoSecondTokenExchange()
    {
        using var f = new BrowserFixture { Scenario = "unknown_kid" };
        var auth = f.Auth();
        Assert.Equal(AuthErrorCode.ProtocolFailure, (await Assert.ThrowsAsync<CliAuthException>(
            () => auth.LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(2, f.KeyCalls);
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Theory]
    [InlineData("no_keys")]
    [InlineData("symmetric")]
    [InlineData("encryption")]
    public async Task UnusableSigningKeysFailBeforeBrowser(string scenario)
    {
        using var f = new BrowserFixture();
        f.ChangeJwks = _ => scenario switch
        {
            "no_keys" => new { keys = Array.Empty<object>() },
            "symmetric" => new { keys = new[] { new { kty = "oct", k = "c3ludGhldGlj", alg = "HS256" } } },
            _ => new { keys = new[] { new { kty = "RSA", use = "enc" } } }
        };
        Assert.Equal(AuthErrorCode.DiscoveryFailed, (await Assert.ThrowsAsync<CliAuthException>(
            () => f.Auth().LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Fact]
    public async Task TokenDeadlineDoesNotRetryOneUseCode()
    {
        using var f = new BrowserFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnTokenRequest = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        };
        var auth = f.Auth();
        var login = auth.LoginAsync("work", new LoginOptions(), Ct);
        await entered.Task.WaitAsync(Ct);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AuthErrorCode.NetworkFailure, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("error")]
    public async Task InvalidTokenResponseFailsWithoutRetryOrRawDiagnostics(string scenario)
    {
        using var f = new BrowserFixture();
        f.OnTokenRequest = _ => Task.FromResult(scenario switch
        {
            "duplicate" => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"access_token\":\"secret-one\",\"access_token\":\"secret-two\"}") },
            "redirect" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            { Headers = { Location = new Uri("https://attacker.example.test/token") } },
            "oversize" => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('a', 256 * 1024 + 1)) },
            _ => BrowserFixture.Json(new { error = "invalid_grant", error_description = "secret-provider-description" }, HttpStatusCode.BadRequest)
        });
        var auth = f.Auth();
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(scenario == "error" ? "invalid_grant" : null, error.ProviderErrorCode);
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", Ct)).Kind);
    }
}
