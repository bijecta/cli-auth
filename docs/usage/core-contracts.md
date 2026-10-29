# Core contracts and memory sessions

The initial Core feature implements immutable local configuration, safe contract
models, session isolation, and a process-local memory store with matching locks.
It defines `ICliAuthenticator`, now implemented by the local session coordinator
in `CliAuthenticator`. Browser PKCE, device login, and lazy discovery are implemented;
refresh and DI registration remain deferred. See [composition](authenticator-composition.md)
and [browser login](browser-login.md).

The [compiled consumer example](../../samples/Bijecta.CliAuth.Sample.Cli/Examples/CoreUsage.cs)
shows profile configuration, direct construction, and facade usage. The sample CLI
exposes `demo --flow browser|device` with explicit memory storage. The device example
uses the implemented host presentation callback; see [device login](device-login.md).

## Configuration and flow selection

```csharp
var profile = new OidcProfile(
    applicationId: "example.my-cli",
    profileName: "work",
    issuer: new Uri("https://id.example.test"),
    clientId: "my-cli",
    scopes: ["api.read", "offline_access"],
    persistence: PersistenceMode.MemoryOnly);

var store = new MemoryTokenStore();
```

The persistence setting describes the required policy; it does not select an OS
backend. Memory-only mode must be intentional. The normal default is Protected;
protected backends will be implemented in later features. The store declares its
policy through the read-only ITokenStore.PersistenceMode property, and authenticator
construction rejects profiles with a different policy.

[ADR 0005](../decisions/0005-plaintext-persistence-first.md) accepts a future, explicitly
selected plaintext file backend. A distinct plaintext policy must be added before
that backend can be used. The current enum/default remains unchanged; no file backend
is implemented yet, and plaintext must never masquerade as Protected.

The agreed composition model supports multiple named profiles with one shared
storage/locking setup per authenticator. All profiles must match its persistence
policy; duplicate names are rejected. Per-profile backend selection is deferred.
This policy is recorded in [ADR 0004](../decisions/0004-authenticator-storage-and-profile-scope.md);
direct construction and persistence compatibility checks are implemented. Provider
browser capability checks happen lazily when explicit login needs discovery.

Profile construction validates local values without contacting a provider. It
includes `openid`; `profile` and `offline_access` are opt-in. Scope collections are
copied, distinct, ordinal, case-sensitive, and read-only. The configured issuer's
original spelling is retained for exact comparison and isolation. A narrowly
scoped IP-loopback HTTP development exception is off by default.

Login flow is selected per call, not part of the account identity. Browser is the
default. Device login requires an asynchronous presentation callback and rejects
browser-only account hints and reauthentication options. There is no arbitrary
parameter dictionary or automatic flow fallback. The presentation data includes
the user code, verification URI, optional complete URI, and expiry, never a device code.

## Isolation and sensitive values

`SessionKey.ForProfile(profile)` hashes a versioned, length-prefixed UTF-8 encoding
of the application, profile, exact issuer, client, sorted scope set, and callback/
security policy fingerprint. Scope count is explicit; this avoids ambiguous field
boundaries. Policy version 1 includes callback settings, loopback HTTP opt-in,
expiry margin, persistence, explicitly trusted additional endpoint origins, and the
initial validation policy. Changes to identity
or policy select another key. Browser and device flow share a profile's key.

`AccessToken.Value` is deliberate sensitive access for HTTP or other protocols.
Ordinary formatting, debugger presentation, and default JSON serialization omit it.
`StoredSession` similarly excludes both tokens and contains no raw ID token.
Default JSON is therefore **not** a persistence representation: protected backends
must use an explicit internal envelope serializer rather than serializing these
redacted public views. Device presentation codes and URIs are also excluded from
ordinary serialization and formatting.

`CliAuthException` uses fixed messages and locally generated correlation IDs. It
accepts no raw exception/body/message. Known standard provider codes are allowlisted;
unknown strings are discarded. Cancellation remains `OperationCanceledException`.
The application owns output, retries, and exit codes.

## Memory store and lock contract

`MemoryTokenStore` implements both `ITokenStore` and `ISessionLockProvider`. Share
the same instance for storage and coordination; different instances are isolated.
Individual reads/replacements/deletions are atomic. Compound read/check/write
operations require an acquired lease:

```csharp
var key = SessionKey.ForProfile(profile);
await using (var lease = await store.AcquireAsync(key, TimeSpan.FromSeconds(30), ct))
{
    var session = await store.ReadAsync(key, ct);
    // A coordinator checks epoch/generation and applies a transition while locked.
}
```

The store does not reacquire its own lease inside Read/Write/Delete, which would
make coordinated operations deadlock. Lease disposal is idempotent. Waiters cancel
independently; a timeout returns SessionBusy and never discards a live gate.
Gates remain stable for the instance lifetime. No cross-process coordination or
persistence is claimed by this backend.

Deleting a key clears credentials and retains a None tombstone with a fresh epoch
and advanced generation, including when the key was previously absent. Read returns
that marker so an in-flight login can detect deletion; it returns null only for a
key never stored or removed in this instance. Local status treats the marker as None.
Repeated deletion advances the marker again. Unknown schemas are rejected before
an envelope is accepted. Credential-free lifecycle markers cannot carry tokens.

The store is an advanced extension contract, not an authenticator: it does not
validate protocol responses or enforce stale-writer checks by itself. The coordinator
holds the shared lease, reloads the envelope, compares the captured epoch, and commits
with a new epoch and advanced generation after a validated login result.
Do not hold a lease while waiting for a human to authenticate.

## Local status model

The internal evaluator takes `TimeProvider` and the profile expiry margin. A token
is Available only when its expiry is strictly beyond now plus the margin. At the
boundary, a refresh token yields RefreshRequired; otherwise explicit login is
required. Uncertain and orphaned RefreshInProgress markers report Uncertain without
exposing or using credentials. The facade's GetStatusAsync now uses this evaluator
under the matching session lock, without protocol or browser interaction.

## Validation and next feature

Behavioral tests cover configuration rejection, defensive collections, namespace
isolation, safe formatting, schema/lifecycle validation, fake-time expiry boundaries,
credential removal, lock timeout/cancellation, and 100 cooperating in-process mutations.
These tests do not establish native persistence, completed refresh coordination,
protocol conformance, or real-provider compatibility.

Both login adapters are implemented with synthetic-provider and Keycloak coverage.
Plaintext persistence and refresh coordination follow, preserving lazy discovery
and the internal dependency boundaries in the accepted ADRs.
