namespace Bijecta.CliAuth.Core;

/// <summary>An explicitly selected interactive login flow.</summary>
public enum LoginFlow
{
    /// <summary>Authorization Code with PKCE through the system browser.</summary>
    Browser,
    /// <summary>Device authorization with host-owned code presentation.</summary>
    Device
}
