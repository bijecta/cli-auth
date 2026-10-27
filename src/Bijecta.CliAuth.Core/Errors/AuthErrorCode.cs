namespace Bijecta.CliAuth.Core;

/// <summary>Stable failure categories for hosts to map to their own presentation.</summary>
public enum AuthErrorCode
{
    /// <summary>The local profile or policy is invalid.</summary>
    InvalidConfiguration,
    /// <summary>Provider discovery failed validation or retrieval.</summary>
    DiscoveryFailed,
    /// <summary>The selected capability is unavailable.</summary>
    UnsupportedCapability,
    /// <summary>The system browser could not be opened.</summary>
    BrowserUnavailable,
    /// <summary>The callback was rejected.</summary>
    CallbackRejected,
    /// <summary>The interactive login timed out.</summary>
    LoginTimedOut,
    /// <summary>Authorization was denied.</summary>
    AccessDenied,
    /// <summary>Explicit login is required.</summary>
    ReauthenticationRequired,
    /// <summary>Refresh may have reached the provider without a reliable result.</summary>
    RefreshOutcomeUnknown,
    /// <summary>The selected storage backend is unavailable.</summary>
    StorageUnavailable,
    /// <summary>The stored session is corrupt.</summary>
    StorageCorrupt,
    /// <summary>The stored schema is unsupported.</summary>
    UnsupportedSchema,
    /// <summary>The session lock could not be acquired in time.</summary>
    SessionBusy,
    /// <summary>Another session mutation superseded this operation.</summary>
    OperationSuperseded,
    /// <summary>A provider network operation failed.</summary>
    NetworkFailure,
    /// <summary>A provider response failed protocol requirements.</summary>
    ProtocolFailure,
    /// <summary>Token lifetime does not exceed the configured margin.</summary>
    TokenLifetimeTooShort,
    /// <summary>The requested HTTP destination is not allowed.</summary>
    DestinationNotAllowed,
    /// <summary>The requested operation or data is invalid.</summary>
    InvalidRequest,
}
