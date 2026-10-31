using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

internal static class CoordinatorTestData
{
    internal static OidcProfile Profile(string name = "work", PersistenceMode persistence = PersistenceMode.MemoryOnly) =>
        CoreTestData.Profile(name: name, scopes: ["api.read"], persistence: persistence);

    internal static ValidatedLogin Candidate(OidcProfile profile, string subject = "synthetic-subject",
        DateTimeOffset? expiry = null, string? refreshToken = "synthetic-refresh", IEnumerable<string>? scopes = null,
        Uri? issuer = null, string? clientId = null) => new(
        new AccessToken("synthetic-access-" + subject, expiry ?? CoreTestData.Now.AddMinutes(5), scopes ?? profile.Scopes),
        subject, issuer ?? profile.Issuer, clientId ?? profile.ClientId, refreshToken);

    internal static ValidatedRefresh Refreshed(string value = "synthetic-refreshed-access", DateTimeOffset? expiry = null,
        string? refreshToken = "synthetic-rotated-refresh", IEnumerable<string>? scopes = null) => new(
        new AccessToken(value, expiry ?? CoreTestData.Now.AddHours(1), scopes ?? ["api.read"],
            obtainedAtUtc: CoreTestData.Now), refreshToken);

    internal static StoredSession Session(OidcProfile profile, DateTimeOffset? expiry = null,
        string? refreshToken = "synthetic-refresh", SessionLifecycleState state = SessionLifecycleState.Active,
        long generation = 0, Guid? epoch = null, DateTimeOffset? obtained = null) => new(generation, epoch ?? Guid.NewGuid(), state,
        new AccessToken("synthetic-cached-access", expiry ?? CoreTestData.Now.AddMinutes(5), profile.Scopes,
            obtainedAtUtc: obtained),
        refreshToken, profile.Issuer, profile.ClientId, "synthetic-cached-subject");

    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static Task<T> Bounded<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = CoreTestData.Now;
        internal int Reads;
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref Reads);
            return Now;
        }
    }

    internal sealed class Browser : ISystemBrowser
    {
        internal int Calls;
        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    internal sealed class Protocol : IAuthenticationProtocol
    {
        internal int LoginCalls;
        internal int RevokeCalls;
        internal int RefreshCalls;
        internal Func<OidcProfile, LoginOptions, ISystemBrowser, CancellationToken, Task<ValidatedLogin>>? Login;
        internal Func<OidcProfile, string, CancellationToken, Task<ValidatedRefresh>>? Refresh;
        internal Func<OidcProfile, string, CancellationToken, Task<RevocationOutcome>>? Revoke;

        public Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options, ISystemBrowser browser, CancellationToken ct)
        {
            Interlocked.Increment(ref LoginCalls);
            return Login is null ? Task.FromResult(Candidate(profile)) : Login(profile, options, browser, ct);
        }

        public Task<ValidatedRefresh> RefreshAsync(OidcProfile profile, string refreshToken, string subject,
            IReadOnlyList<string> grantedScopes, CancellationToken ct)
        {
            Interlocked.Increment(ref RefreshCalls);
            return Refresh is null ? Task.FromResult(Refreshed()) : Refresh(profile, refreshToken, ct);
        }

        public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct)
        {
            Interlocked.Increment(ref RevokeCalls);
            return Revoke is null ? Task.FromResult(RevocationOutcome.Succeeded) : Revoke(profile, refreshToken, ct);
        }
    }

    // A declared-policy/fault-injection fake, not a protected-storage implementation.
    internal sealed class Store(PersistenceMode mode = PersistenceMode.MemoryOnly) : ITokenStore
    {
        internal MemoryTokenStore Inner { get; } = new();
        internal int Reads;
        internal int Writes;
        internal int Deletes;
        internal Exception? ReadError { get; set; }
        internal Exception? WriteError { get; set; }
        internal Exception? DeleteError { get; set; }
        public PersistenceMode PersistenceMode { get; } = mode;

        public ValueTask<StoredSession?> ReadAsync(SessionKey key, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (ReadError is not null) { throw ReadError; }
            return Inner.ReadAsync(key, ct);
        }

        public ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct)
        {
            Interlocked.Increment(ref Writes);
            if (WriteError is not null) { throw WriteError; }
            return Inner.WriteAsync(key, session, ct);
        }

        public ValueTask DeleteAsync(SessionKey key, CancellationToken ct)
        {
            Interlocked.Increment(ref Deletes);
            if (DeleteError is not null) { throw DeleteError; }
            return Inner.DeleteAsync(key, ct);
        }
    }

    internal sealed class Fixture(OidcProfile? profile = null)
    {
        internal OidcProfile Profile { get; } = profile ?? CoordinatorTestData.Profile();
        internal Store Store { get; } = new();
        internal Protocol Protocol { get; } = new();
        internal Clock Clock { get; } = new();
        internal Browser Browser { get; } = new();
        internal SessionKey Key => SessionKey.ForProfile(Profile);
        internal CliAuthenticator Auth() => new([Profile], Store, Protocol, Store.Inner, Browser, Clock);
        internal ValueTask SeedAsync(StoredSession session) => Store.Inner.WriteAsync(Key, session, TestContext.Current.CancellationToken);
        internal ValueTask<StoredSession?> ReadAsync() => Store.Inner.ReadAsync(Key, TestContext.Current.CancellationToken);
    }
}
