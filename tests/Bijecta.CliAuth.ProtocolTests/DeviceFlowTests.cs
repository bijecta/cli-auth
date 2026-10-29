using System.Net;
using System.Net.Sockets;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class DeviceFlowTests
{
    private static LoginOptions Options(Func<DeviceAuthorizationPrompt, CancellationToken, Task>? present = null) =>
        new(LoginFlow.Device, present ?? ((_, _) => Task.CompletedTask));
    private static CliAuthenticator Auth(BrowserFixture f, OidcProfile? profile = null) =>
        f.Auth(new AuthenticationProtocol(f.Time, f), profile);

    [Theory(Timeout = 15_000)]
    [InlineData("valid")]
    [InlineData("valid_at_hash")]
    [InlineData("valid_azp")]
    [InlineData("rotation")]
    [InlineData("es256")]
    public async Task DeviceOnlyProviderPresentsSafePromptAndCommitsValidatedSession(string scenario)
    {
        using var f = new BrowserFixture { Device = true, Scenario = scenario };
        f.ChangeMetadata = m =>
        {
            m.Remove("authorization_endpoint");
            m.Remove("response_types_supported");
            m["code_challenge_methods_supported"] = new[] { "plain" };
            m["require_pushed_authorization_requests"] = true;
            return m;
        };
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var auth = Auth(f, f.Profile(((IPEndPoint)occupied.LocalEndpoint).Port));
        DeviceAuthorizationPrompt? shown = null;
        var login = auth.LoginAsync("work", Options((prompt, _) => { shown = prompt; return Task.CompletedTask; }), TestContext.Current.CancellationToken);
        Assert.NotNull(shown);
        Assert.Equal("USER-CODE", shown.UserCode);
        Assert.Equal(BrowserFixture.Issuer + "/verify", shown.VerificationUri.AbsoluteUri);
        Assert.NotNull(shown.VerificationUriComplete);
        Assert.Equal(f.Time.GetUtcNow().AddMinutes(10), shown.ExpiresAtUtc);
        Assert.DoesNotContain("USER-CODE", shown.ToString());
        Assert.Equal(0, f.TokenCalls);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        var result = await login;
        Assert.Equal("test-subject", result.Subject);
        Assert.Equal(BrowserFixture.ClientId, f.DeviceRequest!["client_id"]);
        Assert.Equal("openid profile", f.DeviceRequest["scope"]);
        Assert.Equal(new[] { "client_id", "scope" }, f.DeviceRequest.Keys.Order().ToArray());
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", f.Exchange!["grant_type"]);
        Assert.Equal("secret-device-code", f.Exchange["device_code"]);
        Assert.Equal(new[] { "client_id", "device_code", "grant_type" }, f.Exchange.Keys.Order().ToArray());
        Assert.Equal(0, f.BrowserCalls);
        Assert.Null(f.LastCallback);
        Assert.Equal(scenario == "rotation" ? 2 : 1, f.KeyCalls);
        Assert.Equal("synthetic-access", (await auth.GetAccessTokenAsync("work", TestContext.Current.CancellationToken)).Value);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
        await auth.LogoutAsync("work", new LogoutOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("missing", AuthErrorCode.UnsupportedCapability)]
    [InlineData("grant", AuthErrorCode.UnsupportedCapability)]
    [InlineData("http", AuthErrorCode.DestinationNotAllowed)]
    [InlineData("origin", AuthErrorCode.DestinationNotAllowed)]
    public async Task MissingOrUntrustedCapabilityFailsBeforePresentation(string scenario, AuthErrorCode expected)
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeMetadata = m =>
        {
            switch (scenario)
            {
                case "missing": m.Remove("device_authorization_endpoint"); break;
                case "grant": m["grant_types_supported"] = new[] { "authorization_code" }; break;
                case "http": m["device_authorization_endpoint"] = "http://issuer.example.test/device"; break;
                default: m["device_authorization_endpoint"] = "https://other.example.test/device"; break;
            }
            return m;
        };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => Auth(f).LoginAsync("work", Options((_, _) => throw new InvalidOperationException()), TestContext.Current.CancellationToken));
        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(0, f.DeviceCalls);
        Assert.Equal(0, f.TokenCalls);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("device_code", "")]
    [InlineData("user_code", "")]
    [InlineData("verification_uri", "http://issuer.example.test/verify")]
    [InlineData("verification_uri", "https://attacker.example.test/verify")]
    [InlineData("verification_uri_complete", "https://issuer.example.test/verify#secret")]
    [InlineData("expires_in", "600")]
    [InlineData("interval", "5")]
    public async Task InvalidAuthorizationResponseIsNeverPresented(string field, string value)
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeDevice = d => { d[field] = value; return d; };
        var auth = Auth(f);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", Options((_, _) => throw new InvalidOperationException()), TestContext.Current.CancellationToken));
        Assert.Contains(error.ErrorCode, new[] { AuthErrorCode.ProtocolFailure, AuthErrorCode.DestinationNotAllowed });
        Assert.Null(error.InnerException);
        Assert.Equal(0, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("expires_in", 0)]
    [InlineData("expires_in", -1)]
    [InlineData("expires_in", int.MaxValue)]
    [InlineData("interval", 0)]
    [InlineData("interval", -5)]
    public async Task InvalidTimersFailBeforePresentation(string field, int value)
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeDevice = d => { d[field] = value; return d; };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => Auth(f).LoginAsync("work", Options(), TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task PendingAndSlowDownHonorProviderIntervalAndKeepIncreasedDelay()
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeDevice = d => { d["interval"] = 2; d.Remove("verification_uri_complete"); return d; };
        f.OnTokenRequest = _ =>
        {
            var error = f.TokenCalls switch { 1 => "authorization_pending", 2 => "slow_down", 3 => "authorization_pending", _ => "access_denied" };
            return Task.FromResult(BrowserFixture.Json(new { error }, HttpStatusCode.BadRequest));
        };
        var login = Auth(f).LoginAsync("work", Options(present: (p, _) => { Assert.Null(p.VerificationUriComplete); return Task.CompletedTask; }), TestContext.Current.CancellationToken);
        foreach (var seconds in new[] { 2, 2, 7, 7 }) { await f.Time.AdvanceDelayAsync(seconds, TestContext.Current.CancellationToken); }
        Assert.Equal(AuthErrorCode.AccessDenied, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(4, f.TokenCalls);
        Assert.Equal(1, f.DeviceCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task RequestTimeoutDoublesSubsequentDelayAndDoesNotRestartAuthorization()
    {
        using var f = new BrowserFixture { Device = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnTokenRequest = async ct =>
        {
            if (f.TokenCalls == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException();
            }
            return BrowserFixture.Json(new { error = "access_denied" }, HttpStatusCode.BadRequest);
        };
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await f.Time.AdvanceDelayAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(AuthErrorCode.AccessDenied, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(2, f.TokenCalls);
        Assert.Equal(1, f.DeviceCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDeadlineIncludesPresentationAndPolling(bool poll)
    {
        using var f = new BrowserFixture { Device = true };
        f.OnTokenRequest = _ => Task.FromResult(BrowserFixture.Json(new { error = "authorization_pending" }, HttpStatusCode.BadRequest));
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(poll ? null : (_, _) => Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        if (poll) { await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken); }
        f.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AuthErrorCode.LoginTimedOut, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        var callsAtCompletion = f.TokenCalls;
        f.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(callsAtCompletion, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsPresentationOrPollingWithoutSession(bool poll)
    {
        using var f = new BrowserFixture { Device = true };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(poll ? null : (_, _) => Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken)), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        f.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(0, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("wrong_azp")]
    [InlineData("missing_azp")]
    [InlineData("missing_sub")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("wrong_algorithm")]
    [InlineData("bad_signature")]
    [InlineData("wrong_at_hash")]
    public async Task InvalidDeviceIdentityCannotCreateSession(string scenario)
    {
        using var f = new BrowserFixture { Device = true, Scenario = scenario };
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal(AuthErrorCode.ProtocolFailure, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, f.TokenCalls);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("id_token")]
    [InlineData("access_token")]
    [InlineData("expires_in")]
    public async Task MissingTokenFieldsFailClosed(string field)
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeTokens = t => { t.Remove(field); return t; };
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal(AuthErrorCode.ProtocolFailure, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("access_denied", AuthErrorCode.AccessDenied)]
    [InlineData("expired_token", AuthErrorCode.LoginTimedOut)]
    [InlineData("invalid_client", AuthErrorCode.ProtocolFailure)]
    [InlineData("secret-provider-error", AuthErrorCode.ProtocolFailure)]
    public async Task TerminalErrorsStopAndRedactProviderPayload(string providerError, AuthErrorCode expected)
    {
        using var f = new BrowserFixture { Device = true };
        f.OnTokenRequest = _ => Task.FromResult(BrowserFixture.Json(new { error = providerError, error_description = "secret-device-code USER-CODE" }, HttpStatusCode.BadRequest));
        var login = Auth(f).LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => login);
        Assert.Equal(expected, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("USER-CODE", error.ToString());
        Assert.Equal(1, f.TokenCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task LogoutDuringPresentationSupersedesLaterDeviceSuccess()
    {
        using var f = new BrowserFixture { Device = true };
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(async (_, ct) => await auth.LogoutAsync("work", new LogoutOptions(), ct)), TestContext.Current.CancellationToken);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal(AuthErrorCode.OperationSuperseded, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }
    [Theory(Timeout = 15_000)]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "oversize")]
    [InlineData(true, "oversize")]
    [InlineData(false, "redirect")]
    [InlineData(true, "redirect")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(true, "server_pending")]
    public async Task MalformedOrUnexpectedResponsesAreTerminal(bool polling, string scenario)
    {
        using var f = new BrowserFixture { Device = true };
        Task<HttpResponseMessage> Reply(CancellationToken _) => Task.FromResult(scenario switch
        {
            "duplicate" => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"error\":\"authorization_pending\",\"error\":\"slow_down\"}") },
            "oversize" => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('a', 256 * 1024 + 1)) },
            "redirect" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            { Headers = { Location = new Uri("https://attacker.example.test/secret-device-code") } },
            "server_pending" => BrowserFixture.Json(new { error = "authorization_pending" }, HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("secret-device-code malformed") }
        });
        if (polling) { f.OnTokenRequest = Reply; } else { f.OnDeviceRequest = Reply; }
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        if (polling) { await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken); }
        var error = await Assert.ThrowsAsync<CliAuthException>(() => login);
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(polling ? 1 : 0, f.TokenCalls);
        Assert.Equal(1, f.DeviceCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryNetworkFailuresAreTerminalWithoutPollingRetry(bool polling)
    {
        using var f = new BrowserFixture { Device = true };
        Task<HttpResponseMessage> Fail(CancellationToken _) => throw new HttpRequestException("secret-transport-detail");
        if (polling) { f.OnTokenRequest = Fail; } else { f.OnDeviceRequest = Fail; }
        var login = Auth(f).LoginAsync("work", Options(), TestContext.Current.CancellationToken);
        if (polling) { await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken); }
        var error = await Assert.ThrowsAsync<CliAuthException>(() => login);
        Assert.Equal(AuthErrorCode.NetworkFailure, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(polling ? 1 : 0, f.TokenCalls);
        Assert.Equal(1, f.DeviceCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task CancellationInterruptsInFlightPoll()
    {
        using var f = new BrowserFixture { Device = true };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnTokenRequest = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        };
        var auth = Auth(f);
        var login = auth.LoginAsync("work", Options(), cancel.Token);
        await f.Time.AdvanceDelayAsync(5, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(1, f.TokenCalls);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", TestContext.Current.CancellationToken)).Kind);
    }

    [Fact(Timeout = 15_000)]
    public async Task AuthorizationLatencyCountsAgainstProviderExpiry()
    {
        using var f = new BrowserFixture { Device = true };
        f.ChangeDevice = d => { f.Time.Advance(TimeSpan.FromSeconds(20)); d["expires_in"] = 10; return d; };
        var login = Auth(f).LoginAsync("work", Options((_, _) => throw new InvalidOperationException("Should not present expired codes")), TestContext.Current.CancellationToken);
        Assert.Equal(AuthErrorCode.LoginTimedOut, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task HostPresentationFailuresAreSafeAndDoNotStartPolling()
    {
        using var f = new BrowserFixture { Device = true };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => Auth(f).LoginAsync("work",
            Options((_, _) => throw new InvalidOperationException("secret-device-code USER-CODE")), TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(0, f.TokenCalls);
    }

}
