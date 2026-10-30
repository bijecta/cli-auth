using System.Diagnostics;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.StorageTests;

internal sealed class LockTestProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private bool _released;

    // Probes expecting SessionBusy stay short; probes expecting ACQUIRED tolerate transient
    // contention, such as Windows antivirus or indexer handles on a newly created lock file.
    private const int BusyProbeMilliseconds = 150;
    private const int AcquireMilliseconds = 5000;

    private LockTestProcess(string directory, SessionKey key, bool disableImplicitLocks, string mode, int timeoutMilliseconds)
    {
        _deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // The SDK exports the muxer it is running under; PATH may resolve a host lacking this runtime.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LockProcess", "Bijecta.CliAuth.LockProcess.dll"));
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(key.Value);
        start.ArgumentList.Add(timeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(mode);
        // Set before child startup: SafeFileHandle caches this setting statically.
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = disableImplicitLocks ? "1" : "0";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lock helper.");
    }

    internal static async Task<LockTestProcess> HoldAsync(string directory, SessionKey key, bool disableImplicitLocks = false)
    {
        var child = new LockTestProcess(directory, key, disableImplicitLocks, "hold", AcquireMilliseconds);
        try
        {
            Assert.Equal("WAITING", await child.ReadAsync());
            Assert.Equal("ACQUIRED", await child.ReadAsync());
            return child;
        }
        catch { await child.DisposeAsync(); throw; }
    }

    /// <summary>Expects contention: returns SessionBusy after a short wait, or ACQUIRED if the lock was free.</summary>
    internal static Task<string> ProbeBusyAsync(string directory, SessionKey key, bool disableImplicitLocks = false) =>
        ProbeAsync(directory, key, disableImplicitLocks, BusyProbeMilliseconds);

    /// <summary>Expects a free lock: waits long enough to absorb transient third-party contention.</summary>
    internal static Task<string> ProbeAcquireAsync(string directory, SessionKey key, bool disableImplicitLocks = false) =>
        ProbeAsync(directory, key, disableImplicitLocks, AcquireMilliseconds);

    private static async Task<string> ProbeAsync(string directory, SessionKey key, bool disableImplicitLocks, int timeoutMilliseconds)
    {
        await using var child = new LockTestProcess(directory, key, disableImplicitLocks, "probe", timeoutMilliseconds);
        Assert.Equal("WAITING", await child.ReadAsync());
        var result = await child.ReadAsync();
        if (result == "ACQUIRED") { Assert.Equal("RELEASED", await child.ReadAsync()); }
        await child.ExitAsync();
        child._released = true;
        return result!;
    }

    private async Task<string?> ReadAsync() => await _process.StandardOutput.ReadLineAsync(_deadline.Token);

    private async Task ExitAsync()
    {
        await _process.WaitForExitAsync(_deadline.Token);
        Assert.Equal(0, _process.ExitCode);
        Assert.Equal("", await _process.StandardError.ReadToEndAsync(_deadline.Token));
    }

    internal async Task ReleaseAsync()
    {
        _process.StandardInput.Close();
        Assert.Equal("RELEASED", await ReadAsync());
        await ExitAsync();
        _released = true;
    }

    internal async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(_deadline.Token);
        _released = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_released && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                // Cleanup must still work when the test's token/deadline has expired.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cleanup.Token);
            }
        }
        finally { _process.Dispose(); _deadline.Dispose(); }
    }
}
