namespace Bijecta.CliAuth.Core;

/// <summary>Credential-free local session metadata.</summary>
public sealed class SessionStatus
{
    /// <summary>Creates a status result without token values or provider requests.</summary>
    public SessionStatus(SessionStatusKind kind, DateTimeOffset? expiresAtUtc = null,
        string? subject = null, Uri? issuer = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Kind = kind;
        ExpiresAtUtc = expiresAtUtc?.ToUniversalTime();
        Subject = subject;
        Issuer = issuer;
    }

    /// <summary>The time-dependent local state.</summary>
    public SessionStatusKind Kind { get; }
    /// <summary>Local expiry metadata, if available.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
    /// <summary>Validated local subject metadata; not a live identity check.</summary>
    public string? Subject { get; }
    /// <summary>The stored issuer binding, if available.</summary>
    public Uri? Issuer { get; }
    /// <summary>Formats without personal metadata.</summary>
    public override string ToString() => $"SessionStatus ({Kind})";
}
