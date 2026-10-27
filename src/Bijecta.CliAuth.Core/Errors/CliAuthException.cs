namespace Bijecta.CliAuth.Core;

/// <summary>A failure with a stable, credential-free message and no raw provider exception.</summary>
public sealed class CliAuthException : Exception
{
    /// <summary>Creates a safe failure without accepting raw messages or response bodies.</summary>
    public CliAuthException(AuthErrorCode errorCode, string? providerErrorCode = null) : base(MessageFor(errorCode))
    {
        ErrorCode = errorCode;
        CorrelationId = Guid.NewGuid();
        ProviderErrorCode = providerErrorCode is "invalid_request" or "invalid_client" or "invalid_grant" or
            "unauthorized_client" or "unsupported_grant_type" or "invalid_scope" or "access_denied" or
            "server_error" or "temporarily_unavailable" or "authorization_pending" or "slow_down" or
            "expired_token" or "invalid_token" or "insufficient_scope" or "unsupported_response_type" or
            "login_required" or "interaction_required" or "consent_required" or "account_selection_required"
            ? providerErrorCode : null;
    }

    /// <summary>The host-facing failure category.</summary>
    public AuthErrorCode ErrorCode { get; }

    /// <summary>A locally generated identifier for correlating safe diagnostics.</summary>
    public Guid CorrelationId { get; }

    /// <summary>An allowlisted standard provider code; unknown input is discarded.</summary>
    public string? ProviderErrorCode { get; }

    /// <summary>Safe guidance; the library never starts an automatic retry from this value.</summary>
    public string RetryGuidance => ErrorCode switch
    {
        AuthErrorCode.InvalidConfiguration or AuthErrorCode.DiscoveryFailed or
            AuthErrorCode.UnsupportedCapability or AuthErrorCode.DestinationNotAllowed or
            AuthErrorCode.InvalidRequest => "Check application and provider configuration.",
        AuthErrorCode.ReauthenticationRequired or AuthErrorCode.RefreshOutcomeUnknown => "Run explicit login.",
        AuthErrorCode.StorageUnavailable or AuthErrorCode.StorageCorrupt or
            AuthErrorCode.UnsupportedSchema => "Inspect the selected storage backend; do not silently reset it.",
        AuthErrorCode.SessionBusy or AuthErrorCode.OperationSuperseded => "Retry the command after the current operation completes.",
        _ => "Inspect the failure before retrying; do not automatically replay token requests."
    };

    private static string MessageFor(AuthErrorCode code) => code switch
    {
        AuthErrorCode.InvalidConfiguration => "Authentication configuration is invalid.",
        AuthErrorCode.DiscoveryFailed => "Provider discovery failed.",
        AuthErrorCode.UnsupportedCapability => "The selected authentication capability is unavailable.",
        AuthErrorCode.BrowserUnavailable => "The system browser is unavailable.",
        AuthErrorCode.CallbackRejected => "The authentication callback was rejected.",
        AuthErrorCode.LoginTimedOut => "Interactive login timed out.",
        AuthErrorCode.AccessDenied => "Authorization was denied.",
        AuthErrorCode.ReauthenticationRequired => "Explicit login is required.",
        AuthErrorCode.RefreshOutcomeUnknown => "The refresh outcome is unknown; explicit login is required.",
        AuthErrorCode.StorageUnavailable => "The selected storage backend is unavailable.",
        AuthErrorCode.StorageCorrupt => "The stored session is corrupt.",
        AuthErrorCode.UnsupportedSchema => "The stored session schema is unsupported.",
        AuthErrorCode.SessionBusy => "The session is busy.",
        AuthErrorCode.OperationSuperseded => "A newer session operation superseded this operation.",
        AuthErrorCode.NetworkFailure => "A provider network operation failed.",
        AuthErrorCode.ProtocolFailure => "A provider response failed protocol requirements.",
        AuthErrorCode.TokenLifetimeTooShort => "The token lifetime does not exceed the expiry margin.",
        AuthErrorCode.DestinationNotAllowed => "The HTTP destination is not allowed.",
        AuthErrorCode.InvalidRequest => "The authentication request is invalid.",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}
