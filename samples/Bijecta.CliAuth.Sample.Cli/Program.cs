using System.CommandLine;
using System.Text.Json;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

var root = new RootCommand("Bijecta.CliAuth development sample. Storage is explicitly selected: process memory for demo, plaintext files for the persistent commands.");
var issuer = new Option<string>("--issuer") { Required = true, Description = "Exact OIDC issuer." };
var clientId = new Option<string>("--client-id") { Required = true, Description = "Registered native public client ID." };
var scopes = new Option<string[]>("--scope") { Description = "Repeat for each requested scope; openid is always included." };
var port = new Option<int>("--callback-port") { Description = "Registered loopback port; default 0 selects a dynamic port." };
var allowHttp = new Option<bool>("--allow-loopback-http") { Description = "Allow HTTP for an IP-literal loopback development issuer only." };
var flow = new Option<string>("--flow") { DefaultValueFactory = _ => "browser", Description = "Login flow: browser or device." };
var storageDir = new Option<string>("--storage-dir")
{
    Required = true,
    Description = "Absolute directory for plaintext session files. Tokens are stored unencrypted."
};
var profiles = new Option<string[]>("--profile")
{
    DefaultValueFactory = _ => ["default"],
    Description = "Profile name; repeat to list several profiles sharing the issuer and client."
};

// Every command that reads a stored session must describe the same profile: the session key covers it.
OidcProfile Profile(ParseResult parse, string name, PersistenceMode persistence)
{
    if (!Uri.TryCreate(parse.GetValue(issuer), UriKind.Absolute, out var issuerUri))
    {
        throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
    }
    return new OidcProfile("bijecta-sample", name, issuerUri, parse.GetValue(clientId)!, parse.GetValue(scopes),
        persistence, new BrowserCallbackOptions(parse.GetValue(port)), parse.GetValue(allowHttp));
}

LoginOptions Options(ParseResult parse) => parse.GetValue(flow) switch
{
    "browser" => new LoginOptions(),
    "device" => new LoginOptions(LoginFlow.Device, (prompt, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"Open {prompt.VerificationUri}");
        Console.WriteLine($"Enter code: {prompt.UserCode}");
        Console.WriteLine($"Waiting for authorization (expires {prompt.ExpiresAtUtc:u})...");
        return Task.CompletedTask;
    }),
    _ => throw new CliAuthException(AuthErrorCode.InvalidConfiguration)
};

// Explicit plaintext selection on both sides: the profiles require Plaintext and the store declares it.
(CliAuthenticator Auth, string Profile) Persistent(ParseResult parse, bool single = true)
{
    var names = parse.GetValue(profiles)!;
    if (single && names.Length != 1) { throw new CliAuthException(AuthErrorCode.InvalidConfiguration); }
    var configured = names.Select(name => Profile(parse, name, PersistenceMode.Plaintext)).ToArray();
    return (new CliAuthenticator(configured, new FileTokenStore(parse.GetValue(storageDir)!)), names[0]);
}

object Describe(SessionStatus status) =>
    new { Kind = status.Kind.ToString(), status.ExpiresAtUtc, status.Subject, Issuer = status.Issuer?.OriginalString };

void Add(Command command, params Option[] options)
{
    foreach (var option in options) { command.Options.Add(option); }
    root.Subcommands.Add(command);
}

var demo = new Command("demo", "Browser or device login, cached token acquisition, status, and local logout in one process.");
Add(demo, flow, issuer, clientId, scopes, port, allowHttp);
demo.SetAction((parse, cancellationToken) => Run(async () =>
{
    var auth = new CliAuthenticator([Profile(parse, "demo", PersistenceMode.MemoryOnly)], new MemoryTokenStore());
    var options = Options(parse);
    Console.WriteLine("This session exists only for this process.");
    if (options.Flow == LoginFlow.Browser) { Console.WriteLine("Opening your browser."); }
    var login = await auth.LoginAsync("demo", options, cancellationToken);

    Console.WriteLine(JsonSerializer.Serialize(new { action = "login", Persistence = login.Persistence.ToString(), login.ExpiresAtUtc, login.GrantedScopes }));
    var token = await auth.GetAccessTokenAsync("demo", cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "acquire", token.ExpiresAtUtc, token.GrantedScopes }));
    var status = await auth.GetStatusAsync("demo", cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "status", Kind = status.Kind.ToString() }));
    var logout = await auth.LogoutAsync("demo", new LogoutOptions(), cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "logout", logout.LocalSessionRemoved, RevocationOutcome = logout.RevocationOutcome.ToString() }));
    Console.WriteLine("Local session removed. Your provider's browser session may still be active.");
    return 0;
}, cancellationToken));

var login = new Command("login", "Log in and persist the session in plaintext files for later commands.");
Add(login, flow, storageDir, profiles, issuer, clientId, scopes, port, allowHttp);
login.SetAction((parse, cancellationToken) => Run(async () =>
{
    var (auth, profile) = Persistent(parse);
    var options = Options(parse);
    Console.WriteLine("Warning: tokens will be stored unencrypted in the storage directory.");
    if (options.Flow == LoginFlow.Browser) { Console.WriteLine("Opening your browser."); }
    var result = await auth.LoginAsync(profile, options, cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "login", profile, Persistence = result.Persistence.ToString(), result.Subject, result.ExpiresAtUtc, result.GrantedScopes }));
    return 0;
}, cancellationToken));

var status = new Command("status", "Show one profile's stored session without network access.");
Add(status, storageDir, profiles, issuer, clientId, scopes, port, allowHttp);
status.SetAction((parse, cancellationToken) => Run(async () =>
{
    var (auth, profile) = Persistent(parse);
    var result = await auth.GetStatusAsync(profile, cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "status", profile, Status = Describe(result) }));
    return 0;
}, cancellationToken));

var list = new Command("list", "List every given profile and whether it has a stored session.");
Add(list, storageDir, profiles, issuer, clientId, scopes, port, allowHttp);
list.SetAction((parse, cancellationToken) => Run(async () =>
{
    var (auth, _) = Persistent(parse, single: false);
    foreach (var entry in await auth.ListSessionsAsync(cancellationToken))
    {
        Console.WriteLine(entry.Error is { } error
            ? JsonSerializer.Serialize(new { profile = entry.ProfileName, Error = error.ToString() })
            : JsonSerializer.Serialize(new { profile = entry.ProfileName, Status = Describe(entry.Status!) }));
    }
    return 0;
}, cancellationToken));

var logout = new Command("logout", "Remove one profile's stored session, including a corrupt or newer-version one.");
Add(logout, storageDir, profiles, issuer, clientId, scopes, port, allowHttp);
logout.SetAction((parse, cancellationToken) => Run(async () =>
{
    var (auth, profile) = Persistent(parse);
    var result = await auth.LogoutAsync(profile, new LogoutOptions(), cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new { action = "logout", profile, result.LocalSessionRemoved, RevocationOutcome = result.RevocationOutcome.ToString() }));
    return 0;
}, cancellationToken));

return await root.Parse(args).InvokeAsync();

static async Task<int> Run(Func<Task<int>> action, CancellationToken cancellationToken)
{
    try
    {
        return await action();
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Authentication cancelled.");
        return 130;
    }
    catch (CliAuthException error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ErrorCode = error.ErrorCode.ToString(), error.ProviderErrorCode, error.Message, error.CorrelationId }));
        if (error.ErrorCode is AuthErrorCode.StorageCorrupt)
        {
            Console.Error.WriteLine("The stored session cannot be used. Run login again to replace it, or logout to remove it.");
        }
        else if (error.ErrorCode is AuthErrorCode.UnsupportedSchema)
        {
            Console.Error.WriteLine("The stored session was written by a newer version. Upgrade, or run logout to remove it.");
        }
        else if (error.ErrorCode is AuthErrorCode.StorageInsecure)
        {
            Console.Error.WriteLine("The storage directory is not private: use a local directory writable only by you (Unix: chmod 700).");
        }
        return error.ErrorCode switch
        {
            AuthErrorCode.InvalidConfiguration or AuthErrorCode.UnsupportedCapability or
                AuthErrorCode.DestinationNotAllowed or AuthErrorCode.InvalidRequest => 2,
            AuthErrorCode.ReauthenticationRequired or AuthErrorCode.RefreshOutcomeUnknown => 3,
            AuthErrorCode.AccessDenied or AuthErrorCode.BrowserUnavailable or
                AuthErrorCode.CallbackRejected or AuthErrorCode.LoginTimedOut => 4,
            AuthErrorCode.StorageUnavailable or AuthErrorCode.StorageCorrupt or AuthErrorCode.UnsupportedSchema or
                AuthErrorCode.StorageInsecure => 5,
            AuthErrorCode.DiscoveryFailed or AuthErrorCode.NetworkFailure or AuthErrorCode.ProtocolFailure or
                AuthErrorCode.TokenLifetimeTooShort => 6,
            AuthErrorCode.SessionBusy or AuthErrorCode.OperationSuperseded => 7,
            _ => 1
        };
    }
}
