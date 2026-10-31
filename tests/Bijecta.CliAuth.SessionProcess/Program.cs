using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

// Disposable cross-process test protocol: one command per stdin line, one result line per command.
// A real CliAuthenticator and FileTokenStore run against a fake protocol issuing synthetic credentials;
// no raw paths are emitted.
using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = watchdog.Token;
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
            case "login" or "login-pause" or "login-stage":
                protocol.Subject = command[1];
                protocol.Pause = command[0] == "login-pause";
                stageBeforePublish = command[0] == "login-stage";
                var login = await auth.LoginAsync("work", new LoginOptions(), ct);
                Console.WriteLine($"LOGGED-IN {login.Subject}");
                break;
            case "status":
                var status = await auth.GetStatusAsync("work", ct);
                Console.WriteLine($"STATUS {status.Kind} {status.Subject ?? "-"}");
                break;
            case "token":
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

    public async Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options, ISystemBrowser browser, CancellationToken ct)
    {
        if (Pause)
        {
            // Stands in for the user signing in: no session lease is held while the parent races other processes.
            Console.WriteLine("ENTERED");
            if (await Console.In.ReadLineAsync(ct) != "resume") { throw new OperationCanceledException(); }
        }
        return new ValidatedLogin(new AccessToken("synthetic-access-" + Subject, DateTimeOffset.UtcNow.AddHours(1), profile.Scopes),
            Subject, profile.Issuer, profile.ClientId, "synthetic-refresh-" + Subject);
    }

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct) =>
        Task.FromResult(RevocationOutcome.NotRequested);
}
