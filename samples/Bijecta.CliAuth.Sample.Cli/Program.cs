using System.CommandLine;
using System.Text.Json;
using Bijecta.CliAuth.Core;

var root = new RootCommand("Bijecta.CliAuth development sample. Storage is explicitly process-local memory.");
var demo = new Command("demo", "Browser login, cached token acquisition, status, and local logout in one process.");
var issuer = new Option<string>("--issuer") { Required = true, Description = "Exact OIDC issuer." };
var clientId = new Option<string>("--client-id") { Required = true, Description = "Registered native public client ID." };
var scopes = new Option<string[]>("--scope") { Description = "Repeat for each requested scope; openid is always included." };
var port = new Option<int>("--callback-port") { Description = "Registered loopback port; default 0 selects a dynamic port." };
var allowHttp = new Option<bool>("--allow-loopback-http") { Description = "Allow HTTP for an IP-literal loopback development issuer only." };
demo.Options.Add(issuer);
demo.Options.Add(clientId);
demo.Options.Add(scopes);
demo.Options.Add(port);
demo.Options.Add(allowHttp);
demo.SetAction(async (parse, cancellationToken) =>
{
    try
    {
        if (!Uri.TryCreate(parse.GetValue(issuer), UriKind.Absolute, out var issuerUri))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        var profile = new OidcProfile("bijecta-sample", "demo", issuerUri, parse.GetValue(clientId)!,
            parse.GetValue(scopes), PersistenceMode.MemoryOnly,
            new BrowserCallbackOptions(parse.GetValue(port)), parse.GetValue(allowHttp));
        var auth = new CliAuthenticator([profile], new MemoryTokenStore());
        Console.WriteLine("Opening your browser. This session exists only for this process.");
        var login = await auth.LoginAsync("demo", new LoginOptions(), cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new { action = "login", Persistence = login.Persistence.ToString(), login.ExpiresAtUtc, login.GrantedScopes }));
        var token = await auth.GetAccessTokenAsync("demo", cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new { action = "acquire", token.ExpiresAtUtc, token.GrantedScopes }));
        var status = await auth.GetStatusAsync("demo", cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new { action = "status", Kind = status.Kind.ToString() }));
        var logout = await auth.LogoutAsync("demo", new LogoutOptions(), cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new { action = "logout", logout.LocalSessionRemoved, RevocationOutcome = logout.RevocationOutcome.ToString() }));
        Console.WriteLine("Local session removed. Your provider's browser session may still be active.");
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Authentication cancelled.");
        return 130;
    }
    catch (CliAuthException error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ErrorCode = error.ErrorCode.ToString(), error.ProviderErrorCode, error.Message, error.CorrelationId }));
        return error.ErrorCode switch
        {
            AuthErrorCode.InvalidConfiguration or AuthErrorCode.UnsupportedCapability => 2,
            AuthErrorCode.AccessDenied => 3,
            AuthErrorCode.LoginTimedOut => 4,
            _ => 1
        };
    }
});
root.Subcommands.Add(demo);
return await root.Parse(args).InvokeAsync();
