namespace Bijecta.CliAuth.Core;

/// <summary>Local removal and remote revocation outcomes without SSO logout claims.</summary>
public sealed class LogoutResult
{
    /// <summary>Creates a credential-free logout result.</summary>
    public LogoutResult(bool localSessionRemoved, RevocationOutcome revocationOutcome = RevocationOutcome.NotRequested)
    {
        if (!Enum.IsDefined(revocationOutcome))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        LocalSessionRemoved = localSessionRemoved;
        RevocationOutcome = revocationOutcome;
    }

    /// <summary>Whether local credentials were removed successfully.</summary>
    public bool LocalSessionRemoved { get; }
    /// <summary>The independently reported remote outcome.</summary>
    public RevocationOutcome RevocationOutcome { get; }
}
