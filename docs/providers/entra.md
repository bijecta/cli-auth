# Microsoft Entra ID configuration guide

## Tested with

| | |
|---|---|
| Date tested | 2026-11-06 |
| Provider version, plan, or tier | Microsoft Entra ID Free (the directory that comes with an Azure account), single tenant, v2.0 endpoints |
| Library commit | `78036ad` |
| Hosted run | [42305996399](https://github.com/bijecta/cli-auth/actions/runs/42305996399), *Hosted provider integration (Linux)*, dispatch on pull request #12 |
| Result | See the [compatibility table](README.md) |

## Prerequisites

A Microsoft Entra directory: the **Default Directory** of any Azure account is enough, and no paid licence is needed.
The tests also need a dedicated **cloud-only member user** (`<user>@<tenant>.onmicrosoft.com`), not a personal
Microsoft account. A personal account is only a guest in the directory, and it signs in through `login.live.com`
with its own MFA.

Set up the test user so that it never gets an MFA or registration prompt:

- **Overview → Properties → Manage security defaults:** *Disabled*. Security defaults require every user to
  register MFA.
- **Protection → Password reset → Properties:** *None*, and **Authentication methods → Registration campaign:**
  *Disabled*. Either can show "More information required" after the password.
- No Conditional Access policy requires MFA for the test user, and the user has no admin role. Admin roles always
  need methods registered for password reset.
- Sign in once at `https://myapps.microsoft.com` to replace the initial password. Do not use the Azure portal or
  the Entra admin center for this: Microsoft always requires MFA there, and you cannot turn that off.

## Register the application

Use **App registrations → New registration** with **Accounts in this organizational directory only (Single
tenant)**. The tests use two registrations, one per flow:

| Setting | Browser app | Device app |
|---|---|---|
| Supported account types | Single tenant | Single tenant |
| Redirect URI | `http://127.0.0.1/callback/` under *Mobile and desktop applications*, added through the manifest (see [Redirect URIs](#redirect-uris)) | none |
| Authentication → Allow public client flows | not needed | **Yes** (required for the device code grant) |
| API permissions (Microsoft Graph, delegated) | `openid`, `profile`, `offline_access`, `User.Read` | the same |
| Grant admin consent | Yes | Yes |
| Certificates & secrets | none | none |

Both are public clients. The library never sends a client secret, so do not create one. PKCE (S256) is accepted
without any setting.

## Redirect URIs

Microsoft's identity platform ignores the port of a loopback redirect URI when it compares URIs, as RFC 8252 §7.3
asks
([redirect URI restrictions](https://learn.microsoft.com/entra/identity-platform/reply-url#localhost-exceptions)).
One registered `http://127.0.0.1/callback/` therefore serves any `--callback-port`, including the default dynamic
port. The tests pass a fixed port (43823) only to keep their configuration the same as Auth0's.

The portal's redirect URI form does not accept `127.0.0.1` for every platform. Add the URI in the app's
**Manifest** instead (on the *Microsoft Graph App Manifest* tab), then save:

```json
"publicClient": {
    "redirectUris": [ "http://127.0.0.1/callback/" ]
},
```

Afterwards, **Authentication** lists it under *Mobile and desktop applications*. Keep `http`, `127.0.0.1` rather
than `localhost`, and the trailing slash.

## Browser flow

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login \
  --issuer https://login.microsoftonline.com/<tenant-id>/v2.0 --client-id <client-id> \
  --scope profile --scope offline_access --storage-dir <absolute-dir>
```

`status`, `token`, and `logout` take the same issuer, client, and scopes, as in the [Auth0 guide](auth0.md#browser-flow).
The sign-in pages ask for the username, then the password, then may ask **Stay signed in?** (either answer works).
With admin consent granted, users see no consent screen. These commands were not run against the tenant; the
automated tests use the same public API.

## Device flow

Turn on **Allow public client flows** on the device registration. The `verification_uri` Entra returns is
`https://login.microsoft.com/device`, which is not on the issuer's host (`login.microsoftonline.com`). Core only
shows the user provider URLs from trusted origins, so the profile must list that origin in
`allowedEndpointOrigins` (see below). Otherwise login fails with `DestinationNotAllowed` before a code is shown.
The sample CLI has no option for this yet, so its `--flow device` does not work against Entra.

On the verification page the user enters the code, signs in, and approves on **Approve sign-in on another device**
(older pages ask "Are you trying to sign in to …?"). The page then says the user may close the window.

## Scopes and refresh tokens

- `openid` is always added. Request `offline_access` to receive a refresh token.
- **Extra granted scopes.** Entra's token responses list every delegated permission the app has consent for (with
  the setup above: `User.Read email openid profile`), whatever was requested. Core ignores granted scopes that
  were not requested ([ADR 0009](../decisions/0009-silent-refresh.md#validating-the-response)), so
  `GrantedScopes` shows only the requested ones. The access token still carries all of them.
- **Access token audience.** Without a resource scope, the access token is for Microsoft Graph. To call your own
  API, request its scope (`api://<app-id>/<scope>`). Entra issues an access token for one resource at a time.
- **Rotation.** Each refresh returns a new refresh token, and the used one stays valid until it expires
  (*Rotates*, verified by the tests).
- **Lifetimes.** Access tokens last 60–90 minutes (about 71 in the test run), and the ID token lasts 1 hour. The
  refresh test moves the clock forward instead of waiting. At just before access-token expiry, the refreshed ID
  token would look expired, so the Entra tests move the clock into the 30-minute early-refresh window instead
  (`refreshAhead`). Real refreshes happen at real time and need none of this. Changing token lifetimes in Entra
  needs a premium licence.

## `OidcProfile` settings

The issuer is `https://login.microsoftonline.com/<tenant-id>/v2.0`, exactly as the tenant's discovery document
reports it. The library compares issuers exactly, so only single-tenant issuers work. The `common` and
`organizations` endpoints report a `{tenantid}` template as their issuer, so discovery fails with `DiscoveryFailed`.

```csharp
new OidcProfile("<application>", "<profile>", new Uri("https://login.microsoftonline.com/<tenant-id>/v2.0"),
    "<client-id>", scopes: ["profile", "offline_access"], persistence: PersistenceMode.Plaintext,
    // Needed for the device flow only: the verification page is on another host.
    allowedEndpointOrigins: [new Uri("https://login.microsoft.com")]);
```

## Integration test variables

| Variable | Value |
|---|---|
| `CLIAUTH_ENTRA_ISSUER` | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `CLIAUTH_ENTRA_CLIENT_ID` | Browser registration's Application (client) ID |
| `CLIAUTH_ENTRA_DEVICE_CLIENT_ID` | Device registration's Application (client) ID |
| `CLIAUTH_ENTRA_USERNAME`, `CLIAUTH_ENTRA_PASSWORD` | The cloud-only test user, with no MFA registration |
| `CLIAUTH_ENTRA_CALLBACK_PORT` | Optional; the workflow uses 43823 |

Locally, keep them in a `.env.*` file at the repository root (ignored by Git) and export each line literally, as
in the [Auth0 guide](auth0.md#integration-test-variables).

Consent denial is skipped: admin consent is granted, so the user is never asked.

## Security notes

- Both registrations are public clients: no secret or certificate is created or sent.
- Discovery advertises no `revocation_endpoint` and no `code_challenge_methods_supported`. Entra supports S256,
  and the library always sends it.
- A used refresh token stays valid after rotation (*Rotates*). Logout only removes the local session, and the
  library cannot revoke remotely.
- The access token carries every consented permission, including ones the CLI did not request. Grant only the
  permissions the CLI needs.

## Known limits and troubleshooting

| Symptom | `AuthErrorCode` | Cause and fix |
|---|---|---|
| "More information required" after the password | `LoginTimedOut` (`BrowserUnavailable` in automated tests) | Security defaults, password-reset registration, a registration campaign, or a Conditional Access policy asks for MFA. Turn them off for the test user (see [Prerequisites](#prerequisites)). The user's **Sign-in logs** name the cause. |
| Device login fails before a code is shown | `DestinationNotAllowed` | Add `https://login.microsoft.com` to `allowedEndpointOrigins`. |
| Device login fails with `unauthorized_client` | `ProtocolFailure` | Turn on **Allow public client flows** on the device registration. |
| Entra shows "The redirect URI … does not match" | `LoginTimedOut` (`BrowserUnavailable` in automated tests) | Register exactly `http://127.0.0.1/callback/` through the manifest. |
| Login fails with a `common` or `organizations` issuer | `DiscoveryFailed` | Discovery reports the `{tenantid}` template, which does not equal the configured issuer. Use the tenant-specific issuer; multi-tenant issuers are not supported. |
| A refresh asks for a new login | `ReauthenticationRequired` (`invalid_grant`) | The refresh token expired or was revoked (password change, session revocation). Log in again. |
