using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

public sealed class AtomicFileWriteTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static StoredSession Session(string access, long generation = 1) =>
        new(generation, Guid.NewGuid(), SessionLifecycleState.Active,
            new AccessToken(access, DateTimeOffset.UtcNow.AddMinutes(10), ["openid"]),
            "synthetic-refresh", new Uri("https://issuer.example.test/realm"), "cli", "synthetic-subject");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteStagedFileDoesNotChangeVisibleSessionUntilPublication(bool existing)
    {
        using var location = new TestDirectory();
        var observer = new FileTokenStore(location.Path);
        var original = Session("synthetic-original");
        if (existing) { await observer.WriteAsync(Key, original, TestContext.Current.CancellationToken); }
        var replacement = Session("synthetic-replacement", 2);
        var observed = false;
        var writer = new FileTokenStore(location.Path, async (temporaryPath, ct) =>
        {
            observed = true;
            Assert.Equal(location.Path, System.IO.Path.GetDirectoryName(temporaryPath));
            Assert.EndsWith(".tmp", temporaryPath);
            var staged = SessionFileSerializer.Deserialize(Key, await System.IO.File.ReadAllBytesAsync(temporaryPath, ct));
            Assert.Equal(replacement.Epoch, staged.Epoch);
            Assert.Equal(replacement.AccessToken!.Value, staged.AccessToken!.Value);
            var visible = await observer.ReadAsync(Key, ct);
            if (existing) { Assert.Equal(original.Epoch, visible!.Epoch); }
            else { Assert.Null(visible); }
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(temporaryPath));
            }
        });
        await writer.WriteAsync(Key, replacement, TestContext.Current.CancellationToken);
        Assert.True(observed);
        Assert.Equal(replacement.Epoch, (await observer.ReadAsync(Key, TestContext.Current.CancellationToken))!.Epoch);
        Assert.Equal([location.SessionPath], Directory.GetFiles(location.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforePublicationPreservesOriginalAndRemovesOwnedTemporary(bool existing)
    {
        using var location = new TestDirectory();
        var observer = new FileTokenStore(location.Path);
        var original = Session("synthetic-original");
        if (existing) { await observer.WriteAsync(Key, original, TestContext.Current.CancellationToken); }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var writer = new FileTokenStore(location.Path, (_, _) =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Key, Session("synthetic-new"), cancel.Token).AsTask());
        var visible = await observer.ReadAsync(Key, TestContext.Current.CancellationToken);
        if (existing) { Assert.Equal(original.Epoch, visible!.Epoch); }
        else { Assert.Null(visible); }
        Assert.Empty(Directory.GetFiles(location.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforePublicationPreservesOriginalAndCleansUp(bool existing)
    {
        using var location = new TestDirectory();
        var observer = new FileTokenStore(location.Path);
        var original = Session("synthetic-original");
        if (existing) { await observer.WriteAsync(Key, original, TestContext.Current.CancellationToken); }
        var writer = new FileTokenStore(location.Path, (_, _) => throw new IOException("synthetic-secret-I/O-details"));
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            writer.WriteAsync(Key, Session("synthetic-new"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString());
        var visible = await observer.ReadAsync(Key, TestContext.Current.CancellationToken);
        if (existing) { Assert.Equal(original.Epoch, visible!.Epoch); }
        else { Assert.Null(visible); }
        Assert.Empty(Directory.GetFiles(location.Path, "*.tmp"));
    }

    [Fact]
    public async Task NativePublicationFailureDoesNotDamageExistingDestination()
    {
        using var location = new TestDirectory();
        var observer = new FileTokenStore(location.Path);
        var original = Session("synthetic-original");
        await observer.WriteAsync(Key, original, TestContext.Current.CancellationToken);
        var writer = new FileTokenStore(location.Path, (temporaryPath, _) =>
        {
            // Simulate losing the staged file before rename; the native move must fail safely.
            System.IO.File.Delete(temporaryPath);
            return Task.CompletedTask;
        });
        Assert.Equal(AuthErrorCode.StorageUnavailable, (await Assert.ThrowsAsync<CliAuthException>(() =>
            writer.WriteAsync(Key, Session("synthetic-new"), TestContext.Current.CancellationToken).AsTask())).ErrorCode);
        Assert.Equal(original.Epoch, (await observer.ReadAsync(Key, TestContext.Current.CancellationToken))!.Epoch);
        Assert.Equal([location.SessionPath], Directory.GetFiles(location.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingReaderRetainsOldFileWhileNewReaderSeesReplacement(bool unicodeDirectory)
    {
        using var location = new TestDirectory();
        var directory = unicodeDirectory ? System.IO.Path.Combine(location.Path, "session-é-会話") : location.Path;
        var path = System.IO.Path.Combine(directory, Key.Value + ".json");
        var store = new FileTokenStore(directory);
        var original = Session("synthetic-original");
        await store.WriteAsync(Key, original, TestContext.Current.CancellationToken);
        await using var oldHandle = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var replacement = Session("synthetic-new", 2);
        await store.WriteAsync(Key, replacement, TestContext.Current.CancellationToken);
        using var oldBytes = new MemoryStream();
        await oldHandle.CopyToAsync(oldBytes, TestContext.Current.CancellationToken);
        Assert.Equal(original.Epoch, SessionFileSerializer.Deserialize(Key, oldBytes.ToArray()).Epoch);
        Assert.Equal(replacement.Epoch, (await store.ReadAsync(Key, TestContext.Current.CancellationToken))!.Epoch);
    }

    [Fact]
    public async Task CleanupDoesNotDeleteAnotherAttemptsTemporaryFile()
    {
        using var location = new TestDirectory();
        Directory.CreateDirectory(location.Path);
        var unrelated = System.IO.Path.Combine(location.Path, ".other-attempt.tmp");
        await System.IO.File.WriteAllTextAsync(unrelated, "synthetic-other-attempt", TestContext.Current.CancellationToken);
        var writer = new FileTokenStore(location.Path, (_, _) => throw new IOException());
        await Assert.ThrowsAsync<CliAuthException>(() => writer.WriteAsync(Key, Session("synthetic-new"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("synthetic-other-attempt", await System.IO.File.ReadAllTextAsync(unrelated, TestContext.Current.CancellationToken));
        Assert.Equal([unrelated], Directory.GetFiles(location.Path));
    }

    [Fact]
    public async Task ReplacementUsesStagedFilePermissions()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session("synthetic-original"), TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(location.SessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        await store.WriteAsync(Key, Session("synthetic-new"), TestContext.Current.CancellationToken);
        Assert.Equal("synthetic-new", (await store.ReadAsync(Key, TestContext.Current.CancellationToken))!.AccessToken!.Value);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(location.SessionPath));
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-atomic-" + Guid.NewGuid().ToString("N"));
        internal string SessionPath => System.IO.Path.Combine(Path, Key.Value + ".json");
        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
