using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

/// <summary>Explicit per-login flow and host interaction settings.</summary>
[DebuggerDisplay("LoginOptions ({Flow})")]
public sealed class LoginOptions
{
    /// <summary>Creates options; device flow requires presentation and rejects browser-only hints.</summary>
    public LoginOptions(LoginFlow flow = LoginFlow.Browser,
        Func<DeviceAuthorizationPrompt, CancellationToken, Task>? deviceAuthorizationHandler = null,
        string? loginHint = null, bool forceReauthentication = false)
    {
        if (!Enum.IsDefined(flow) ||
            (flow == LoginFlow.Device && (deviceAuthorizationHandler is null || loginHint is not null || forceReauthentication)) ||
            (flow == LoginFlow.Browser && deviceAuthorizationHandler is not null))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        if (loginHint is not null)
        {
            ContractValidation.Identifier(loginHint);
        }

        Flow = flow;
        DeviceAuthorizationHandler = deviceAuthorizationHandler;
        LoginHint = loginHint;
        ForceReauthentication = forceReauthentication;
    }

    /// <summary>The selected flow; the library must not silently change it.</summary>
    public LoginFlow Flow { get; }
    /// <summary>The host's asynchronous presentation hook; never receives a device code.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public Func<DeviceAuthorizationPrompt, CancellationToken, Task>? DeviceAuthorizationHandler { get; }
    /// <summary>An optional browser account hint, excluded from default diagnostics.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? LoginHint { get; }
    /// <summary>Whether browser login explicitly requests reauthentication.</summary>
    public bool ForceReauthentication { get; }
    /// <summary>Formats without account hints or presentation delegates.</summary>
    public override string ToString() => $"LoginOptions ({Flow})";
}
