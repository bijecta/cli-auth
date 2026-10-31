using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

public sealed class FileLogoutTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static readonly SessionKey OtherKey = new(new string('b', 64));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static StoredSession Session(string access = "synthetic-access") =>
        new(1, Guid.NewGuid(), SessionLifecycleState.Active,
            new AccessToken(access, DateTimeOffset.UtcNow.AddMinutes(10), ["openid"]),
            "synthetic-refresh", new Uri("https://issuer.example.test/realm"), "cli", "synthetic-subject");

    [Fact]
    public async Task DeleteRemovesTheRecordForEveryInstanceAndRetainsTheLockFile()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await using (await store.AcquireAsync(Key, TimeSpan.FromSeconds(5), Ct))
        {
            await store.WriteAsync(Key, Session(), Ct);
            await store.WriteAsync(OtherKey, Session(), Ct);
            await store.DeleteAsync(Key, Ct);
        }
        Assert.False(System.IO.File.Exists(location.SessionPath(Key)));
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(location.Path, Key.Value + ".lock")));
        Assert.Null(await new FileTokenStore(location.Path).ReadAsync(Key, Ct));
        Assert.NotNull(await new FileTokenStore(location.Path).ReadAsync(OtherKey, Ct));
    }

    [Fact]
    public async Task MissingDirectoryOrRecordAndRepeatedDeleteSucceedWithoutCreatingAnything()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.DeleteAsync(Key, Ct);
        Assert.False(Directory.Exists(location.Path));
        await store.WriteAsync(OtherKey, Session(), Ct);
        await store.DeleteAsync(Key, Ct);
        await store.DeleteAsync(Key, Ct);
        Assert.Equal([location.SessionPath(OtherKey)], Directory.GetFiles(location.Path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":99}")]
    public async Task UnreadableRecordsCanOnlyBeRemovedByExplicitDelete(string content)
    {
        using var location = new TestDirectory();
        await PrivateTestFiles.WriteAsync(location.SessionPath(Key), content, Ct);
        var store = new FileTokenStore(location.Path);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => store.ReadAsync(Key, Ct).AsTask());
        Assert.True(error.ErrorCode is AuthErrorCode.StorageCorrupt or AuthErrorCode.UnsupportedSchema);
        // Reading never repairs the record; explicit removal does not need to parse it.
        Assert.Equal(content, await System.IO.File.ReadAllTextAsync(location.SessionPath(Key), Ct));
        await store.DeleteAsync(Key, Ct);
        Assert.Null(await store.ReadAsync(Key, Ct));
    }

    [Fact]
    public async Task OnlyThisKeysOrphanedStagingFilesAreRemoved()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), Ct);
        string Named(string name) => System.IO.Path.Combine(location.Path, name);
        var orphan = Named($".{Key.Value}.{Guid.NewGuid():N}.tmp");
        var kept = new[]
        {
            Named($".{OtherKey.Value}.{Guid.NewGuid():N}.tmp"),
            Named($".{Key.Value}.{Guid.NewGuid():N}".ToUpperInvariant() + ".tmp"),
            Named($".{Key.Value}.not-a-staging-name.tmp"),
            Named($".{Key.Value}.{Guid.NewGuid():N}.tmp.bak"),
            Named("unrelated.txt")
        };
        foreach (var path in kept.Append(orphan))
        {
            await System.IO.File.WriteAllTextAsync(path, "synthetic-credential-copy", Ct);
        }
        await store.DeleteAsync(Key, Ct);
        Assert.False(System.IO.File.Exists(orphan));
        Assert.All(kept, path => Assert.True(System.IO.File.Exists(path)));
    }

    [Fact]
    public async Task OpenReaderDoesNotPreventDeleteOrAnImmediateNewLogin()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session("synthetic-old"), Ct);
        await using var reader = new FileStream(location.SessionPath(Key), FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await store.DeleteAsync(Key, Ct);
        Assert.Null(await store.ReadAsync(Key, Ct));
        await store.WriteAsync(Key, Session("synthetic-new"), Ct);
        Assert.Equal("synthetic-new", (await store.ReadAsync(Key, Ct))!.AccessToken!.Value);
        // The removed record remains readable only through the handle opened before removal.
        using var old = new StreamReader(reader);
        Assert.Contains("synthetic-old", await old.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task CancellationBeforeRemovalPreservesTheRecord()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), Ct);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(Key, cancel.Token).AsTask());
        Assert.NotNull(await store.ReadAsync(Key, Ct));
    }

    [Fact]
    public async Task RemovalFailureIsSanitizedStorageUnavailable()
    {
        using var location = new TestDirectory();
        Directory.CreateDirectory(location.SessionPath(Key));
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            new FileTokenStore(location.Path).DeleteAsync(Key, Ct).AsTask());
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(location.Path, error.ToString());
        Assert.True(Directory.Exists(location.SessionPath(Key)));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdefa", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    public void StagingFileNamesMatchOnlyWriteAsyncOutput(string suffix, bool expected)
    {
        Assert.Equal(expected, FileTokenStore.IsStagingFileName($".{Key.Value}.{suffix}.tmp", Key));
        Assert.False(FileTokenStore.IsStagingFileName($".{OtherKey.Value}.{suffix}.tmp", Key));
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-logout-" + Guid.NewGuid().ToString("N"));
        internal string SessionPath(SessionKey key) => System.IO.Path.Combine(Path, key.Value + ".json");
        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
