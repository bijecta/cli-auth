using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.Sample.Cli.Examples;

// Compiled consumer examples. Browser and device login are implemented; refresh is deferred.
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

    // Persistent plaintext storage: both the profile and the store select it explicitly.
    internal static ICliAuthenticator CreatePlaintextAuthenticator(string storageDirectory) => new CliAuthenticator(
        [new OidcProfile("example.my-cli", "work", new Uri("https://id.example.test"), "my-cli",
            ["api.read", "offline_access"], persistence: PersistenceMode.Plaintext)],
        new FileTokenStore(storageDirectory));

    internal static Task<IReadOnlyList<ProfileSessionStatus>> ListSessionsAsync(ICliAuthenticator auth, CancellationToken ct) =>
        auth.ListSessionsAsync(ct);

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
