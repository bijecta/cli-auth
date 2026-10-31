using System.Collections.Frozen;

namespace Bijecta.CliAuth.Core;

internal sealed class SessionCoordinator
{
    private readonly FrozenDictionary<string, Registration> _profiles;
    private readonly Registration[] _profileOrder;
    private readonly ITokenStore _store;
    private readonly ISessionLockProvider _locks;
    private readonly IAuthenticationProtocol _protocol;
    private readonly ISystemBrowser _browser;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lockTimeout;

    internal SessionCoordinator(IEnumerable<OidcProfile> profiles, ITokenStore tokenStore,
        IAuthenticationProtocol protocol, ISessionLockProvider? sessionLocks, ISystemBrowser? browser,
        TimeProvider? timeProvider, TimeSpan? sessionLockTimeout)
    {
        if (profiles is null || tokenStore is null || protocol is null)
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        var persistenceMode = tokenStore.PersistenceMode;
        if (!Enum.IsDefined(persistenceMode))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        if (tokenStore is ISessionLockProvider intrinsicLocks)
        {
            if (sessionLocks is not null && !ReferenceEquals(sessionLocks, intrinsicLocks))
            {
                throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
            }
            sessionLocks = intrinsicLocks;
        }
        if (sessionLocks is null)
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }

        var registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
        var order = new List<Registration>();
        foreach (var profile in profiles)
        {
            var registration = profile is null ? null : new Registration(profile);
            if (registration is null || profile!.Persistence != persistenceMode ||
                !registrations.TryAdd(profile.ProfileName, registration))
            {
                throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
            }
            order.Add(registration);
        }
        if (registrations.Count == 0)
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        _lockTimeout = sessionLockTimeout ?? TimeSpan.FromSeconds(30);
        if (_lockTimeout <= TimeSpan.Zero || _lockTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        _profiles = registrations.ToFrozenDictionary(StringComparer.Ordinal);
        _profileOrder = [.. order];
        _store = tokenStore;
        _locks = sessionLocks;
        _protocol = protocol;
        _browser = browser ?? new SystemBrowser();
        _time = timeProvider ?? TimeProvider.System;
    }

    internal async Task<LoginResult> LoginAsync(string profile, LoginOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var registration = Resolve(profile);
        if (options is null)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Guid capturedEpoch;
        bool capturedCorrupt;
        await using (var lease = await AcquireAsync(registration.Key, ct).ConfigureAwait(false))
        {
            // ADR 0009: explicit login may replace a corrupt record, but only one still corrupt at commit.
            (var current, capturedCorrupt) = await ReadReplaceableAsync(registration, ct).ConfigureAwait(false);
            capturedEpoch = current?.Epoch ?? Guid.Empty;
        }

        // No session lock is held while the user completes authentication.
        ValidatedLogin candidate;
        try
        {
            candidate = await _protocol.LoginAsync(registration.Profile, options, _browser, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (OperationCanceledException) { throw new CliAuthException(AuthErrorCode.NetworkFailure); }
        catch (Exception) { throw new CliAuthException(AuthErrorCode.ProtocolFailure); }

        ct.ThrowIfCancellationRequested();
        await using (var lease = await AcquireAsync(registration.Key, ct).ConfigureAwait(false))
        {
            var (current, corrupt) = await ReadReplaceableAsync(registration, ct).ConfigureAwait(false);
            if (corrupt != capturedCorrupt || (current?.Epoch ?? Guid.Empty) != capturedEpoch)
            {
                // A record that became corrupt mid-login is never replaced; report it rather than supersede.
                throw new CliAuthException(corrupt ? AuthErrorCode.StorageCorrupt : AuthErrorCode.OperationSuperseded);
            }
            ValidateCandidate(registration, candidate);
            var committed = new StoredSession(NextGeneration(current), Guid.NewGuid(), SessionLifecycleState.Active,
                candidate.AccessToken, candidate.RefreshToken, candidate.Issuer, candidate.ClientId, candidate.Subject);
            await WriteAsync(registration.Key, committed, ct).ConfigureAwait(false);
            return new LoginResult(candidate.Subject, candidate.Issuer, candidate.AccessToken.ExpiresAtUtc,
                candidate.AccessToken.GrantedScopes, registration.Profile.Persistence);
        }
    }

    internal async Task<SessionStatus> GetStatusAsync(string profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await StatusAsync(Resolve(profile), ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ProfileSessionStatus>> ListSessionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<ProfileSessionStatus>(_profileOrder.Length);
        foreach (var registration in _profileOrder)
        {
            try
            {
                entries.Add(new ProfileSessionStatus(registration.Profile.ProfileName,
                    await StatusAsync(registration, ct).ConfigureAwait(false)));
            }
            catch (CliAuthException error)
            {
                // One unreadable or busy profile must not hide the others.
                entries.Add(new ProfileSessionStatus(registration.Profile.ProfileName, error.ErrorCode));
            }
        }
        return entries.AsReadOnly();
    }

    private async Task<SessionStatus> StatusAsync(Registration registration, CancellationToken ct)
    {
        await using var lease = await AcquireAsync(registration.Key, ct).ConfigureAwait(false);
        var current = await ReadAsync(registration, ct).ConfigureAwait(false);
        return SessionStatusEvaluator.Evaluate(current, _time, registration.Profile.ExpiryMargin);
    }

    internal async Task<AccessToken> GetAccessTokenAsync(string profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var registration = Resolve(profile);
        await using var lease = await AcquireAsync(registration.Key, ct).ConfigureAwait(false);
        var current = await ReadAsync(registration, ct).ConfigureAwait(false);
        if (current?.State == SessionLifecycleState.RefreshInProgress)
        {
            // The matching lock has no live refresh owner: this is an orphaned marker.
            await WriteAsync(registration.Key, new StoredSession(NextGeneration(current), current.Epoch,
                SessionLifecycleState.Uncertain), ct).ConfigureAwait(false);
            throw new CliAuthException(AuthErrorCode.ReauthenticationRequired);
        }
        var status = SessionStatusEvaluator.Evaluate(current, _time, registration.Profile.ExpiryMargin);
        if (status.Kind == SessionStatusKind.Available)
        {
            return current!.AccessToken!;
        }
        if (status.Kind == SessionStatusKind.RefreshRequired)
        {
            // Refresh coordination is a subsequent feature. Preserve the existing session.
            throw new CliAuthException(AuthErrorCode.UnsupportedCapability);
        }
        throw new CliAuthException(AuthErrorCode.ReauthenticationRequired);
    }

    internal async Task<LogoutResult> LogoutAsync(string profile, LogoutOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var registration = Resolve(profile);
        if (options is null)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        string? refreshToken = null;
        var outcome = options.RevokeRefreshToken ? RevocationOutcome.Unsupported : RevocationOutcome.NotRequested;
        await using (var lease = await AcquireAsync(registration.Key, ct).ConfigureAwait(false))
        {
            if (options.RevokeRefreshToken)
            {
                try
                {
                    refreshToken = (await ReadAsync(registration, ct).ConfigureAwait(false))?.RefreshToken;
                }
                catch (CliAuthException error) when (error.ErrorCode is AuthErrorCode.StorageCorrupt or
                    AuthErrorCode.UnsupportedSchema or AuthErrorCode.StorageUnavailable or AuthErrorCode.StorageInsecure)
                {
                    // Explicit local removal must remain possible when credentials cannot be read.
                    outcome = RevocationOutcome.Failed;
                }
            }
            await DeleteAsync(registration.Key, ct).ConfigureAwait(false);
        }

        if (options.RevokeRefreshToken && refreshToken is not null)
        {
            // Local removal is committed and the lease released before remote work.
            try
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                outcome = await _protocol.RevokeRefreshTokenAsync(registration.Profile, refreshToken, linked.Token)
                    .WaitAsync(linked.Token).ConfigureAwait(false);
                if (outcome is not (RevocationOutcome.Succeeded or RevocationOutcome.Unsupported or RevocationOutcome.Failed))
                {
                    outcome = RevocationOutcome.Failed;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { outcome = RevocationOutcome.Failed; }
        }
        return new LogoutResult(localSessionRemoved: true, outcome);
    }

    private Registration Resolve(string profile)
    {
        ContractValidation.Identifier(profile);
        return _profiles.TryGetValue(profile, out var registration)
            ? registration : throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
    }

    private void ValidateCandidate(Registration registration, ValidatedLogin? candidate)
    {
        if (candidate is null || candidate.AccessToken is null || candidate.Issuer is null || !candidate.Issuer.IsAbsoluteUri ||
            !string.Equals(candidate.Issuer.OriginalString, registration.Profile.Issuer.OriginalString, StringComparison.Ordinal) ||
            !string.Equals(candidate.ClientId, registration.Profile.ClientId, StringComparison.Ordinal) ||
            candidate.AccessToken.GrantedScopes.Any(scope => !registration.Scopes.Contains(scope)) ||
            candidate.AccessToken.ExpiresAtUtc <= _time.GetUtcNow())
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }
        ContractValidation.Identifier(candidate.Subject, AuthErrorCode.ProtocolFailure);
        if (candidate.RefreshToken is not null)
        {
            ContractValidation.Identifier(candidate.RefreshToken, AuthErrorCode.ProtocolFailure);
        }
    }

    private async ValueTask<StoredSession?> ReadAsync(Registration registration, CancellationToken ct)
    {
        StoredSession? current;
        try { current = await _store.ReadAsync(registration.Key, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (Exception) { throw new CliAuthException(AuthErrorCode.StorageUnavailable); }
        if (current?.State is SessionLifecycleState.Active or SessionLifecycleState.RefreshInProgress)
        {
            if (!string.Equals(current.Issuer!.OriginalString, registration.Profile.Issuer.OriginalString, StringComparison.Ordinal) ||
                !string.Equals(current.ClientId, registration.Profile.ClientId, StringComparison.Ordinal) ||
                current.AccessToken!.GrantedScopes.Any(scope => !registration.Scopes.Contains(scope)))
            {
                throw new CliAuthException(AuthErrorCode.StorageCorrupt);
            }
        }
        return current;
    }

    // A corrupt record reads as (null, true); its successor starts again at generation 0.
    private async ValueTask<(StoredSession? Session, bool Corrupt)> ReadReplaceableAsync(Registration registration, CancellationToken ct)
    {
        try { return (await ReadAsync(registration, ct).ConfigureAwait(false), false); }
        catch (CliAuthException error) when (error.ErrorCode == AuthErrorCode.StorageCorrupt) { return (null, true); }
    }

    private async ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct)
    {
        try { await _store.WriteAsync(key, session, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (Exception) { throw new CliAuthException(AuthErrorCode.StorageUnavailable); }
    }

    private async ValueTask DeleteAsync(SessionKey key, CancellationToken ct)
    {
        try { await _store.DeleteAsync(key, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (Exception) { throw new CliAuthException(AuthErrorCode.StorageUnavailable); }
    }

    private async ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, CancellationToken ct)
    {
        try
        {
            var lease = await _locks.AcquireAsync(key, _lockTimeout, ct).ConfigureAwait(false);
            return lease is null ? throw new CliAuthException(AuthErrorCode.StorageUnavailable) : new SafeLease(lease);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (Exception) { throw new CliAuthException(AuthErrorCode.StorageUnavailable); }
    }

    private static long NextGeneration(StoredSession? previous) => previous?.Generation == long.MaxValue
        ? throw new CliAuthException(AuthErrorCode.StorageCorrupt) : previous is null ? 0 : previous.Generation + 1;

    private sealed class Registration(OidcProfile profile)
    {
        internal OidcProfile Profile { get; } = profile;
        internal SessionKey Key { get; } = SessionKey.ForProfile(profile);
        internal FrozenSet<string> Scopes { get; } = profile.Scopes.ToFrozenSet(StringComparer.Ordinal);
    }

    private sealed class SafeLease(IAsyncDisposable lease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (CliAuthException) { throw; }
            catch (Exception) { throw new CliAuthException(AuthErrorCode.StorageUnavailable); }
        }
    }
}
