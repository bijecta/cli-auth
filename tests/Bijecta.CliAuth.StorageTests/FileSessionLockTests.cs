using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;
using Microsoft.Extensions.Time.Testing;

namespace Bijecta.CliAuth.StorageTests;

public sealed class FileSessionLockTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameKeyContendsAcrossInstancesAndHandsOffAfterRelease()
    {
        using var location = new LockTestDirectory();
        var first = new FileTokenStore(location.Path);
        var second = new FileTokenStore(location.Path);
        var owner = await first.AcquireAsync(Key, Timeout, Ct);
        try
        {
            var waiting = second.AcquireAsync(Key, Timeout, Ct).AsTask();
            Assert.False(waiting.IsCompleted);
            await using var independent = await second.AcquireAsync(new SessionKey(new string('b', 64)), Timeout, Ct);
            await owner.DisposeAsync();
            await using var successor = await waiting.WaitAsync(Timeout, Ct);
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task SameInstanceContendsAndDifferentDirectoriesAreIndependent()
    {
        using var location = new LockTestDirectory();
        using var other = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        await using var owner = await store.AcquireAsync(Key, Timeout, Ct);
        Assert.Equal(AuthErrorCode.SessionBusy, (await Assert.ThrowsAsync<CliAuthException>(() =>
            store.AcquireAsync(Key, TimeSpan.FromMilliseconds(40), Ct).AsTask())).ErrorCode);
        await using var independent = await new FileTokenStore(other.Path).AcquireAsync(Key, Timeout, Ct);
    }

    [Fact]
    public async Task CancellationDoesNotPoisonOwnerOrLaterWaiters()
    {
        using var location = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        var owner = await store.AcquireAsync(Key, Timeout, Ct);
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var waiting = store.AcquireAsync(Key, Timeout, cancel.Token).AsTask();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal("SessionBusy", await LockTestProcess.ProbeBusyAsync(location.Path, Key));
        }
        finally { await owner.DisposeAsync(); }
        await using var next = await store.AcquireAsync(Key, Timeout, Ct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public async Task InvalidTimeoutDoesNotCreateDirectory(long milliseconds)
    {
        using var location = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        Assert.Equal(AuthErrorCode.InvalidRequest, (await Assert.ThrowsAsync<CliAuthException>(() =>
            store.AcquireAsync(Key, TimeSpan.FromMilliseconds(milliseconds), Ct).AsTask())).ErrorCode);
        Assert.False(Directory.Exists(location.Path));
    }

    [Fact]
    public async Task PreCancellationAndConstructionDoNotCreateDirectory()
    {
        using var location = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        Assert.False(Directory.Exists(location.Path));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AcquireAsync(Key, Timeout, cancel.Token).AsTask());
        Assert.False(Directory.Exists(location.Path));
    }

    [Fact]
    public async Task LockIdentityIsRetainedAndNewUnixModesArePrivate()
    {
        using var location = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        await (await store.AcquireAsync(Key, Timeout, Ct)).DisposeAsync();
        var path = System.IO.Path.Combine(location.Path, Key.Value + ".lock");
        Assert.Equal([path], Directory.GetFiles(location.Path));
        Assert.Equal(0, new FileInfo(path).Length);
        // Content is irrelevant to ownership; acquiring must neither truncate nor replace the file.
        await System.IO.File.WriteAllTextAsync(path, "synthetic-marker", Ct);
        await (await store.AcquireAsync(Key, Timeout, Ct)).DisposeAsync();
        Assert.Equal("synthetic-marker", await System.IO.File.ReadAllTextAsync(path, Ct));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                System.IO.File.GetUnixFileMode(location.Path));
        }
    }

    [Fact]
    public async Task StorageFailureIsSanitizedRatherThanRetriedAsContention()
    {
        using var location = new LockTestDirectory();
        await System.IO.File.WriteAllTextAsync(location.Path, "synthetic-obstruction", Ct);
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct).AsTask());
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(location.Path, error.ToString());
    }

    [Fact]
    public async Task LockOpenFailureIsSanitizedWithoutRetrying()
    {
        using var location = new LockTestDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(location.Path, Key.Value + ".lock"));
        var retries = 0;
        // Controlled time never advances: misclassifying this as contention would hang until WaitAsync expires.
        var store = new FileTokenStore(location.Path, null, new FakeTimeProvider(), () =>
        {
            retries++;
            return TimeSpan.FromMilliseconds(25);
        });
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            store.AcquireAsync(Key, Timeout, Ct).AsTask().WaitAsync(Timeout, Ct));
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Equal(0, retries);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(location.Path, error.ToString());
    }

    [Fact]
    public void OnlyExactPlatformContentionCodeIsRetried()
    {
        var code = OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsMacOS() ? 35 : 11;
        Assert.True(FileSessionLockProvider.IsContention(new IOException("anything", code)));
        Assert.False(FileSessionLockProvider.IsContention(new IOException("sharing violation", 5)));
        Assert.False(FileSessionLockProvider.IsContention(new IOException("would block", unchecked((int)0x80070005))));
    }

    [Fact]
    public async Task DeadlineCapsRetryDelayWithControlledTime()
    {
        using var location = new LockTestDirectory();
        await using var owner = await new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct);
        var time = new FakeTimeProvider();
        var retries = 0;
        var waiter = new FileTokenStore(location.Path, null, time, () =>
        {
            retries++;
            return TimeSpan.FromMilliseconds(25);
        });
        var waiting = waiter.AcquireAsync(Key, TimeSpan.FromMilliseconds(10), Ct).AsTask();
        Assert.Equal(1, retries);
        time.Advance(TimeSpan.FromMilliseconds(9));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var error = await Assert.ThrowsAsync<CliAuthException>(() => waiting.WaitAsync(Timeout, Ct));
        Assert.Equal(AuthErrorCode.SessionBusy, error.ErrorCode);
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task RetryWaitsForInjectedClockThenAcquiresReleasedLock()
    {
        using var location = new LockTestDirectory();
        var owner = await new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct);
        try
        {
            var time = new FakeTimeProvider();
            var waiter = new FileTokenStore(location.Path, null, time, () => TimeSpan.FromMilliseconds(25));
            var waiting = waiter.AcquireAsync(Key, Timeout, Ct).AsTask();
            await owner.DisposeAsync();
            time.Advance(TimeSpan.FromMilliseconds(24));
            Assert.False(waiting.IsCompleted);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await using var acquired = await waiting.WaitAsync(Timeout, Ct);
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task CancellationInterruptsFakeTimeDelayWithoutAdvancingClock()
    {
        using var location = new LockTestDirectory();
        await using var owner = await new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct);
        var time = new FakeTimeProvider();
        var waiter = new FileTokenStore(location.Path, null, time);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = waiter.AcquireAsync(Key, Timeout, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout, Ct));
        Assert.Equal("SessionBusy", await LockTestProcess.ProbeBusyAsync(location.Path, Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadlineBeforeHandoffReleasesAcquiredHandle(bool cancelAtHandoff)
    {
        using var location = new LockTestDirectory();
        var time = new FakeTimeProvider();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var store = new FileTokenStore(location.Path, null, time, beforeLockHandoff: () =>
        {
            if (cancelAtHandoff) { cancel.Cancel(); }
            else { time.Advance(Timeout); }
        });
        if (cancelAtHandoff)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AcquireAsync(Key, Timeout, cancel.Token).AsTask());
        }
        else
        {
            Assert.Equal(AuthErrorCode.SessionBusy, (await Assert.ThrowsAsync<CliAuthException>(() =>
                store.AcquireAsync(Key, Timeout, Ct).AsTask())).ErrorCode);
        }
        Assert.Equal("ACQUIRED", await LockTestProcess.ProbeAcquireAsync(location.Path, Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildHolderExcludesParentAndIndependentKeysProgress(bool disableImplicitLocks)
    {
        using var location = new LockTestDirectory();
        await using var child = await LockTestProcess.HoldAsync(location.Path, Key, disableImplicitLocks);
        var store = new FileTokenStore(location.Path);
        Assert.Equal(AuthErrorCode.SessionBusy, (await Assert.ThrowsAsync<CliAuthException>(() =>
            store.AcquireAsync(Key, TimeSpan.FromMilliseconds(60), Ct).AsTask())).ErrorCode);
        var independent = new SessionKey(new string('b', 64));
        Assert.Equal("ACQUIRED", await LockTestProcess.ProbeAcquireAsync(location.Path, independent));
        await using var lease = await store.AcquireAsync(independent, Timeout, Ct);
        await child.ReleaseAsync();
        await using var successor = await store.AcquireAsync(Key, Timeout, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentHolderExcludesChildIncludingDisabledImplicitLocking(bool disableImplicitLocks)
    {
        using var location = new LockTestDirectory();
        var lease = await new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct);
        try { Assert.Equal("SessionBusy", await LockTestProcess.ProbeBusyAsync(location.Path, Key, disableImplicitLocks)); }
        finally { await lease.DisposeAsync(); }
        Assert.Equal("ACQUIRED", await LockTestProcess.ProbeAcquireAsync(location.Path, Key, disableImplicitLocks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KilledHolderReleasesPersistentLock(bool disableImplicitLocks)
    {
        using var location = new LockTestDirectory();
        await using var child = await LockTestProcess.HoldAsync(location.Path, Key, disableImplicitLocks);
        await child.KillAsync();
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(location.Path, Key.Value + ".lock")));
        Assert.Equal("ACQUIRED", await LockTestProcess.ProbeAcquireAsync(location.Path, Key));
    }

    [Fact]
    public async Task ConcurrentAndRepeatedDisposalCannotReleaseSuccessorsLease()
    {
        using var location = new LockTestDirectory();
        var store = new FileTokenStore(location.Path);
        var first = await store.AcquireAsync(Key, Timeout, Ct);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await first.DisposeAsync(), Ct)));
        await using var second = await store.AcquireAsync(Key, Timeout, Ct);
        await first.DisposeAsync();
        Assert.Equal("SessionBusy", await LockTestProcess.ProbeBusyAsync(location.Path, Key));
    }

    [Fact]
    public async Task SpawnedChildDoesNotInheritParentsUnrelatedLease()
    {
        using var location = new LockTestDirectory();
        var parent = await new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct);
        try
        {
            await using var child = await LockTestProcess.HoldAsync(location.Path, new SessionKey(new string('b', 64)));
            await parent.DisposeAsync();
            Assert.Equal("ACQUIRED", await LockTestProcess.ProbeAcquireAsync(location.Path, Key));
            // Release through the protocol rather than relying on disposal's kill path.
            await child.ReleaseAsync();
        }
        finally { await parent.DisposeAsync(); }
    }
}

internal sealed class LockTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Windows can report a killed child as exited shortly before its file handles close,
        // and scanners may briefly open new files. Retry for a bounded time; a genuine leak still fails.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
                else if (System.IO.File.Exists(Path)) { System.IO.File.Delete(Path); }
                return;
            }
            catch (Exception error) when (attempt < 40 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
