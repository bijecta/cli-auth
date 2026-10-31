using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

// ADR 0009 location rules. Unix- and Windows-specific cases skip on the other platform.
public sealed class FileSystemProtectionTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static readonly SessionKey OtherKey = new(new string('b', 64));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static StoredSession Session() =>
        new(1, Guid.NewGuid(), SessionLifecycleState.Active,
            new AccessToken("synthetic-access", DateTimeOffset.UtcNow.AddMinutes(10), ["openid"]),
            "synthetic-refresh", new Uri("https://issuer.example.test/realm"), "cli", "synthetic-subject");

    [Theory]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite)]
    public async Task UnixDirectoryWritableByOthersIsRefusedButLogoutStillWorks(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix permission bits."); return; }
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), Ct);
        System.IO.File.SetUnixFileMode(location.Path, Private | UnixFileMode.GroupRead | UnixFileMode.OtherRead | extra);

        await AssertInsecure(() => store.ReadAsync(Key, Ct).AsTask());
        await AssertInsecure(() => store.WriteAsync(OtherKey, Session(), Ct).AsTask());
        await AssertInsecure(() => store.AcquireAsync(Key, Timeout, Ct).AsTask());
        await store.DeleteAsync(Key, Ct);
        Assert.False(System.IO.File.Exists(location.SessionPath(Key)));
    }

    [Fact]
    public async Task UnixDirectoryReadableByOthersIsAccepted()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix permission bits."); return; }
        using var location = new TestDirectory();
        Directory.CreateDirectory(location.Path);
        System.IO.File.SetUnixFileMode(location.Path,
            Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var store = new FileTokenStore(location.Path);
        await using (await store.AcquireAsync(Key, Timeout, Ct))
        {
            await store.WriteAsync(Key, Session(), Ct);
        }
        Assert.NotNull(await store.ReadAsync(Key, Ct));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.OtherWrite)]
    public async Task UnixSessionFileWithGroupOrOtherBitsIsRefused(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix permission bits."); return; }
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), Ct);
        System.IO.File.SetUnixFileMode(location.SessionPath(Key), UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);
        await AssertInsecure(() => store.ReadAsync(Key, Ct).AsTask());
    }

    [Fact]
    public async Task SymlinkedSessionFileIsRefusedAndLogoutRemovesOnlyTheLink()
    {
        using var location = new TestDirectory();
        using var elsewhere = new TestDirectory();
        var target = System.IO.Path.Combine(elsewhere.Path, "target.json");
        await PrivateTestFiles.WriteAsync(target, "synthetic-target", Ct);
        PrivateTestFiles.CreateDirectory(location.Path);
        CreateSymlinkOrSkip(location.SessionPath(Key), target);
        var store = new FileTokenStore(location.Path);
        await AssertInsecure(() => store.ReadAsync(Key, Ct).AsTask());
        await store.DeleteAsync(Key, Ct);
        Assert.False(System.IO.File.Exists(location.SessionPath(Key)));
        Assert.Equal("synthetic-target", await System.IO.File.ReadAllTextAsync(target, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymlinkedLockFileIsRefusedEvenWhenDangling(bool dangling)
    {
        using var location = new TestDirectory();
        using var elsewhere = new TestDirectory();
        var target = System.IO.Path.Combine(elsewhere.Path, "target.lock");
        if (!dangling) { await PrivateTestFiles.WriteAsync(target, "", Ct); }
        PrivateTestFiles.CreateDirectory(location.Path);
        CreateSymlinkOrSkip(System.IO.Path.Combine(location.Path, Key.Value + ".lock"), target);
        await AssertInsecure(() => new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct).AsTask());
        Assert.Equal(!dangling, System.IO.File.Exists(target));
    }

    [Fact]
    public async Task SymlinkedDirectoryIsCheckedThroughItsTarget()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix permission bits on the target."); return; }
        using var target = new TestDirectory();
        using var links = new TestDirectory();
        PrivateTestFiles.CreateDirectory(target.Path);
        PrivateTestFiles.CreateDirectory(links.Path);
        var linked = System.IO.Path.Combine(links.Path, "store");
        Directory.CreateSymbolicLink(linked, target.Path);
        var store = new FileTokenStore(linked);
        await store.WriteAsync(Key, Session(), Ct);
        Assert.NotNull(await store.ReadAsync(Key, Ct));
        System.IO.File.SetUnixFileMode(target.Path, Private | UnixFileMode.OtherWrite);
        await AssertInsecure(() => store.ReadAsync(Key, Ct).AsTask());
    }

    [Fact]
    public async Task SuccessfulWriteRemovesOnlyThisKeysOrphanedStagingFiles()
    {
        using var location = new TestDirectory();
        var orphan = System.IO.Path.Combine(location.Path, $".{Key.Value}.{Guid.NewGuid():N}.tmp");
        var otherKeys = System.IO.Path.Combine(location.Path, $".{OtherKey.Value}.{Guid.NewGuid():N}.tmp");
        await PrivateTestFiles.WriteAsync(orphan, "synthetic-credential-copy", Ct);
        await PrivateTestFiles.WriteAsync(otherKeys, "synthetic-credential-copy", Ct);
        await new FileTokenStore(location.Path).WriteAsync(Key, Session(), Ct);
        Assert.False(System.IO.File.Exists(orphan));
        Assert.True(System.IO.File.Exists(otherKeys));
    }

    [Fact]
    public async Task InsecureErrorsAreSanitized()
    {
        using var location = new TestDirectory();
        PrivateTestFiles.CreateDirectory(location.Path);
        CreateSymlinkOrSkip(System.IO.Path.Combine(location.Path, Key.Value + ".lock"),
            System.IO.Path.Combine(location.Path, "synthetic-target"));
        var error = await AssertInsecure(() => new FileTokenStore(location.Path).AcquireAsync(Key, Timeout, Ct).AsTask());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(location.Path, error.ToString());
    }

    [Fact]
    public void NetworkMountsAreDetectedByTheLongestContainingMountPoint()
    {
        (string, DriveType)[] mounts = [("/", DriveType.Fixed), ("/mnt/share", DriveType.Network), ("/mnt/share/local", DriveType.Fixed)];
        Assert.False(StorageLocation.IsOnNetworkMount("/home/user/.local/state/app", mounts));
        Assert.True(StorageLocation.IsOnNetworkMount("/mnt/share/app", mounts));
        Assert.True(StorageLocation.IsOnNetworkMount("/mnt/share", mounts));
        Assert.False(StorageLocation.IsOnNetworkMount("/mnt/shared/app", mounts));
        Assert.False(StorageLocation.IsOnNetworkMount("/mnt/share/local/app", mounts));
        Assert.False(StorageLocation.IsOnNetworkMount("/anything", []));
    }

    [Fact]
    public void WindowsUncPathsAreNetworkPaths()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows path syntax."); return; }
        Assert.True(StorageLocation.IsNetworkPath(@"\\server\share\app"));
        Assert.True(StorageLocation.IsNetworkPath(@"\\?\UNC\server\share\app"));
        Assert.False(StorageLocation.IsNetworkPath(System.IO.Path.GetTempPath()));
    }

    [Fact]
    public async Task WindowsNewDirectoryGetsAProtectedOwnerOnlyAcl()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows ACLs."); return; }
        using var location = new TestDirectory();
        await new FileTokenStore(location.Path).WriteAsync(Key, Session(), Ct);
        var security = new DirectoryInfo(location.Path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        StorageLocation.RequirePrivateAcl(security);
        StorageLocation.RequirePrivateAcl(new FileInfo(location.SessionPath(Key)).GetAccessControl());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsAccessForOthersIsRefused(bool onFile)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows ACLs."); return; }
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        await store.WriteAsync(Key, Session(), Ct);
        GrantEveryoneRead(onFile ? location.SessionPath(Key) : location.Path, onFile);
        await AssertInsecure(() => store.ReadAsync(Key, Ct).AsTask());
        if (!onFile) { await AssertInsecure(() => store.AcquireAsync(Key, Timeout, Ct).AsTask()); }
        await store.DeleteAsync(Key, Ct);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantEveryoneRead(string path, bool isFile)
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (isFile)
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    private static void CreateSymlinkOrSkip(string link, string target)
    {
        try { System.IO.File.CreateSymbolicLink(link, target); }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Developer Mode or elevation on Windows.");
        }
    }

    private static async Task<CliAuthException> AssertInsecure(Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<CliAuthException>(operation);
        Assert.Equal(AuthErrorCode.StorageInsecure, error.ErrorCode);
        return error;
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-protect-" + Guid.NewGuid().ToString("N"));
        internal string SessionPath(SessionKey key) => System.IO.Path.Combine(Path, key.Value + ".json");

        public void Dispose()
        {
            if (!Directory.Exists(Path)) { return; }
            // Restore owner access so cleanup works after permission tests.
            if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(Path, Private); }
            Directory.Delete(Path, recursive: true);
        }
    }
}
