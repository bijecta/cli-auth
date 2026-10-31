# 0009: Silent refresh

Status: Accepted

Date: 2026-10-31 (roadmap milestone 1)

## Context

`GetAccessTokenAsync` returns a cached access token while it is outside the
profile's `ExpiryMargin` (default 60 s, at most 300 s). Inside the margin it
returns `UnsupportedCapability` and keeps the session, so every expired access
token currently means a new login.

The design documents (SPECS TOK-01…05, ARCHITECTURE "Refresh and mutation
coordination") already describe refresh. Their strictest rule, TOK-04, says a
refresh that might have reached the provider but got no trustworthy answer must
never be retried. That covers a timeout after sending, a dropped connection, a
crash, cancellation, or a failed commit. The library would persist a
`RefreshInProgress` marker before every request and, after an unclear outcome,
mark the session `Uncertain`, remove its credentials, and require a new login.
The concern is that the provider may already have rotated the refresh token.

Established CLIs handle refresh differently. These descriptions are based on
their public behavior, not a fresh code audit:

- Azure CLI (MSAL), gcloud, the AWS CLI's SSO sessions, and kubelogin all keep
  the stored refresh token after a network failure and try again on the next run.
  None of them wipe a session because an outcome is unclear.
- All of them report a rejected refresh token (`invalid_grant`) as "run login
  again". kubelogin goes further and starts an interactive login itself.
- MSAL holds its file lock only around cache reads and writes, not around the
  HTTP request, so two processes can refresh at once. That is safe for Entra and
  Google, which do not strictly invalidate an old refresh token.
- The AWS CLI starts trying to refresh 15 minutes before expiry. While the
  current token is still valid, a failed refresh is logged and the current token
  is used; only an expired token turns the failure into an error.

## Decision

### Where refresh runs

`GetAccessTokenAsync` refreshes on demand. There are no background workers and no
timers. It acquires the session lease, reloads the record, and decides from that
record. When a refresh is needed, it holds the lease through the token request
and the durable commit, then releases it.

Consequences of holding the lease throughout:

- Simultaneous callers in one process or across processes cause exactly one
  refresh request. The others wait for the lease, reload the committed record,
  and use it (TOK-02).
- Login commits and logout need the same lease, so they cannot interleave with a
  refresh. Logout waits for a running refresh or returns `SessionBusy`, and a
  refresh can never write back a session that was logged out or replaced (TOK-03,
  TOK-05). The record read under the lease is the one being replaced, so no
  separate epoch check is needed. A refresh keeps the epoch and publishes the next
  generation.
- This is stricter than MSAL and the AWS CLI. It is kept because the providers
  most likely to rotate strictly (Okta, Auth0, Keycloak) can treat two concurrent
  uses of one refresh token as reuse and revoke the whole session.

### When refresh runs: two margins

A profile gets a second, wider margin, modelled on the AWS CLI:

- **Hard margin:** the existing `ExpiryMargin`. Inside it, the cached token is not
  returned. A refresh must succeed, or the call fails.
- **Early-refresh margin:** new `OidcProfile.RefreshAhead`, default 5 minutes,
  between `ExpiryMargin` and 30 minutes. Setting it equal to `ExpiryMargin` turns
  early refresh off. Inside it but outside the hard margin, the call tries to
  refresh. If that attempt fails transiently (see below), the call returns the
  current, still-valid token instead of an error.

A token whose total lifetime is shorter than the early margin (Keycloak's default
access token lives 5 minutes) would otherwise be refreshed on every call. The
early margin therefore applies only to the last half of a token's lifetime. To
know that lifetime, `AccessToken` gains an optional `ObtainedAtUtc`: the local
time the token response was received, saved with the record. Records without it
use only the hard margin.

`GetStatusAsync` and `ListSessionsAsync` are unchanged. They report `Available`
outside the hard margin and never refresh or touch the network.

### Outcomes

| Outcome | Session | Result |
|---|---|---|
| Success | Commit one record, next generation, same epoch: new access token, expiry, `ObtainedAtUtc`, and granted scopes. Keep the old refresh token if none is returned. | The new token |
| `invalid_grant` | Replace the record with a credential-free `ReauthenticationRequired` record (same epoch, next generation). Login replaces it; logout removes it. | `ReauthenticationRequired`, in both margins |
| Other OAuth error (`invalid_client`, `invalid_scope`, `unauthorized_client`, …) | Kept | A typed `ProtocolFailure` with the provider's error code, never the raw body. Not swallowed in the early margin: these are configuration problems. |
| Transient: connection failure, timeout, provider 5xx, `temporarily_unavailable`, discovery failure | Kept | Early margin: the current token. Hard margin: `NetworkFailure` or `DiscoveryFailed`. |
| Cancellation by the caller | Kept | `OperationCanceledException` |
| New token lifetime not longer than `ExpiryMargin` | The successful refresh is still committed | `TokenLifetimeTooShort`, after one refresh, never a loop (TOK-01) |
| Commit fails after the provider answered | Unchanged on disk | `StorageUnavailable` (or the store's error); the new token is not returned |

A failed commit after a rotated refresh token leaves the old token on disk. The
next refresh then gets `invalid_grant` and asks for a login, or succeeds inside a
provider's reuse window. A session is never silently half-written.

### Retry instead of forced login (changes TOK-04)

An unclear outcome keeps the stored refresh token, and the next call tries again.
No `RefreshInProgress` marker is written before a request, and refresh never
produces `Uncertain`. Compared with the strict rule:

- **Provider does not rotate** (Google; Entra in practice): the retry succeeds.
  The strict rule forced a login for nothing.
- **Provider rotates with a reuse window** (Okta's grace period, Auth0's reuse
  interval, Keycloak's maximum reuse count): the retry usually succeeds.
- **Provider rotates strictly with reuse detection:** the retry gets
  `invalid_grant`, or the provider revokes that session. The user logs in again,
  which is where the strict rule would have left them anyway.

So the retry is never worse than the strict rule for the user. Sending a refresh
token back to the provider that issued it, from the client it was issued to, is
not a security risk; detecting reuse is the provider's job. A crash during a
refresh needs no recovery: the operating system releases the lease (ADR 0006), and
the next run retries.

Records already in the `RefreshInProgress` or `Uncertain` state keep their current
handling (an orphaned marker becomes `Uncertain`; both require login). The states
stay in the enum and the file format for compatibility. `RefreshOutcomeUnknown`
is no longer produced and stays in `AuthErrorCode` so later values keep their
numbers.

### Timeouts

The token request has its own deadline, shorter than the 30 s lease wait, so a
slow provider does not turn every waiting caller into `SessionBusy`: 20 s in the
hard margin, 5 s in the early margin, where the current token is still usable.
Discovery shares that deadline. No failure cooldown is persisted: during an outage
each early-margin call makes one short attempt, and connection failures usually
fail at once.

### Validating the response

The protocol layer validates a refresh response with the same rules as a login
token response:

- token type `Bearer` and a positive `expires_in`;
- returned scopes must be a subset of the session's granted scopes; an absent
  `scope` keeps the current granted scopes (RFC 6749 §5.1);
- an ID token, if returned, is fully validated (signature, issuer, audience,
  times), and its `sub` must equal the session's subject (OIDC Core §12.2). A
  mismatch is a `ProtocolFailure` and keeps the session;
- the issuer and client ID of the stored session must match the profile; the
  token endpoint comes from discovery under the profile's endpoint-origin rules.

`IAuthenticationProtocol` gains a refresh operation that returns a validated
result or throws an already classified `CliAuthException`; Core never parses
provider responses. Refresh tokens, access tokens, and raw bodies never appear in
errors, logs, or status.

### Lease-enforced store APIs (closes the ADR 0006/0008 question)

No lease- or epoch-checked overloads are added to `ITokenStore`. `CliAuthenticator`
is the supported path and holds leases itself; code that calls a store directly
coordinates on its own. This can be revisited if a real consumer needs it.

## Not decided here

- Reacting to an HTTP 401 with a refresh, and never letting a delayed 401 from an
  old token invalidate a newer one (TOK-03), belong to the HTTP message handler in
  roadmap milestone 3.
- Starting an interactive login after `ReauthenticationRequired`, as kubelogin
  does, is a host or integration-layer choice (milestone 3), not Core's.
- Step-up and MFA claims during refresh belong to milestone 2.

## Consequences

- `OidcProfile` gains `refreshAhead`, `AccessToken` gains `ObtainedAtUtc`, and
  `IAuthenticationProtocol` gains a member. These are source-breaking for custom
  protocol implementations; acceptable before 1.0. The record keeps schema
  version 1, because the new field is optional.
- Tokens are refreshed up to 5 minutes before expiry by default, so providers see
  somewhat more refresh requests than with expiry-only refresh.
- The parent SPECS TOK-04 and ARCHITECTURE refresh section are updated to match.
- Tests:
  - unit tests with a fake protocol for every outcome row, both margins,
    simultaneous callers making one request, and login and logout waiting for a
    refresh;
  - protocol tests against a fake token endpoint for rotated and kept refresh
    tokens, `invalid_grant`, scope narrowing, an ID token with another `sub`, and
    timeouts;
  - process tests: two processes sharing an expiring session make one request,
    a process killed mid-refresh leaves the next run able to retry, and a stale
    refresh cannot undo a logout or overwrite a newer login;
  - Keycloak integration with a short access-token lifetime and refresh-token
    rotation, including rejected reuse of a rotated token.
- The sample gains a `token` command that acquires a token and prints its expiry
  and scopes, never the token.

## Alternatives considered

- **Keep TOK-04 as written:** one extra write per refresh, orphaned-marker
  handling, and forced logins after network blips, for no gain over the retry.
- **Lock only around cache I/O (MSAL):** concurrent refreshes with one rotating
  refresh token can trigger reuse detection and revoke the session.
- **Background or timer-driven refresh:** a CLI process is short-lived, and a
  library must not start workers.
- **Persisted failure cooldown:** fewer requests during an outage, but one more
  write and more state for a case the short early deadline already bounds.
- **Swallowing every early-margin failure (as the AWS CLI does):** hides a revoked
  session or a misconfiguration until the token expires.
- **No early margin:** simpler, but any provider blip near expiry fails the call
  even though a valid token is still on disk.
