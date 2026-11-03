# Auth0 configuration guide

## Tested with

| | |
|---|---|
| Date tested | 2026-11-03 (local runs only so far) |
| Provider version, plan, or tier | Auth0 free plan, EU region, Universal Login |
| Library commit | Pending: the branch adding the Auth0 scenarios |
| Hosted run | Pending: the first `providers` workflow run |
| Result | See the [compatibility table](README.md) |

## Prerequisites

An Auth0 tenant (the free plan is enough) with the default **Username-Password-Authentication** database
connection, and a test user in it. Auth0's sign-up accepts a personal email address or a GitHub, Google, or
Microsoft login.

## Register the application

Use **Native** applications, which are public clients. Do not use *Regular Web Application* (confidential, with a
secret) or *Machine-to-Machine* (no user login). Keep **This application is owned by a third party** off.

The tests use two applications, one per flow, as for Keycloak:

| Setting (Applications → *app* → Settings) | Browser app | Device app |
|---|---|---|
| Application type | Native | Native |
| Allowed Callback URLs | `http://127.0.0.1:<port>/callback/` | empty |
| Advanced Settings → Grant Types | Authorization Code, Refresh Token | Device Code, Refresh Token |
| Refresh Token Rotation | Allow rotation on; Rotation Overlap Period `0` | the same |
| Maximum Refresh Token Lifetime | `2592000` (default; required for rotation) | the same |
| ID Token Expiration | `86400` for the tests (see [Scopes and refresh tokens](#scopes-and-refresh-tokens)) | default |
| Connections tab | Username-Password-Authentication only | the same |

The dashboard may show a client secret for a Native application. The library never sends one; the token endpoint
authentication method of a Native application is *None*.

Tenant settings used by the tests: **Security → Multi-factor Auth** set to *Never* for the test user,
**Authentication → Authentication Profile** set to *Identifier + Password* (username and password on one page),
and **Bot Detection** off, because the tests never get past a CAPTCHA.

## Redirect URIs

Auth0 compares callback URLs exactly, including the port. It does not accept any loopback port as RFC 8252 §7.3
asks, and rejects a wildcard port
([community answer](https://community.auth0.com/t/allow-wildcard-port-in-redirect-uri-as-per-rfc-8252/98409)).
Register one fixed port and pass the same `--callback-port` (the tests use 43823). Register exactly
`http://127.0.0.1:<port>/callback/`: `http`, `127.0.0.1` rather than `localhost`, and the trailing slash. A
mismatch shows Auth0's "Callback URL mismatch" page instead of the login form. Choose a port that other software
on your users' machines is unlikely to use; if it is busy, login fails with `CallbackRejected`.

## Browser flow

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login \
  --issuer https://<tenant>.<region>.auth0.com/ --client-id <client-id> --callback-port <port> \
  --scope profile --scope offline_access --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- status --issuer https://<tenant>.<region>.auth0.com/ --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- token --issuer https://<tenant>.<region>.auth0.com/ --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- logout --issuer https://<tenant>.<region>.auth0.com/ --client-id <client-id> --scope profile --scope offline_access --storage-dir <absolute-dir>
```

`login` opens Universal Login and prints the expiry and granted scopes; `status` reports `Available`; `token`
prints the expiry and scopes, never the token; `logout` removes the local session. Every command must repeat the
same issuer, client, and scopes. First-party applications get no consent screen. These commands were not run
against the tenant; the automated tests use the same public API.

## Device flow

Enable the **Device Code** grant on a Native application, then run `login --flow device --client-id
<device-client-id>` with the same other options. The CLI prints the verification URI
(`https://<tenant>.<region>.auth0.com/activate`) and a user code. On that page the user enters the code,
**confirms it on a second page** (Auth0 asks before signing in), then signs in, and sees "Congratulations, you're
all set!".

## Scopes and refresh tokens

- `openid` is always added. Request `offline_access` (`--scope offline_access`) and enable the **Refresh Token**
  grant to receive a refresh token.
- With **Allow Refresh Token Rotation** on and a **Rotation Overlap Period** of `0`, every refresh returns a new
  refresh token, and reusing a used one answers `invalid_grant` (*Strict*, verified by the tests). Auth0 then also
  revokes the refresh tokens issued after it
  ([Configure Refresh Token Rotation](https://auth0.com/docs/secure/tokens/refresh-tokens/configure-refresh-token-rotation)).
  The library retries an unclear refresh with the same token ([ADR 0009](../decisions/0009-silent-refresh.md)),
  so a few seconds of overlap avoid a forced login after a dropped connection; the tests use `0` to verify the
  strict case.
- Without rotation, the same refresh token stays valid (*None*).
- **ID token lifetime.** Without an `audience`, Auth0 issues an access token for its `/userinfo` endpoint, valid
  for 24 hours, while the ID token lasts 10 hours by default (`36000`). The refresh test moves the clock to just
  before the access token expires, where the refreshed ID token would look expired, so the test application sets
  **ID Token Expiration** to `86400`. Real refreshes happen at real time and do not need this.

## `OidcProfile` settings

The issuer is `https://<tenant>.<region>.auth0.com/` **with the trailing slash**, exactly as Auth0's discovery
reports it; the library compares issuers exactly. All endpoints are on the issuer's host, so
`allowedEndpointOrigins` is not needed. A custom domain changes the issuer to that domain.

```csharp
new OidcProfile("<application>", "<profile>", new Uri("https://<tenant>.<region>.auth0.com/"), "<client-id>",
    scopes: ["profile", "offline_access"], persistence: PersistenceMode.Plaintext,
    callback: new BrowserCallbackOptions(<port>));
```

## Integration test variables

| Variable | Value |
|---|---|
| `CLIAUTH_AUTH0_ISSUER` | `https://<tenant>.<region>.auth0.com/` |
| `CLIAUTH_AUTH0_CLIENT_ID` | Browser application |
| `CLIAUTH_AUTH0_DEVICE_CLIENT_ID` | Device application |
| `CLIAUTH_AUTH0_USERNAME`, `CLIAUTH_AUTH0_PASSWORD` | Dedicated database-connection test user, MFA off |
| `CLIAUTH_AUTH0_CALLBACK_PORT` | The registered port (43823 in the workflow) |

Locally, keep them in `.env.auth0` at the repository root (ignored by Git) and export each line literally, because
a generated password may contain shell characters such as `&`:

```sh
while IFS='=' read -r k v; do [ -n "$k" ] && export "$k=$v"; done < .env.auth0
```

Consent denial is skipped: first-party applications are not asked for consent.

## Security notes

- Native applications are public: keep the client secret unused, and never put it in a CLI.
- Discovery advertises `revocation_endpoint` (`/oauth/revoke`); the library does not call it yet.
- Discovery lists `plain` alongside `S256`; the library always sends S256.
- Without an `audience`, the access token is only good for Auth0's `/userinfo`. Calling your own API needs an
  access token for that API, requested with Auth0's `audience` parameter, which the library cannot send yet (see
  below).

## Known limits and troubleshooting

| Symptom | `AuthErrorCode` | Cause and fix |
|---|---|---|
| Auth0 shows "Callback URL mismatch" | `BrowserUnavailable` (in automated tests) or `LoginTimedOut` | Register exactly `http://127.0.0.1:<port>/callback/` on the browser application, save, and pass the same `--callback-port`. |
| "Wrong email or password" on the login page | `LoginTimedOut` (or `BrowserUnavailable` in automated tests) | The user is not in a connection enabled for this application, is blocked, or the password differs. |
| Login fails before the browser opens | `CallbackRejected` | The registered port is in use. Free it, or register another port. |
| A refresh after the shifted test clock fails | `ProtocolFailure` | The ID token outlives the shifted clock only if **ID Token Expiration** is at least the access token lifetime (tests only). |
| A refresh asks for a new login | `ReauthenticationRequired` (`invalid_grant`) | A used refresh token was presented outside the overlap period, or it expired. Log in again, or set a short overlap period. |
| The access token is rejected by your API | none | It was issued for `/userinfo`: the library cannot request an `audience` yet. |
| The device page shows a CAPTCHA | `LoginTimedOut` | Bot Detection challenged the login. Interactive users can solve it; the tests cannot. |
