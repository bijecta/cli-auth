using System.Text.Json.Nodes;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

public sealed class FileTokenStoreTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static StoredSession Session(long generation = 1, string access = "synthetic-access") =>
        new(generation, Guid.NewGuid(), SessionLifecycleState.Active,
            new AccessToken(access, DateTimeOffset.UtcNow.AddMinutes(10), ["openid"]),
            "synthetic-refresh", new Uri("https://issuer.example.test/realm"), "cli", "synthetic-subject");

    [Fact]
    public async Task ConstructionAndMissingReadsDoNotCreateTheDirectory()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        Assert.Equal(System.IO.Path.GetFullPath(location.Path), store.StorageDirectory);
        Assert.False(Directory.Exists(location.Path));
        Assert.Null(await store.ReadAsync(Key, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(location.Path));
        Assert.DoesNotContain(location.Path, store.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-directory")]
    public void DirectoryMustBeExplicitAndAbsolute(string? path)
    {
        var error = Assert.Throws<CliAuthException>(() => new FileTokenStore(path!));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task WriteCreatesCompactFileAndAnotherInstanceCanReadIt()
    {
        using var location = new TestDirectory();
        var session = Session();
        await new FileTokenStore(location.Path).WriteAsync(Key, session, TestContext.Current.CancellationToken);
        var path = System.IO.Path.Combine(location.Path, Key.Value + ".json");
        var text = await System.IO.File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\n', text);
        Assert.Contains("synthetic-access", text);
        Assert.Equal([path], Directory.GetFiles(location.Path));
        var result = await new FileTokenStore(location.Path).ReadAsync(Key, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(session.Generation, result.Generation);
        Assert.Equal(session.Epoch, result.Epoch);
        Assert.Equal(session.Issuer!.OriginalString, result.Issuer!.OriginalString);
        Assert.Equal(session.Subject, result.Subject);
        Assert.Equal(session.AccessToken!.Value, result.AccessToken!.Value);
        Assert.Equal(session.AccessToken.ExpiresAtUtc, result.AccessToken.ExpiresAtUtc);
        Assert.Equal(session.RefreshToken, result.RefreshToken);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                System.IO.File.GetUnixFileMode(location.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task ReplacementTruncatesOldContentsAndKeysRemainIndependent()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        var other = new SessionKey(new string('b', 64));
        await store.WriteAsync(Key, Session(access: new string('x', 8192)), TestContext.Current.CancellationToken);
        await store.WriteAsync(other, Session(access: "synthetic-other"), TestContext.Current.CancellationToken);
        var replacement = Session(generation: 2, access: "synthetic-replacement");
        await store.WriteAsync(Key, replacement, TestContext.Current.CancellationToken);
        var result = await store.ReadAsync(Key, TestContext.Current.CancellationToken);
        Assert.Equal("synthetic-replacement", result!.AccessToken!.Value);
        Assert.Equal(2, result.Generation);
        Assert.Equal("synthetic-other", (await store.ReadAsync(other, TestContext.Current.CancellationToken))!.AccessToken!.Value);
        Assert.Null(await store.ReadAsync(new SessionKey(new string('c', 64)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeparateDirectoriesAreIndependent()
    {
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        await new FileTokenStore(first.Path).WriteAsync(Key, Session(), TestContext.Current.CancellationToken);
        Assert.Null(await new FileTokenStore(second.Path).ReadAsync(Key, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(second.Path));
    }

    [Theory]
    [InlineData("malformed", AuthErrorCode.StorageCorrupt)]
    [InlineData("wrong_key", AuthErrorCode.StorageCorrupt)]
    [InlineData("schema", AuthErrorCode.UnsupportedSchema)]
    [InlineData("oversize", AuthErrorCode.StorageCorrupt)]
    public async Task InvalidFilesFailSafelyAndAreNotRewritten(string scenario, AuthErrorCode expected)
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), TestContext.Current.CancellationToken);
        var path = System.IO.Path.Combine(location.Path, Key.Value + ".json");
        var document = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        if (scenario == "wrong_key") { document["sessionKey"] = new string('b', 64); }
        if (scenario == "schema") { document["schemaVersion"] = 2; }
        var invalid = scenario switch
        {
            "malformed" => "{synthetic-secret-malformed",
            "oversize" => new string('x', SessionFileSerializer.MaxDocumentBytes + 1),
            _ => document.ToJsonString()
        };
        await System.IO.File.WriteAllTextAsync(path, invalid, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => store.ReadAsync(Key, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(expected, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString());
        Assert.DoesNotContain(location.Path, error.ToString());
        Assert.Equal(invalid, await System.IO.File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationBeforeOperationsHasNoFilesystemSideEffects()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Key, Session(), cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Key, cancel.Token).AsTask());
        Assert.False(Directory.Exists(location.Path));
    }

    [Fact]
    public async Task InvalidWriteDoesNotTruncateAnExistingSession()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), TestContext.Current.CancellationToken);
        var oversized = Session(access: new string('x', SessionFileSerializer.MaxDocumentBytes));
        Assert.Equal(AuthErrorCode.InvalidRequest, (await Assert.ThrowsAsync<CliAuthException>(() =>
            store.WriteAsync(Key, oversized, TestContext.Current.CancellationToken).AsTask())).ErrorCode);
        Assert.Equal("synthetic-access", (await store.ReadAsync(Key, TestContext.Current.CancellationToken))!.AccessToken!.Value);
    }

    [Fact]
    public async Task FilesystemFailuresAreSafeStorageUnavailableErrors()
    {
        using var location = new TestDirectory();
        Directory.CreateDirectory(location.Path);
        var directoryAsFile = System.IO.Path.Combine(location.Path, Key.Value + ".json");
        Directory.CreateDirectory(directoryAsFile);
        var store = new FileTokenStore(location.Path);
        foreach (var operation in new Func<Task>[]
        {
            () => store.ReadAsync(Key, TestContext.Current.CancellationToken).AsTask(),
            () => store.WriteAsync(Key, Session(), TestContext.Current.CancellationToken).AsTask()
        })
        {
            var error = await Assert.ThrowsAsync<CliAuthException>(operation);
            Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(location.Path, error.ToString());
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-filestore-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
