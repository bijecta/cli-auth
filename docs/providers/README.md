# Provider compatibility

Which OAuth 2.0/OIDC providers Bijecta.CliAuth has been tested against, and how. The rules for what counts as
tested are in [ADR 0010](../decisions/0010-provider-compatibility.md). Each provider has a configuration guide;
new guides start from the [template](_template.md).

## Compatibility

| Provider | Browser PKCE | Device | Refresh | Rotation | Revocation advertised | MFA request | Guide |
|---|---|---|---|---|---|---|---|
| Keycloak | ✅ | ✅ | ✅ | Strict ✅ | Yes | not tested | [Keycloak](keycloak.md) |
| Duende IdentityServer | ✅ | ✅ | ✅ | Strict ✅ | Yes | not tested | [Duende](duende.md) |
| Okta | not tested | not tested | not tested | not tested | not tested | not tested | — |
| Auth0 | ✅ | ✅ | ✅ | Strict ✅ | Yes | not tested | [Auth0](auth0.md) |
| Microsoft Entra ID | not tested[⁴](#entra-4) | not tested | not tested | not tested (*Rotates* locally) | No | not tested | [Entra ID](entra.md) |
| Google | not tested | not tested | not tested | not tested | not tested | not tested | — |
| GitHub | not tested | not tested | not tested | not tested | not tested | not tested | — |

### Legend

- ✅ works, shown by a **hosted** CI run (linked below). A result from local runs only stays "not tested",
  with a note.
- ⚠️ works with a documented limit (see the footnote and the guide).
- ❌ does not work. The footnote says whether the provider does not support it, or the library fails.
- "not tested": no hosted evidence yet.
- **Rotation** describes what the provider does with a used refresh token: *None* (the same token stays valid),
  *Rotates* (a new token is issued; the old one is still accepted for a while), or *Strict* (reusing the old one
  is rejected). Not rotating is not a failure. The ✅ means the library handled that behaviour.
- **Revocation advertised** records whether discovery lists a `revocation_endpoint`. The library does not call it
  yet (remote revocation is deferred).
- **MFA request** is whether the provider honours `acr_values`, `max_age`, and `prompt=login` (milestone 2,
  phase 7).

## Tested against

| Provider | Version or date | Library commit | .NET | Hosted run | Known limits |
|---|---|---|---|---|---|
| Keycloak | 26.8.1 (container image tag), 2026-10-31 | `5ef8473` | 10.0 (SDK 10.0.401) | [41254707591](https://github.com/bijecta/cli-auth/actions/runs/41254707591) (push to main) | [1](#keycloak-1) |
| Duende IdentityServer | 8.0.10, 2026-11-02 | `f793f24` | 10.0 (SDK 10.0.401) | [41518115392](https://github.com/bijecta/cli-auth/actions/runs/41518115392) (pull request #9) | [2](#duende-2) |
| Auth0 | Free plan (EU region), 2026-11-03 | `aee5e20` | 10.0 (SDK 10.0.401) | [41724808616](https://github.com/bijecta/cli-auth/actions/runs/41724808616) (push to main) | [3](#auth0-3) |

### Notes

<a id="keycloak-1"></a>1. Keycloak: the client PKCE mandate also applies to device requests, so the device flow
uses a separate client without it. The disposable test realm enables strict rotation; Keycloak's default realm
setting keeps the old refresh token valid (*Rotates*). See the [guide](keycloak.md#known-limits-and-troubleshooting).

<a id="duende-2"></a>2. Duende IdentityServer 8.0.10, hosted in the test process: browser, device, refresh with strict
rotation, consent denial, and session reuse pass on every pull request. Strict rotation needs
`RefreshTokenUsage = OneTimeOnly`; the server default `ReUse` does not rotate (*None*). Any-port loopback
redirects need the server's opt-in `AddAppAuthRedirectUriValidator()`. Production use needs a Duende licence. See
the [guide](duende.md).

<a id="auth0-3"></a>3. Auth0 (free plan, hosted tenant): browser, device, refresh with strict rotation (0 s
overlap), and session reuse pass; consent denial is skipped (first-party applications get no consent screen).
Auth0 needs a fixed registered callback port, and the library cannot yet send the `audience` parameter, so the
access token is for Auth0's `/userinfo` only. See the [guide](auth0.md).

<a id="entra-4"></a>4. Microsoft Entra ID (free directory, single tenant): local runs on 2026-11-06 passed browser,
device, refresh (*Rotates*), and session reuse; consent denial is skipped (admin consent). The cells change to ✅
once a hosted run passes. Only single-tenant issuers work, because the `common` and `organizations` issuers are
templates. The device `verification_uri` is on another host and must be in `allowedEndpointOrigins`. Granted
scopes that were not requested are ignored. The refresh test uses the early-refresh window because the 1 h ID
token expires before the access token. See the [guide](entra.md).
