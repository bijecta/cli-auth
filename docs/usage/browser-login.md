# Browser login: usage and code walkthrough

The browser adapter is implemented and exercised with signed synthetic tokens,
real loopback sockets, and the public constructor's real HTTP transport. The
[Keycloak fixture](../../samples/keycloak/README.md) passed a user-run live browser
login/acquire/status/logout smoke test on 2026-10-28. Six automated Testcontainers/
Playwright tests also passed against disposable Keycloak and Chromium. Native
launcher/process checks remain pending. This is a development milestone,
not a release/security-review claim. Device flow, refresh, protected persistence,
and remote revocation are still deferred.

## Host usage

```csharp
var profile = new OidcProfile(
    applicationId: "example.my-cli",
    profileName: "work",
    issuer: new Uri("https://id.example.test/realm"),
    clientId: "registered-public-cli",
    scopes: ["api.read"],
    persistence: PersistenceMode.MemoryOnly);
var auth = new CliAuthenticator([profile], new MemoryTokenStore());

var login = await auth.LoginAsync("work", new LoginOptions(), ct);
var token = await auth.GetAccessTokenAsync("work", ct);
// token.Value is intentional credential access; never log it.
await auth.LogoutAsync("work", new LogoutOptions(), ct);
```

Constructing the authenticator performs no discovery or storage I/O. Explicit
browser login does discovery, binds the callback, and opens the browser. The default
callback is an OS-assigned port on `http://127.0.0.1:{port}/callback/`. If your provider
requires an exact registered port, configure `new BrowserCallbackOptions(43821)`
and register that single callback. Do not use broad redirect wildcards.

An issuer normally uses HTTPS. `allowLoopbackHttp: true` is a per-profile development
exception for IP-literal loopback only; it does not disable certificate validation.
Discovered endpoints must share the issuer origin unless the profile explicitly
supplies additional `allowedEndpointOrigins`. That trust policy is copied, validated,
and included in the session key. Configure only origins you trust.

Configuration-file loading belongs to the host CLI. It can bind JSON/environment
settings and construct OidcProfile, or use code directly; Core does not choose file
paths, a configuration framework, precedence rules, or OS storage for the host.
The sample's issuer/client/scope arguments are public configuration used to exercise
this milestone. A file-based sample can follow independently; tokens and secrets
must never become command-line options.

`loginHint` and `forceReauthentication` are typed browser options. State, nonce,
PKCE, redirect, issuer, and client cannot be overridden with a parameter dictionary.
`ISystemBrowser` remains a host extension for embedding/testing; it receives a
sensitive authorization URI and must not log it. The built-in launcher uses native
Windows shell execution or argument-array `/usr/bin/open`/`xdg-open`, with opener
output suppressed. Native desktop behavior still needs manual verification.

## What each part does

1. **SessionCoordinator** protects stored state. It captures the session epoch under
   a short lock, releases the lock while the human signs in, and checks the epoch
   before committing. A competing login or logout can supersede an older result.
2. **ProviderDiscovery** reads the issuer's discovery document and signing keys only
   when needed. It enforces exact issuer identity, endpoint trust, browser capability,
   and a five-minute cache cap. Missing optional metadata does not imply unsupported
   PKCE. Explicitly incompatible metadata fails before browser launch. Required PAR
   is unsupported in this milestone; it is never silently bypassed.
3. **ProtocolHttp** applies request deadlines, response-size limits, and a transport
   without redirect following, ambient cookies, or automatic code-exchange retries.
   Metadata/JWKS allow 1 MiB; token responses allow 256 KiB, including streamed bodies.
4. **LoopbackReceiver** binds IPv4 loopback before launch. It accepts only the exact
   path/Host and a matching state before trusting either a code or provider error.
   It rejects duplicate fields, malformed encoding, mixed code/error, and wrong issuer.
   Four active connections, five-second reads, 16 KiB headers, 8 KiB targets, and a
   20-invalid-request budget bound local abuse. One callback wins; response text is
   static and never reflects callback input. Disposal closes outstanding sockets.
5. **BrowserAuthenticationProtocol** connects those pieces. Duende prepares independent
   state and S256 PKCE, and sends the authorization-code token request. Core adds an
   independent random nonce. It deliberately uses the endpoint helper rather than
   Duende's high-level ProcessResponseAsync so the expiry/scopes/mandatory-ID-token
   policy is explicit. The whole transaction has a 180-second deadline; each provider
   request has 30 seconds. Cancellation is distinct from timeout.
6. **Microsoft validation** verifies the ID-token signature, RS256/ES256 algorithm,
   exact issuer, exact client audience, lifetime with the injected clock and 60-second
   skew, subject, nonce, authorized party, and applicable at_hash. An unknown kid can
   cause one bounded JWKS reload; a bad known-key signature cannot. The access token
   stays opaque. No raw ID token is stored.

After validation, access expiry is calculated from the **token request start**, not
response receipt. This conservatively counts network time against the token lifetime.
An absent scope response inherits the requested scopes; a present one may narrow
but cannot expand them. The result must outlive the profile's expiry margin. The
coordinator saves credentials before returning safe login metadata.

## Verification and limits

Run the solution tests as documented in the root README. Protocol tests cover signed
RSA/EC success, PKCE and redirect binding, signature/issuer/audience/nonce/azp/at_hash
rejection, token envelopes, discovery trust/cache, bounded transport, callback abuse,
connection concurrency/deadlines, cancellation, denial, and key rotation. A separate
public-API test uses the default protocol transport against a local synthetic provider.
Coordinator tests continue to cover login/logout races and storage failures.

Synthetic tests establish these behaviors; they do not establish compatibility with
all providers, native browser launch success, native storage, or cross-process locks.
See the [Keycloak integration suite](../../tests/Bijecta.CliAuth.KeycloakTests/README.md)
for its separate run command, prerequisites, six passing scenarios, and remaining
native launcher/process checks. Additional provider integrations may need narrowly
scoped metadata/client configuration work.
