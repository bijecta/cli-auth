namespace Bijecta.CliAuth.Core;

internal static class SessionStatusEvaluator
{
    internal static SessionStatus Evaluate(StoredSession? session, TimeProvider timeProvider, TimeSpan expiryMargin)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (expiryMargin < TimeSpan.Zero || expiryMargin > TimeSpan.FromSeconds(300))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        if (session is null || session.State == SessionLifecycleState.None)
        {
            return new SessionStatus(SessionStatusKind.None);
        }
        if (session.State is SessionLifecycleState.Uncertain or SessionLifecycleState.RefreshInProgress)
        {
            return new SessionStatus(SessionStatusKind.Uncertain);
        }
        if (session.State == SessionLifecycleState.ReauthenticationRequired)
        {
            return new SessionStatus(SessionStatusKind.ReauthenticationRequired);
        }

        var token = session.AccessToken!;
        var kind = token.ExpiresAtUtc - timeProvider.GetUtcNow() > expiryMargin
            ? SessionStatusKind.Available
            : session.RefreshToken is not null ? SessionStatusKind.RefreshRequired : SessionStatusKind.ReauthenticationRequired;
        return new SessionStatus(kind, token.ExpiresAtUtc, session.Subject, session.Issuer);
    }
}
