# Duende IdentityServer configuration guide

## Tested with

| | |
|---|---|
| Date tested | 2026-11-02 |
| Provider version, plan, or tier | Duende.IdentityServer 8.0.10, unlicensed development/test mode |
| Library commit | Hosted run pending (milestone 2, phase 1 PR) |
| Hosted run | Pending |
| Result | See the [compatibility table](README.md) |

The tests host IdentityServer in the test process with an in-memory configuration
(`tests/Bijecta.CliAuth.ProviderTests/Duende/`). The settings below match that host.

## Prerequisites

An ASP.NET Core application running Duende IdentityServer 8, with login, consent, and device-verification pages
(IdentityServer ships no UI; Duende's templates provide one).

**Licence.** Duende IdentityServer is source-available, not open source. Duende allows evaluation, development,
test environments, and personal projects without a licence; **production use needs a licence**, or an approved
Community Edition licence for qualifying small organisations, and redistribution needs a separate licence. Check
the current terms: [Duende IdentityServer](https://docs.duendesoftware.com/identityserver) and
[Community Edition](https://duendesoftware.com/products/communityedition). This library only talks to your
server over OAuth/OIDC and does not include or redistribute IdentityServer.

## Register the application

A public native client with no secret, authorization code with PKCE S256, and refresh tokens:

```csharp
new Client
{
    ClientId = "<client-id>",
    RequireClientSecret = false,
    AllowedGrantTypes = GrantTypes.Code,
    RequirePkce = true,
    AllowPlainTextPkce = false,
    RedirectUris = { "http://127.0.0.1" },       // with AddAppAuthRedirectUriValidator, see below
    AllowedScopes = { "openid", "profile" },
    AllowOfflineAccess = true,                    // allows the offline_access scope, so a refresh token is issued
    RefreshTokenUsage = TokenUsage.OneTimeOnly,   // optional: rotation, see below
}
```

## Redirect URIs

By default IdentityServer compares redirect URIs as exact strings, so the port would have to be fixed and
registered. To accept any loopback port, as RFC 8252 §7.3 asks, opt in on the server:

```csharp
builder.Services.AddIdentityServer()
    .AddAppAuthRedirectUriValidator();
```

Then register exactly `http://127.0.0.1` (no port, no path) and keep `RequirePkce = true`. The port and path come
from the request, so the library's `http://127.0.0.1:<port>/callback/` works with `--callback-port 0` (the
default). The validator does not cover `localhost`, `[::1]`, or HTTPS loopback; the library uses `127.0.0.1`.
See [Desktop and CLI redirect URIs](https://docs.duendesoftware.com/identityserver/ai/desktop-cli-redirect-uris/).

Without the validator, register `http://127.0.0.1:<port>/callback/` exactly and pass the same `--callback-port`.

## Browser flow

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login \
  --issuer https://<identityserver> --client-id <client-id> --scope profile --scope offline_access \
  --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- status --issuer https://<identityserver> --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- token --issuer https://<identityserver> --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- logout --issuer https://<identityserver> --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
```

`login` opens the browser at your login page and prints the expiry and granted scopes; `status` reports
`Available`; `token` prints the expiry and scopes, never the token; `logout` removes the local session. Every
command must repeat the same issuer, client, and scopes, because they identify the stored session. These commands
were not run against a hosted server; the automated tests use the same public API.

## Device flow

Use a separate client for the device grant (a single client may list both grant types, but the tests use separate
ones, as for Keycloak). It needs no redirect URI and no PKCE:

```csharp
new Client
{
    ClientId = "<device-client-id>",
    RequireClientSecret = false,
    AllowedGrantTypes = GrantTypes.DeviceFlow,
    AllowedScopes = { "openid", "profile" },
    AllowOfflineAccess = true,
}
```

Run `login --flow device --client-id <device-client-id>` with the same other options. The CLI prints the
verification URI (your `UserInteraction.DeviceVerificationUrl` page) and a user code; the user enters the code,
signs in, and approves on that page.

## Scopes and refresh tokens

- `openid` is always added. A refresh token needs `AllowOfflineAccess = true` on the client and the
  `offline_access` scope in the request (`--scope offline_access`).
- Since IdentityServer 7.0, `RefreshTokenUsage` defaults to `ReUse`: the same refresh token stays valid
  (*None* in the compatibility table).
- `TokenUsage.OneTimeOnly` rotates on every refresh. The default refresh token service rejects a consumed token
  with `invalid_grant` (*Strict*), which the tests verify. To tolerate a retry after a lost response, override
  `AcceptConsumedTokenAsync` in a derived `DefaultRefreshTokenService` and set `ConsumedTokenCleanupDelay` to the
  same period. The library retries an unclear refresh with the same token
  ([ADR 0009](../decisions/0009-silent-refresh.md)), so a short grace period avoids a forced login after a
  dropped connection. See [Refreshing a token](https://docs.duendesoftware.com/identityserver/tokens/refresh/) and
  [Refresh token service](https://docs.duendesoftware.com/identityserver/reference/v7/services/refresh-token-service/).
- Lifetimes: `AbsoluteRefreshTokenLifetime`, `SlidingRefreshTokenLifetime`, and `RefreshTokenExpiration` on the
  client.

## `OidcProfile` settings

The issuer is IdentityServer's origin (or `IssuerUri` if you set one), exactly as discovery reports it. All
endpoints are on the same origin, so `allowedEndpointOrigins` is not needed.

```csharp
new OidcProfile("<application>", "<profile>", new Uri("https://<identityserver>"), "<client-id>",
    scopes: ["profile", "offline_access"], persistence: PersistenceMode.Plaintext);
```

## Integration test variables

None. The fixture creates the server, three clients (browser, consent, device), a generated test user, and a
signing key for each run, so the Duende scenarios always run, including on pull requests in the required
integration job. Test-host settings that differ from a production server:

- `options.Authentication.CookieSameSiteMode = SameSiteMode.Lax`: the host is loopback HTTP, and Chromium rejects
  IdentityServer's default `SameSite=None` cookie without HTTPS. Over HTTPS, keep the default.
- Automatic key management is off and a per-run RSA key is configured, so nothing is written to disk.
- `AccessTokenLifetime = 300`, so the refresh test's clock shift stays within the ID token's lifetime.

## Security notes

- Keep the client public with no secret, PKCE S256 required, and plain PKCE disallowed.
- Serve IdentityServer over HTTPS. The library accepts HTTP issuers only for IP-literal loopback development.
- Discovery advertises `revocation_endpoint`; the library does not call it yet (remote revocation is deferred).
- Discovery does not advertise `acr_values_supported` by default.

## Known limits and troubleshooting

| Symptom | `AuthErrorCode` | Cause and fix |
|---|---|---|
| IdentityServer shows an invalid `redirect_uri` error | none (the provider stops) | Exact matching is on. Add `AddAppAuthRedirectUriValidator()` and register `http://127.0.0.1`, or register the exact URI and port. |
| The login page reappears after signing in | `BrowserUnavailable` (in automated tests) or a login timeout | The browser dropped the `SameSite=None` session cookie on plain HTTP. Use HTTPS, or `Lax` for a local HTTP test host. |
| No refresh; token acquisition near expiry asks for login | `ReauthenticationRequired` | No refresh token was issued: request `offline_access` and set `AllowOfflineAccess = true`. |
| A refresh asks for a new login | `ReauthenticationRequired` (`invalid_grant`) | A one-time refresh token was reused, or it expired. Log in again, or allow a grace period (see above). |
| User denies consent | `AccessDenied` | Expected; no session is saved. |
| Device login fails before showing a code | `ProtocolFailure` | The client does not allow `GrantTypes.DeviceFlow`, or the device endpoint is disabled. |
