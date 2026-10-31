using System.Diagnostics;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProcessTests;

// Drives one Bijecta.CliAuth.SessionProcess child: a separate CLI run sharing the storage directory.
internal sealed class SessionProcess : IAsyncDisposable
{
    // Must match the child's profile so the parent can locate and read its session record.
    internal static readonly OidcProfile Profile = new("bijecta-process-tests", "work", new Uri("https://issuer.example.test"),
        "process-client", ["api.read"], PersistenceMode.Plaintext);
    internal static SessionKey Key { get; } = SessionKey.ForProfile(Profile);

    private readonly Process _process;
    private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private bool _finished;

    private SessionProcess(string directory)
    {
        _deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // The SDK exports the muxer it is running under; PATH may resolve a host lacking this runtime.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SessionProcess", "Bijecta.CliAuth.SessionProcess.dll"));
        start.ArgumentList.Add(directory);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start session helper.");
    }

    internal static async Task<SessionProcess> StartAsync(string directory)
    {
        var child = new SessionProcess(directory);
        try
        {
            Assert.Equal("READY", await child.ReadAsync());
            return child;
        }
        catch { await child.DisposeAsync(); throw; }
    }

    internal Task SendAsync(string command) => _process.StandardInput.WriteLineAsync(command.AsMemory(), _deadline.Token);

    internal async Task<string> ReadAsync() =>
        await _process.StandardOutput.ReadLineAsync(_deadline.Token) ?? throw new InvalidOperationException("The session helper exited.");

    internal async Task<string> RunAsync(string command)
    {
        await SendAsync(command);
        return await ReadAsync();
    }

    /// <summary>Ends the child normally and asserts a clean exit with no error output.</summary>
    internal async Task ExitAsync()
    {
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(_deadline.Token);
        Assert.Equal(0, _process.ExitCode);
        Assert.Equal("", await _process.StandardError.ReadToEndAsync(_deadline.Token));
        _finished = true;
    }

    /// <summary>Terminates the child abruptly, as a crash or a killed terminal would.</summary>
    internal async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(_deadline.Token);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_finished && !_process.HasExited)
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

internal sealed class ProcessTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-process-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every file name in the directory other than the session record and its lock file.</summary>
    internal string[] StrayFiles() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName)
        .Where(name => name != SessionProcess.Key.Value + ".json" && name != SessionProcess.Key.Value + ".lock")
        .ToArray()!;

    public void Dispose()
    {
        // Windows can report a killed child as exited shortly before its file handles close,
        // and scanners may briefly open new files. Retry for a bounded time; a genuine leak still fails.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
                return;
            }
            catch (Exception error) when (attempt < 40 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
