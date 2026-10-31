using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

// Disposable cross-process test protocol: one command per stdin line, one result line per command.
// A real CliAuthenticator and FileTokenStore run against a fake protocol issuing synthetic credentials;
// no raw paths are emitted.
using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = watchdog.Token;
// Console.In reads ignore cancellation once blocked, so the watchdog ends the process rather than relying on ct.
ct.Register(() =>
{
    Console.Error.WriteLine("Session helper watchdog expired.");
    Environment.Exit(2);
});
var stageBeforePublish = false;
var store = new FileTokenStore(args[0], async (_, token) =>
{
    if (!stageBeforePublish) { return; }
    // The staging file is written and flushed; hold the commit lease here until the parent kills this process.
    Console.WriteLine("STAGED");
    await Task.Delay(Timeout.Infinite, token);
});
var protocol = new FakeProtocol();
var profile = new OidcProfile("bijecta-process-tests", "work", new Uri("https://issuer.example.test"),
    "process-client", ["api.read"], PersistenceMode.Plaintext);
var auth = new CliAuthenticator([profile], store, protocol, store, sessionLockTimeout: TimeSpan.FromSeconds(10));

Console.WriteLine("READY");
while (await Console.In.ReadLineAsync(ct) is { } line)
{
    var command = line.Split(' ', 2);
    try
    {
        switch (command[0])
        {
            case "login" or "login-pause" or "login-stage" or "login-expiring":
                protocol.Subject = command[1];
                protocol.Pause = command[0] == "login-pause";
                protocol.Expiring = command[0] == "login-expiring";
                stageBeforePublish = command[0] == "login-stage";
                var login = await auth.LoginAsync("work", new LoginOptions(), ct);
                Console.WriteLine($"LOGGED-IN {login.Subject}");
                break;
            case "status":
                var status = await auth.GetStatusAsync("work", ct);
                Console.WriteLine($"STATUS {status.Kind} {status.Subject ?? "-"}");
                break;
            case "token" or "token-pause":
                protocol.Pause = command[0] == "token-pause";
                Console.WriteLine($"TOKEN {(await auth.GetAccessTokenAsync("work", ct)).Value}");
                break;
            case "logout":
                Console.WriteLine($"LOGGED-OUT {(await auth.LogoutAsync("work", new LogoutOptions(), ct)).LocalSessionRemoved}");
                break;
            default:
                Console.WriteLine("UNKNOWN");
                break;
        }
    }
    catch (CliAuthException error)
    {
        Console.WriteLine($"ERROR {error.ErrorCode}");
    }
}

internal sealed class FakeProtocol : IAuthenticationProtocol
{
    internal string Subject { get; set; } = "";
    internal bool Pause { get; set; }
    // Issues a token already inside the 60-second expiry margin, so the next acquisition must refresh.
    internal bool Expiring { get; set; }

    public async Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options, ISystemBrowser browser, CancellationToken ct)
    {
        // Stands in for the user signing in: no session lease is held while the parent races other processes.
        await PauseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return new ValidatedLogin(new AccessToken("synthetic-access-" + Subject, now.AddSeconds(Expiring ? 30 : 3600), profile.Scopes,
            obtainedAtUtc: now), Subject, profile.Issuer, profile.ClientId, "synthetic-refresh-" + Subject);
    }

    public async Task<ValidatedRefresh> RefreshAsync(OidcProfile profile, string refreshToken, string subject,
        IReadOnlyList<string> grantedScopes, CancellationToken ct)
    {
        // Unlike login, refresh runs with the session lease held: other processes wait for it.
        await PauseAsync(ct);
        // The process id shows which process performed the refresh; the rotated refresh token proves the commit.
        var now = DateTimeOffset.UtcNow;
        return new ValidatedRefresh(new AccessToken($"synthetic-refreshed-{Environment.ProcessId}", now.AddHours(1), grantedScopes,
            obtainedAtUtc: now), $"synthetic-rotated-{Environment.ProcessId}");
    }

    private async Task PauseAsync(CancellationToken ct)
    {
        if (!Pause) { return; }
        Console.WriteLine("ENTERED");
        if (await Console.In.ReadLineAsync(ct) != "resume") { throw new OperationCanceledException(); }
    }

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct) =>
        Task.FromResult(RevocationOutcome.NotRequested);
}
