# Authenticator composition and session coordination

The coordinator is implemented. It connects named profiles, the chosen store and
locks, the clock, and an internal protocol boundary. Public local status, cached-token
acquisition, and local logout work. Browser login now uses the real internal adapter;
see [the walkthrough](browser-login.md). Login commit/race and revocation orchestration
are also tested with an internal fake. Device login uses the same coordinator;
see [device login](device-login.md). Refresh and remote revocation remain deferred.

## Direct construction

```csharp
var profiles = new[]
{
    new OidcProfile("example.my-cli", "work", new Uri("https://id.example.test"),
        "my-cli", ["api.read"], persistence: PersistenceMode.MemoryOnly),
    new OidcProfile("example.my-cli", "development", new Uri("https://dev-id.example.test"),
        "my-cli-dev", ["api.read"], persistence: PersistenceMode.MemoryOnly)
};
var store = new MemoryTokenStore();
ICliAuthenticator auth = new CliAuthenticator(profiles, store, sessionLocks: store);

var status = await auth.GetStatusAsync("work", ct);
var logout = await auth.LogoutAsync("work", new LogoutOptions(), ct);
```

Persistent plaintext storage is selected the same way, explicitly on both sides:

```csharp
var profile = new OidcProfile("example.my-cli", "work", new Uri("https://id.example.test"),
    "my-cli", ["api.read"], persistence: PersistenceMode.Plaintext);
var store = new FileTokenStore(storageDirectory); // host-owned absolute directory
ICliAuthenticator auth = new CliAuthenticator([profile], store);

foreach (var entry in await auth.ListSessionsAsync(ct))
{
    // entry.Status (Kind, ExpiresAtUtc, Subject, Issuer) or entry.Error
}
```

Tokens are unencrypted; tell users so in your CLI. A profile requiring MemoryOnly or
Protected is rejected with FileTokenStore, and a Plaintext profile is rejected with
MemoryTokenStore. Choose a per-user directory such as `$XDG_STATE_HOME/<app>` or
`~/.local/state/<app>` (Linux), `~/Library/Application Support/<app>` (macOS), or
`%LOCALAPPDATA%\<app>` (Windows); no default-location helper is provided yet.
To move a profile off Plaintext later, delete its old record with
`SessionKey.ForProfile(plaintextProfile)` and `FileTokenStore.DeleteAsync`
([ADR 0008](../decisions/0008-plaintext-persistence-integration.md)).

`sessionLocks` may be omitted when the store implements ISessionLockProvider, as
MemoryTokenStore and FileTokenStore do. If that intrinsic provider exists, a conflicting explicit
provider is rejected. Other custom stores must receive an explicit matching lock
provider. Instances sharing a backend must share its coordination domain.

Profiles are copied into an ordinal, case-sensitive registry. Duplicate names,
an empty registry, missing dependencies, invalid lock timeouts, and persistence
mismatches fail as InvalidConfiguration during construction. No discovery, store
read/write, browser operation, or clock read occurs in the constructor. The default
lock wait is 30 seconds; the clock and browser can be supplied by the host. The host
owns the lifetime of the injected dependencies.

ITokenStore now requires a read-only PersistenceMode property. It must be immutable
and perform no I/O. Every profile must match that declared policy. The declaration
does not certify the security of a custom backend; extension authors own its real
protection and atomicity guarantees. Backend selection remains explicit, with no
OS detection, per-profile routing, or plaintext fallback.

## Local operations

Every operation resolves its named profile and acquires the matching per-session
lease before accessing state. Reads are reloaded, not cached in the authenticator.
This lets separate instances observe committed changes in the same backend.

GetStatusAsync classifies local state with TimeProvider and the configured margin.
It does not call the protocol or browser. GetAccessTokenAsync returns only an active,
correctly bound token outside the margin. Missing/unrecoverable state requires
explicit login. A refresh-required session currently returns UnsupportedCapability
without clearing credentials, retrying, or opening a browser; refresh coordination
is a follow-up feature.

An orphaned RefreshInProgress is reported as Uncertain by status. Acquisition while
holding the lock converts it to a credential-free Uncertain marker, advances its
generation, and returns ReauthenticationRequired. It never reuses the previous
refresh token. Stored issuer/client/scope mismatches fail as StorageCorrupt rather
than becoming a usable token or being silently reset.

## Login transaction coordination

The implemented sequence is:

1. Acquire the session lease, read/check the envelope, and capture its epoch. Store
   readiness failures occur before any protocol interaction.
2. Release the lease and invoke the internal protocol adapter with the selected flow
   and host interaction settings. Do not hold a session lock while the human signs in.
3. Reacquire the lease and reload the envelope. If logout or another login changed
   the epoch, reject the old operation as OperationSuperseded.
4. Check result binding, granted-scope containment, subject, and unexpired lifetime.
   The adapter is responsible for full cryptographic and flow validation; these
   coordinator checks are additional consistency checks, not substitutes for it.
5. Atomically commit a new Active envelope with a fresh epoch and advanced generation.
   Return safe login metadata only after the store confirms the write.

Two competing logins can run interactively at once; the first successful commit
wins. A generation change alone does not supersede a login when the epoch is still
the same, allowing later refresh coordination to coexist with explicit login.
Cancellation before commit preserves existing state, including when an adapter
returns a result after ignoring cancellation. Unknown protocol exceptions are
converted to safe ProtocolFailure errors without response bodies or inner exceptions.

The default public adapter dispatches browser PKCE or device login with lazy discovery.
A missing selected-flow capability returns UnsupportedCapability without fallback. No public protocol
override or third-party protocol types are exposed. Coordinator tests inject a fake
through an internal constructor; applications cannot configure it as a public extension.

## Logout and revocation

Ordinary local logout removes the session record without first reading/decrypting
credentials. Explicit logout can therefore remove corrupt or unsupported-schema data;
no other operation repairs or replaces it. Every committed session has a fresh epoch,
so a login that started from an existing session and finishes after logout is
superseded. A login started while signed out is not fenced and commits its fresh
credentials ([ADR 0007](../decisions/0007-logout-deletes-session-records.md)).

When revocation is requested, copy the refresh token under the lease if readable,
then commit local deletion and release the lease before remote work. A requested
revocation with no usable refresh token reports Unsupported. An unreadable session
can still be removed, reporting Failed for revocation. A deletion failure throws;
it cannot report a successful logout or start revocation.

The optional revocation attempt has a 30-second TimeProvider-based cancellation
budget and is awaited with that cancellation even if the adapter ignores it.
Timeout or provider failure reports Failed while local removal stays committed.
Caller cancellation remains OperationCanceledException; if it happens after local
commit, it does not restore credentials. Completion of an older revocation never
performs another deletion, so it cannot remove a newer login.

The default public adapter reports Unsupported for revocation. No live provider
revocation or browser SSO logout is claimed by these fake-tested orchestration rules.

## Verified scope

Tests use controlled completion barriers rather than browser interaction or sleeps
for login/logout races. They cover direct composition, profile and persistence
validation, silent local operations, shared-store visibility, competing login commits,
logout supersession, cancellation, corrupt storage, commit/delete failures, and a
fake-time revocation deadline. Native persistence and cross-process locking are
not established by these in-memory tests.

See [ADR 0004](../decisions/0004-authenticator-storage-and-profile-scope.md) and the
[compiled consumer example](../../samples/Bijecta.CliAuth.Sample.Cli/Examples/CoreUsage.cs).
