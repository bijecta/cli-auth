using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Sample.Cli.Examples;

// Compiled consumer examples. Browser login is implemented; device login and refresh are deferred.
internal static class CoreUsage
{
    internal static OidcProfile CreateDevelopmentProfile() => new(
        applicationId: "example.my-cli",
        profileName: "work",
        issuer: new Uri("https://id.example.test"),
        clientId: "my-cli",
        scopes: ["api.read", "offline_access"],
        persistence: PersistenceMode.MemoryOnly);

    internal static MemoryTokenStore CreateDevelopmentStore() => new();

    internal static ICliAuthenticator CreateDevelopmentAuthenticator()
    {
        var store = CreateDevelopmentStore();
        return new CliAuthenticator([CreateDevelopmentProfile()], store, sessionLocks: store);
    }

    internal static Task<LoginResult> BrowserLoginAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.LoginAsync("work", new LoginOptions(LoginFlow.Browser), ct);

    internal static Task<LoginResult> DeviceLoginAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.LoginAsync("work", new LoginOptions(LoginFlow.Device,
            deviceAuthorizationHandler: (prompt, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Intentional user presentation belongs to the CLI, not library diagnostics.
                Console.WriteLine($"Visit {prompt.VerificationUri} and enter {prompt.UserCode}.");
                return Task.CompletedTask;
            }), ct);

    internal static Task<SessionStatus> LocalStatusAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.GetStatusAsync("work", ct);

    internal static Task<AccessToken> SilentTokenAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.GetAccessTokenAsync("work", ct);

    internal static Task<LogoutResult> LocalLogoutAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.LogoutAsync("work", new LogoutOptions(), ct);
}
