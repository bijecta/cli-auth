namespace Bijecta.CliAuth.Core;

/// <summary>A host-replaceable launcher used only by explicit browser login.</summary>
public interface ISystemBrowser
{
    /// <summary>Opens an authorization URI without logging it or invoking a shell.</summary>
    Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}
