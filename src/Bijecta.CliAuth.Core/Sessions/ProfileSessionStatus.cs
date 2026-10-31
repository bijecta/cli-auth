namespace Bijecta.CliAuth.Core;

/// <summary>One configured profile's credential-free local status, or the error that prevented reading it.</summary>
public sealed class ProfileSessionStatus
{
    /// <summary>Creates an entry for a profile whose local status was read.</summary>
    public ProfileSessionStatus(string profileName, SessionStatus status)
    {
        ProfileName = ContractValidation.Identifier(profileName, AuthErrorCode.InvalidRequest);
        Status = status ?? throw new CliAuthException(AuthErrorCode.InvalidRequest);
    }

    /// <summary>Creates an entry for a profile whose status could not be read, such as a corrupt record.</summary>
    public ProfileSessionStatus(string profileName, AuthErrorCode error)
    {
        ProfileName = ContractValidation.Identifier(profileName, AuthErrorCode.InvalidRequest);
        if (!Enum.IsDefined(error))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Error = error;
    }

    /// <summary>The configured profile name.</summary>
    public string ProfileName { get; }
    /// <summary>The same status GetStatusAsync reports, or null when <see cref="Error"/> is set.</summary>
    public SessionStatus? Status { get; }
    /// <summary>Why this profile's status could not be read, or null when <see cref="Status"/> is set.</summary>
    public AuthErrorCode? Error { get; }
    /// <summary>Formats without personal metadata.</summary>
    public override string ToString() => Error is { } error
        ? $"ProfileSessionStatus ({ProfileName}, {error})"
        : $"ProfileSessionStatus ({ProfileName}, {Status!.Kind})";
}
