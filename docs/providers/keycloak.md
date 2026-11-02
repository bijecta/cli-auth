# Keycloak configuration guide

## Tested with

| | |
|---|---|
| Date tested | 2026-10-31 |
| Provider version, plan, or tier | Keycloak 26.8.1, `quay.io/keycloak/keycloak:26.8.1` (version tag, not a pinned digest) |
| Library commit | `5ef8473` |
| Hosted run | [41254707591](https://github.com/bijecta/cli-auth/actions/runs/41254707591), job *Keycloak browser and device integration (Linux)* |
| Result | See the [compatibility table](README.md) |

The hosted evidence comes from the disposable suite in
[`tests/Bijecta.CliAuth.KeycloakTests`](../../tests/Bijecta.CliAuth.KeycloakTests/README.md), which builds its own
realm for every run. The settings below match that realm and the
[developer fixture](../../samples/keycloak/README.md).

## Prerequisites

A Keycloak realm you administer. For local development, the fixture in `samples/keycloak` starts one with Docker
Compose, a public client, a device client, and a generated test user.

## Register the application

Create an OpenID Connect client (Keycloak docs:
[OIDC clients](https://www.keycloak.org/docs/latest/server_admin/#_oidc_clients)):

- **Client authentication:** off (a public client, no secret).
- **Standard flow:** on. **Direct access grants** (password grant): off.
- **Advanced → Proof Key for Code Exchange Code Challenge Method:** `S256`.
- **Default client scopes:** keep them minimal (the fixture uses only `basic`) so Keycloak does not add named
  scopes you did not request. Add `profile`, `email`, and `offline_access` as optional scopes.

## Redirect URIs

Register the exact loopback redirect, including the port: `http://127.0.0.1:<port>/callback/`. The fixture uses
`http://127.0.0.1:43821/callback/`, so the CLI must pass `--callback-port 43821`. Keycloak compares the URI it
receives against the registered patterns; a wildcard such as `http://127.0.0.1:*` is not used here.

## Browser flow

With the fixture running (see its README for `prepare`, `up`, and `wait`):

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login \
  --issuer http://127.0.0.1:8080/realms/cliauth --client-id bijecta-cli \
  --callback-port 43821 --allow-loopback-http --storage-dir "$PWD/.local-sessions"
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- status \
  --issuer http://127.0.0.1:8080/realms/cliauth --client-id bijecta-cli \
  --callback-port 43821 --allow-loopback-http --storage-dir "$PWD/.local-sessions"
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- token \
  --issuer http://127.0.0.1:8080/realms/cliauth --client-id bijecta-cli \
  --callback-port 43821 --allow-loopback-http --storage-dir "$PWD/.local-sessions"
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- logout \
  --issuer http://127.0.0.1:8080/realms/cliauth --client-id bijecta-cli \
  --callback-port 43821 --allow-loopback-http --storage-dir "$PWD/.local-sessions"
```

`login` opens your browser at Keycloak's login page; sign in as the generated user. It then prints the expiry
and granted scopes, `status` reports `Available`, `token` prints the expiry and scopes (never the token), and
`logout` reports that the local session was removed. Sessions are stored as plaintext files in the directory you
pass. `--allow-loopback-http` is only for this local HTTP issuer; use HTTPS elsewhere.

## Device flow

Enable **OAuth 2.0 Device Authorization Grant** on a client. Keycloak 26.8.1 applies the client's PKCE mandate
to device requests too, and the library's device flow follows RFC 8628 without PKCE. Use a **separate device
client** with the PKCE method unset and the standard flow off (the fixture's `bijecta-cli-device`):

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login --flow device \
  --issuer http://127.0.0.1:8080/realms/cliauth --client-id bijecta-cli-device \
  --allow-loopback-http --storage-dir "$PWD/.local-sessions"
```

The CLI prints the verification URI and a user code. Open the URI, enter the code, sign in, and grant access on
Keycloak's device consent page (Keycloak shows it for device logins even when the client does not require
consent). Then run `status`, `token`, and `logout` with the same `--client-id bijecta-cli-device`.

## Scopes and refresh tokens

- `openid` is always added. Request `profile` or `email` only if you need them.
- Keycloak issues a refresh token for a normal login without `offline_access`. Request `offline_access` only if
  you want an offline token that outlives the SSO session.
- Refresh-token lifetime follows the realm's **SSO Session Idle** and **SSO Session Max** settings.
- Rotation (**Realm settings → Tokens → Revoke Refresh Token**): off by default, so each refresh returns a new
  refresh token but the old one stays usable (*Rotates*). With it on and **Refresh Token Max Reuse** at 0,
  reuse is rejected with `invalid_grant` (*Strict*). The hosted suite runs with strict rotation.

## `OidcProfile` settings

The issuer is the realm URL, exactly as discovery reports it: `https://<host>/realms/<realm>`. All endpoints
live on the same origin, so `allowedEndpointOrigins` is not needed.

```csharp
new OidcProfile("<application>", "<profile>", new Uri("https://<host>/realms/<realm>"), "<client-id>",
    scopes: ["profile"], persistence: PersistenceMode.Plaintext,
    callback: new BrowserCallbackOptions(port: 43821));
```

## Integration test variables

The hosted suite needs none: it creates its own realm. The provider harness's reference scenarios
(`KeycloakScenarios` in [`tests/Bijecta.CliAuth.ProviderTests`](../../tests/Bijecta.CliAuth.ProviderTests/README.md))
run against the developer fixture with these variables:

| Variable | Value for the fixture |
|---|---|
| `CLIAUTH_KEYCLOAK_ISSUER` | `http://127.0.0.1:8080/realms/cliauth` |
| `CLIAUTH_KEYCLOAK_CLIENT_ID` | `bijecta-cli` |
| `CLIAUTH_KEYCLOAK_DEVICE_CLIENT_ID` | `bijecta-cli-device` |
| `CLIAUTH_KEYCLOAK_CALLBACK_PORT` | `43821` |
| `CLIAUTH_KEYCLOAK_USERNAME` | `learner` |
| `CLIAUTH_KEYCLOAK_PASSWORD` | The generated password in `samples/keycloak/.local/credentials.txt` |

## Security notes

- Keep the client public with no secret, and keep the password grant off.
- Keep the PKCE method at `S256` on the browser client.
- Do not use the fixture's HTTP issuer or generated credentials outside local development.

## Known limits and troubleshooting

| Symptom | `AuthErrorCode` | Cause and fix |
|---|---|---|
| Device login fails before showing a code | `ProtocolFailure` | The client has the device grant disabled, requires PKCE, or does not exist in the running realm. Use a device client without the PKCE method. A fixture realm imported before the device client was added must be recreated (see the fixture README). |
| Login fails before the browser opens | `CallbackRejected` | The registered callback port is in use. Free the port; Keycloak needs the exact registered port. |
| Keycloak shows "Invalid parameter: redirect_uri" | none (the provider stops) | The redirect URI or port does not match the client's registration. |
| A refresh asks for a new login | `ReauthenticationRequired` (`invalid_grant`) | The SSO session expired, or strict rotation rejected a reused refresh token. Log in again. |
| User denies consent | `AccessDenied` | Expected; no session is saved. |
