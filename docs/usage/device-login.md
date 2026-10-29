# Device login: usage and code walkthrough

Device login is implemented behind the same public authenticator as browser PKCE.
It uses host-owned presentation, provider-paced polling, shared OIDC validation,
and the existing epoch-checked session commit. Storage remains explicitly memory-only
in the sample. Refresh and protected persistence are separate future milestones.

## Host usage

```csharp
var auth = new CliAuthenticator([profile], new MemoryTokenStore());
var login = await auth.LoginAsync("work", new LoginOptions(
    flow: LoginFlow.Device,
    deviceAuthorizationHandler: (prompt, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"Open {prompt.VerificationUri}");
        Console.WriteLine($"Enter code: {prompt.UserCode}");
        return Task.CompletedTask;
    }), cancellationToken);
```

The application presents the verification URI and user code deliberately. It may
also offer the optional `VerificationUriComplete` as a link or QR code. These values
are sensitive interaction data, excluded from ordinary serialization and formatting;
do not put them in diagnostics. The secret device code is never passed to the host.
Core does not launch a browser or bind a listener for device login.

Presentation must finish before Core starts polling. A callback should normally
render the prompt and return promptly; it must not wait for login to finish. Honor
its cancellation token for any asynchronous work. Core stops awaiting an uncooperative
callback at expiry/cancellation, but cannot forcibly stop arbitrary host code.
Browser-only login hints and forced reauthentication are rejected for this flow.

## Implementation

1. `AuthenticationProtocol` selects the internal browser or device implementation.
   Constructors perform no provider I/O; no protocol override is exposed publicly.
2. `ProviderDiscovery` checks the selected flow. Device login requires the discovered
   device authorization endpoint, token endpoint, and usable signing keys. Browser
   endpoint/PKCE/PAR requirements do not apply. Exact issuer, explicit trusted origins,
   bounded retrieval, and the five-minute cache cap remain enforced.
3. `DeviceAuthenticationProtocol` uses Duende's public-client endpoint helpers. It
   validates the authorization response and presentation URIs before calling the host.
   Verification origins must match the issuer or an explicitly configured
   `allowedEndpointOrigins` entry. HTTPS is required except the profile's explicit
   IP-loopback development exception. No client secret is sent.
4. The provider lifetime includes authorization-request latency and presentation.
   Monotonic elapsed time and a cancellation deadline prevent late polling. Each HTTP
   request retains the 30-second bound and 256 KiB POST-response limit. Positive integer
   expiry/interval values are bounded to 2,147,483 seconds to fit bounded timer inputs;
   malformed, duplicate, fractional, string, zero, and overflowing values fail closed.
5. Polling waits the provider interval, or five seconds when absent, before the first
   request and between subsequent requests. `authorization_pending` retains the delay;
   `slow_down` adds five seconds permanently. A request timeout doubles the subsequent
   delay. Backoff saturates at the timer bound and never extends the provider deadline.
   Other network failures, malformed responses, redirects, 5xx, and terminal OAuth
   errors stop the transaction without restarting authorization. Only HTTP 400 OAuth
   pending/slow-down responses continue polling.
6. `LoginTokenValidator` shares signature, issuer, audience, lifetime, subject, azp,
   applicable at_hash, token-envelope, scope, and expiry checks with browser login.
   Browser login supplies its required nonce; device login has no browser nonce.
   An ID token is mandatory for this library's OIDC login. Access tokens stay opaque;
   raw ID tokens are never stored.
7. The coordinator commits only validated results whose captured session epoch still
   matches. Logout or a winning login supersedes an older pending device operation.
   No session lock is held while presenting or polling.

Denial maps to `AccessDenied`; provider `expired_token` and local provider-deadline
expiry map to `LoginTimedOut`. Caller cancellation remains `OperationCanceledException`.
Errors exclude raw provider descriptions, transport exceptions, and codes/URLs.
Missing advertised capability maps to `UnsupportedCapability`. Client-level grant
rejection after successful discovery remains a provider protocol failure.

## Try the development sample

Prepare/start the [Keycloak fixture](../../samples/keycloak/README.md), then run:

```sh
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- demo --flow device \
  --issuer http://127.0.0.1:8080/realms/cliauth \
  --client-id bijecta-cli-device --allow-loopback-http
```

Open the displayed verification page and enter the displayed code. Sign in using
the generated disposable fixture credentials. The sample then acquires the cached
token, reports local status, and logs out, without printing token values.

The fixture uses separate browser and device clients. Keycloak 26.8.1 applies its
client PKCE mandate to device authorization as well; the browser client retains
required S256, while the dedicated device client disables browser authorization and
uses standard RFC 8628 without Keycloak's device PKCE extension. Existing imported
realms must be recreated to pick up the new client. Core has no Keycloak branches.
See the [versioned provider implementation](https://github.com/keycloak/keycloak/blob/26.8.1/services/src/main/java/org/keycloak/protocol/oidc/grants/device/endpoints/DeviceEndpoint.java)
and [RFC 8628 polling rules](https://www.rfc-editor.org/rfc/rfc8628.html#section-3.5).

## Verification

Deterministic tests use signed synthetic tokens and fake time for capabilities,
presentation, pending/slow-down/backoff, cancellation, expiry, invalid responses and
identities, safe errors, no listener/browser use, and logout supersession. The
[Keycloak suite](../../tests/Bijecta.CliAuth.KeycloakTests/README.md) contains separate
real-provider device lifecycle, denial, cancellation, and disabled-grant tests.
Other provider compatibility, native storage, and cross-process locking remain
outside the evidence established by these tests.
