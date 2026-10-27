namespace Bijecta.CliAuth.Core;

/// <summary>Settings for an IPv4-literal loopback browser callback.</summary>
public sealed class BrowserCallbackOptions
{
    /// <summary>Creates callback settings; port zero requests an OS-assigned port.</summary>
    public BrowserCallbackOptions(int port = 0, string path = "/callback/")
    {
        if (port is < 0 or > 65535 || string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith('/') || !path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('%') || path.Contains('?') || path.Contains('#') ||
            path.Any(c => !(char.IsAsciiLetterOrDigit(c) || "/-._~!$&'()*+,;=:@".Contains(c))) || path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }

        Port = port;
        Path = path;
    }

    /// <summary>The only supported listener address for the initial browser flow.</summary>
    public string Address => "127.0.0.1";

    /// <summary>The configured port, or zero for dynamic binding.</summary>
    public int Port { get; }

    /// <summary>The exact callback path, including a trailing slash.</summary>
    public string Path { get; }
}
