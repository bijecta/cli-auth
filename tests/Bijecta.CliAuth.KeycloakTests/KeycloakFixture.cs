using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bijecta.CliAuth.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Testcontainers.Keycloak;

namespace Bijecta.CliAuth.KeycloakTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>
{
    public const string Name = "Keycloak browser integration";
}

// A fresh imported realm and browser per test collection. No developer fixture or credentials are used.
public sealed class KeycloakFixture : IAsyncLifetime
{
    internal const string Image = "quay.io/keycloak/keycloak:26.8.1";
    internal const string ClientId = "bijecta-cli";
    internal const string AcceptClientId = "consent-accept";
    internal const string DenyClientId = "consent-deny";
    internal const string UserName = "integration-user";
    private KeycloakContainer? _container;
    private readonly string _adminPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    internal string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    internal int CallbackPort { get; private set; }
    internal Uri Issuer { get; private set; } = null!;
    internal IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser fixture is not initialized.");

    public async ValueTask InitializeAsync()
    {
        var stage = "realm configuration";
        try
        {
            // Hold the callback port while Docker chooses its mapped ports, then release it for Core.
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Server.ExclusiveAddressUse = true;
            reservation.Start();
            CallbackPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var realm = BuildRealm();
            stage = "container startup";
            _container = new KeycloakBuilder(Image)
                .WithUsername("integration-admin")
                .WithPassword(_adminPassword)
                .WithLogger(NullLogger.Instance)
                .WithCommand("--import-realm")
                .WithResourceMapping(Encoding.UTF8.GetBytes(realm.ToJsonString()), "/opt/keycloak/data/import/cliauth-realm.json")
                .Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _container.StartAsync(startup.Token);
            // This fixture deliberately targets local Docker-compatible runtimes with published loopback ports.
            var host = _container.Hostname;
            if (host != "localhost" && (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip)))
            {
                throw new InvalidOperationException("A local container runtime is required for loopback HTTP development tests.");
            }
            Issuer = new Uri($"http://127.0.0.1:{_container.GetMappedPublicPort(8080)}/realms/cliauth");
            stage = "browser startup";
            _playwright = await Playwright.CreateAsync().WaitAsync(startup.Token);
            _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 30_000 }).WaitAsync(startup.Token);
        }
        catch (Exception error)
        {
            var startupDetails = "";
            if (stage == "container startup" && _container is not null)
            {
                try
                {
                    // No browser/authentication requests have run at this point. Keep bounded startup
                    // diagnostics, with both generated credentials removed before reporting anything.
                    var logs = await _container.GetLogsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    var text = (logs.Stdout + logs.Stderr).Replace(Password, "[redacted]", StringComparison.Ordinal)
                        .Replace(_adminPassword, "[redacted]", StringComparison.Ordinal);
                    startupDetails = "\nContainer startup output:\n" + text[^Math.Min(text.Length, 4096)..];
                }
                catch { }
            }
            try { await DisposeAsync(); } catch { }
            // Upstream errors can embed commands, URLs, or credentials. Report only the setup stage/type.
            throw new InvalidOperationException($"Integration fixture failed during {stage} ({error.GetType().Name}). " +
                "Check container-runtime access, image availability, and the installed Playwright Chromium browser." + startupDetails);
        }
    }

    internal OidcProfile Profile(string clientId = ClientId) => new("cliauth-integration", "work", Issuer, clientId,
        clientId == ClientId ? null : ["profile"], PersistenceMode.MemoryOnly,
        new BrowserCallbackOptions(CallbackPort), allowLoopbackHttp: true);

    private JsonObject BuildRealm()
    {
        var realm = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "realm.template.json")))!.AsObject();
        var client = realm["clients"]![0]!.AsObject();
        client["redirectUris"] = new JsonArray(JsonValue.Create($"http://127.0.0.1:{CallbackPort}/callback/"));
        foreach (var id in new[] { AcceptClientId, DenyClientId })
        {
            var consentClient = client.DeepClone().AsObject();
            consentClient["clientId"] = id;
            consentClient["consentRequired"] = true;
            consentClient["optionalClientScopes"] = new JsonArray(JsonValue.Create("profile"));
            realm["clients"]!.AsArray().Add((JsonNode)consentClient);
        }
        realm["users"] = new JsonArray(new JsonObject
        {
            ["username"] = UserName,
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["firstName"] = "Integration",
            ["lastName"] = "User",
            ["email"] = "integration@example.test",
            ["requiredActions"] = new JsonArray(),
            ["credentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "password",
                ["value"] = Password,
                ["temporary"] = false
            })
        });
        return realm;
    }

    public async ValueTask DisposeAsync()
    {
        var failed = false;
        if (_browser is not null)
        {
            try { await _browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { failed = true; }
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        if (_container is not null)
        {
            try { await _container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); } catch { failed = true; }
            _container = null;
        }
        if (failed) { throw new InvalidOperationException("Integration fixture cleanup failed; check the test container runtime."); }
    }
}
