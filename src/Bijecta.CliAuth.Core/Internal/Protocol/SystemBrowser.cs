using System.Diagnostics;

namespace Bijecta.CliAuth.Core;

internal sealed class SystemBrowser : ISystemBrowser
{
    public async Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(authorizationUri.AbsoluteUri);
            }
            using var process = Process.Start(start);
            if (!OperatingSystem.IsWindows())
            {
                if (process is null) { throw new CliAuthException(AuthErrorCode.BrowserUnavailable); }
                _ = DiscardAsync(process.StandardOutput.BaseStream);
                _ = DiscardAsync(process.StandardError.BaseStream);
                // Detect an immediate opener failure without waiting for a browser's lifetime.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return; }
                if (process.ExitCode != 0) { throw new CliAuthException(AuthErrorCode.BrowserUnavailable); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (CliAuthException) { throw; }
        catch { throw new CliAuthException(AuthErrorCode.BrowserUnavailable); }
    }

    private static async Task DiscardAsync(Stream stream)
    {
        // Openers can echo the authorization URI on failure. Never inherit their output.
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
