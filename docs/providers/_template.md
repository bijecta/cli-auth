# <Provider> configuration guide

<!-- Copy this file to docs/providers/<provider>.md. Keep every section; write "Not applicable" with a reason
rather than deleting one. Never put real tenant identifiers, client IDs, or credentials in a guide: use
placeholders such as <tenant> and <client-id>. Cite the provider documentation you relied on. -->

## Tested with

| | |
|---|---|
| Date tested | <YYYY-MM-DD> |
| Provider version, plan, or tier | <self-hosted version, or SaaS plan> |
| Library commit | <short hash> |
| Hosted run | <link to the providers workflow run> |
| Result | See the [compatibility table](README.md) |

## Prerequisites

What account, tenant, or installation you need, and anything that must be enabled first.

## Register the application

Register a **public native client** with no client secret, if the provider allows it. If the provider insists
on a secret, say so here and link the ADR 0010 decision.

- Application type and the setting names as they appear in the provider's console.
- Grant types to enable: authorization code (with PKCE S256), refresh token, device code.
- PKCE: whether it is required, optional, or configurable.

## Redirect URIs

The library listens on `http://127.0.0.1:<port>/callback/`. State whether the provider:

- accepts any port for a loopback redirect (RFC 8252 §7.3), so `--callback-port` can stay 0; or
- needs the exact port registered, so the CLI must pass the same `--callback-port`;
- accepts `127.0.0.1`, `localhost`, or only one of them.

## Browser flow

Provider-side setup specific to the browser flow, then the walkthrough with the sample CLI:

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- login \
  --issuer <issuer> --client-id <client-id> --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- status --issuer <issuer> --client-id <client-id> --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- token --issuer <issuer> --client-id <client-id> --storage-dir <absolute-dir>
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- logout --issuer <issuer> --client-id <client-id> --storage-dir <absolute-dir>
```

Expected output for each step. The sample never prints tokens.

## Device flow

How to enable the device grant, whether it needs a separate client, and the same walkthrough with
`--flow device` on `login`. Note what the verification page asks the user for.

## Scopes and refresh tokens

- Scopes to request, and what must be requested to receive a refresh token (for example `offline_access`).
- Scope formats specific to the provider.
- Refresh-token lifetime, idle timeout, and rotation settings, with the *None*, *Rotates*, or *Strict* behaviour
  observed by the tests.

## `OidcProfile` settings

The exact issuer to configure (it must match the discovery document's `issuer`), scopes, callback port, and
`allowedEndpointOrigins` when discovery lists endpoints on another host than the issuer.

```csharp
new OidcProfile("<application>", "<profile>", new Uri("<issuer>"), "<client-id>",
    scopes: ["<scope>"], persistence: PersistenceMode.Plaintext);
```

## Integration test variables

The tests read these variables (see the [provider tests README](../../tests/Bijecta.CliAuth.ProviderTests/README.md)).
Locally they come from your environment; in CI from the `providers` environment's secrets.

| Variable | Value |
|---|---|
| `CLIAUTH_<KEY>_ISSUER` | Exact issuer |
| `CLIAUTH_<KEY>_CLIENT_ID` | Browser client |
| `CLIAUTH_<KEY>_DEVICE_CLIENT_ID` | Optional: device client, if separate |
| `CLIAUTH_<KEY>_CONSENT_CLIENT_ID` | If the provider can deny consent: a client that always asks |
| `CLIAUTH_<KEY>_USERNAME`, `CLIAUTH_<KEY>_PASSWORD` | Dedicated test user, MFA off |
| `CLIAUTH_<KEY>_CALLBACK_PORT` | Optional: the registered port, if the provider needs an exact one |
| `CLIAUTH_<KEY>_SCOPES` | Optional: space-separated scopes, overriding the provider's defaults |

## Security notes

Public-client caveats, secrets the provider requires, and scopes not to request.

## Known limits and troubleshooting

| Symptom | `AuthErrorCode` | Cause and fix |
|---|---|---|
| <what the user sees> | <code> | <why, and what to change> |
