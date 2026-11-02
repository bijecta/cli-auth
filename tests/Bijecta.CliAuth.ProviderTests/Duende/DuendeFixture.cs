using System.Security.Cryptography;
using Bijecta.CliAuth.ProviderTests.Harness;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.ProviderTests.Duende;

/// <summary>
/// A disposable Duende IdentityServer, hosted in the test process on an ephemeral IPv4 loopback port. Clients, the
/// test user, and the signing key are created in memory for each run. Development and testing need no Duende licence;
/// the host is never packaged or redistributed (ADR 0010).
/// </summary>
public sealed class DuendeFixture : IAsyncLifetime
{
    internal const string BrowserClientId = "cli-browser";
    internal const string ConsentClientId = "cli-consent";
    internal const string DeviceClientId = "cli-device";
    internal const string UserName = "integration-user";
    internal static readonly string[] Scopes = ["profile", "offline_access"];

    private WebApplication? _app;

    internal string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    internal string SubjectId { get; } = Guid.NewGuid().ToString("N");
    internal Uri Issuer { get; private set; } = null!;

    internal ProviderSettings Settings => new(DuendeScenarios.Provider, Issuer, BrowserClientId, DeviceClientId,
        ConsentClientId, UserName, Password, callbackPort: 0, Scopes, new Dictionary<string, string>());

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(DuendeFixture).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Host logs can contain authorization URLs, codes and tokens; failures are reported by the tests instead.
        builder.Logging.ClearProviders();
        var signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) };
        builder.Services.AddIdentityServer(options =>
            {
                // A per-run key is configured explicitly, so nothing is written to disk.
                options.KeyManagement.Enabled = false;
                // The default SameSite=None cookie needs HTTPS, which Chromium enforces; this host is loopback HTTP.
                options.Authentication.CookieSameSiteMode = SameSiteMode.Lax;
                options.UserInteraction.LoginUrl = DuendeUi.LoginPath;
                options.UserInteraction.ConsentUrl = DuendeUi.ConsentPath;
                options.UserInteraction.DeviceVerificationUrl = DuendeUi.DevicePath;
            })
            .AddSigningCredential(signingKey, SecurityAlgorithms.RsaSha256)
            .AddInMemoryIdentityResources([new IdentityResources.OpenId(), new IdentityResources.Profile()])
            .AddInMemoryApiScopes([])
            .AddInMemoryClients(Clients())
            // RFC 8252 §7.3: a client registered with http://127.0.0.1 and PKCE accepts any port and path at runtime.
            .AddAppAuthRedirectUriValidator();
        builder.Services.AddSingleton(new DuendeUi.TestUser(UserName, Password, SubjectId));

        var app = builder.Build();
        app.UseIdentityServer();
        DuendeUi.Map(app);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await app.StartAsync(startup.Token);
        }
        catch (Exception error)
        {
            await app.DisposeAsync();
            throw new InvalidOperationException($"The Duende IdentityServer host did not start ({error.GetType().Name}).");
        }
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Issuer = new Uri(new Uri(address).GetLeftPart(UriPartial.Authority));
    }

    private static Client[] Clients()
    {
        Client Browser(string clientId, bool requireConsent) => new()
        {
            ClientId = clientId,
            RequireClientSecret = false,
            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,
            AllowPlainTextPkce = false,
            RedirectUris = { "http://127.0.0.1" },
            AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile },
            AllowOfflineAccess = true,
            RequireConsent = requireConsent,
            AllowRememberConsent = false,
            // Keycloak-like lifetimes keep the refresh test's clock shift inside the ID token's lifetime.
            AccessTokenLifetime = 300,
            // Strict rotation: each refresh token is single-use, and the default service rejects a consumed one.
            RefreshTokenUsage = TokenUsage.OneTimeOnly,
        };
        return
        [
            Browser(BrowserClientId, requireConsent: false),
            Browser(ConsentClientId, requireConsent: true),
            new()
            {
                ClientId = DeviceClientId,
                RequireClientSecret = false,
                AllowedGrantTypes = GrantTypes.DeviceFlow,
                AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile },
                AllowOfflineAccess = true,
                AccessTokenLifetime = 300,
                RefreshTokenUsage = TokenUsage.OneTimeOnly,
            },
        ];
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(shutdown.Token);
        }
        finally
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
