using System.Net;
using System.Net.Sockets;
using System.Text;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class LoopbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing_state")]
    [InlineData("wrong_state")]
    [InlineData("duplicate_state")]
    [InlineData("duplicate_code")]
    [InlineData("code_and_error")]
    [InlineData("wrong_issuer")]
    [InlineData("malformed_percent")]
    [InlineData("invalid_utf8")]
    [InlineData("forged_error")]
    [InlineData("wrong_path")]
    [InlineData("wrong_host")]
    [InlineData("fragment")]
    [InlineData("post")]
    [InlineData("oversized_target")]
    [InlineData("oversized_headers")]
    public async Task InvalidCallbackDoesNotConsumeTransaction(string scenario)
    {
        using var f = new BrowserFixture();
        f.OnBrowser = async (_, ct) =>
        {
            var state = Uri.EscapeDataString(f.Front!["state"]);
            var query = $"state={state}&code=synthetic-code";
            query = scenario switch
            {
                "missing_state" => "code=synthetic-code",
                "wrong_state" => "state=wrong&code=synthetic-code",
                "duplicate_state" => query + "&state=" + state,
                "duplicate_code" => query + "&code=second",
                "code_and_error" => query + "&error=access_denied",
                "wrong_issuer" => query + "&iss=https%3A%2F%2Fother.example.test",
                "malformed_percent" => query + "&extra=%Q0",
                "invalid_utf8" => query + "&extra=%ff",
                "forged_error" => "state=wrong&error=access_denied",
                "fragment" => query + "#fragment",
                "oversized_target" => query + "&extra=" + new string('a', 8192),
                _ => query
            };
            var path = scenario == "wrong_path" ? "/other/" : "/callback/";
            var host = scenario == "wrong_host" ? "localhost:" + f.LastCallback!.Port : f.LastCallback!.Authority;
            var request = $"{(scenario == "post" ? "POST" : "GET")} {path}?{query} HTTP/1.1\r\nHost: {host}\r\n";
            if (scenario == "oversized_headers")
            {
                request += "X-Large: " + new string('a', 17000) + "\r\n";
            }

            var rejected = await SendRaw(f.LastCallback, request + "\r\n", ct);
            if (scenario != "oversized_headers" || rejected.Length != 0)
            {
                Assert.Contains("400 Bad Request", rejected);
                Assert.Contains("Cache-Control: no-store", rejected);
            }
            Assert.DoesNotContain("synthetic-code", rejected);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await client.GetAsync(f.Callback(), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        };
        await f.Auth().LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(1, f.TokenCalls);
    }

    [Fact]
    public async Task MatchingStateDenialFailsBeforeExchange()
    {
        using var f = new BrowserFixture();
        f.OnBrowser = async (_, ct) =>
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await client.GetAsync(f.Callback("state=" + f.Front!["state"] + "&error=access_denied&error_description=secret"), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.AccessDenied, error.ErrorCode);
        Assert.Equal("access_denied", error.ProviderErrorCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(0, f.TokenCalls);
        await BrowserLoginTests.AssertClosedAsync(f.LastCallback!);
    }

    [Fact]
    public async Task TwentyInvalidRequestsExhaustTransactionBudget()
    {
        using var f = new BrowserFixture();
        f.OnBrowser = async (_, ct) =>
        {
            for (var i = 0; i < 20; i++)
            {
                await SendRaw(f.LastCallback!, $"GET /callback/?state=wrong&code=wrong HTTP/1.1\r\nHost: {f.LastCallback!.Authority}\r\n\r\n", ct);
            }
        };
        Assert.Equal(AuthErrorCode.CallbackRejected, (await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact]
    public async Task FixedPortCollisionFailsBeforeBrowserLaunch()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        using var f = new BrowserFixture();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        Assert.Equal(AuthErrorCode.CallbackRejected, (await Assert.ThrowsAsync<CliAuthException>(() => f.Auth(profile: f.Profile(port)).LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTotalDeadlineCloseListener(bool deadline)
    {
        using var f = new BrowserFixture();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnBrowser = (_, _) =>
        {
            opened.TrySetResult();
            return Task.CompletedTask;
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var login = f.Auth().LoginAsync("work", new LoginOptions(), cancel.Token);
        await opened.Task.WaitAsync(Ct);
        if (deadline)
        {
            f.Time.Advance(TimeSpan.FromSeconds(180));
            Assert.Equal(AuthErrorCode.LoginTimedOut, (await Assert.ThrowsAsync<CliAuthException>(() => login)).ErrorCode);
        }
        else
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        }

        Assert.Equal(0, f.TokenCalls);
        await BrowserLoginTests.AssertClosedAsync(f.LastCallback!);
    }

    internal static async Task<string> SendRaw(Uri uri, string request, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, uri.Port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            return await reader.ReadToEndAsync(ct);
        }
        catch (IOException)
        {
            return string.Empty; // A peer reset while rejecting oversized headers is also a closed connection.
        }
    }
}
