using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

// The fake protocol against a real FileTokenStore: each new authenticator stands in for the next CLI run.
public sealed class PlaintextFileCoordinatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static OidcProfile Plaintext(string name = "work") => Profile(name, PersistenceMode.Plaintext);

    [Fact]
    public void PlaintextSelectionIsExplicitOnBothSidesAndConstructionPerformsNoIo()
    {
        using var location = new TestDirectory();
        var store = new FileTokenStore(location.Path);
        Assert.Equal(PersistenceMode.Plaintext, store.PersistenceMode);
        _ = new CliAuthenticator([Plaintext()], store);
        Assert.False(Directory.Exists(location.Path));
        foreach (var mismatch in new[] { PersistenceMode.MemoryOnly, PersistenceMode.Protected })
        {
            AssertConfiguration(() => new CliAuthenticator([Profile(persistence: mismatch)], store));
        }
        AssertConfiguration(() => new CliAuthenticator([Plaintext(), Profile("other")], store));
        AssertConfiguration(() => new CliAuthenticator([Plaintext()], new MemoryTokenStore()));
        // The store's own lease domain is canonical; a different lock provider is rejected.
        AssertConfiguration(() => new CliAuthenticator([Plaintext()], store, new MemoryTokenStore()));
    }

    [Fact]
    public async Task SessionPersistsAcrossRunsUntilLogoutRemovesItsFile()
    {
        using var location = new TestDirectory();
        var profile = Plaintext();
        var login = await Run(location).LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(PersistenceMode.Plaintext, login.Persistence);
        var sessionFile = Path.Combine(location.Path, SessionKey.ForProfile(profile).Value + ".json");
        Assert.True(File.Exists(sessionFile));

        var next = Run(location);
        var status = await next.GetStatusAsync("work", Ct);
        Assert.Equal(SessionStatusKind.Available, status.Kind);
        Assert.Equal(login.Subject, status.Subject);
        Assert.Equal("synthetic-access-synthetic-subject", (await next.GetAccessTokenAsync("work", Ct)).Value);
        Assert.Equal(SessionStatusKind.Available, Assert.Single(await next.ListSessionsAsync(Ct)).Status!.Kind);

        Assert.True((await next.LogoutAsync("work", new LogoutOptions(), Ct)).LocalSessionRemoved);
        Assert.False(File.Exists(sessionFile));
        Assert.Equal(SessionStatusKind.None, (await Run(location).GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task ProfilesSharingTheDirectoryAreListedIndependently()
    {
        using var location = new TestDirectory();
        OidcProfile[] profiles = [Plaintext("work"), Plaintext("personal")];
        await Run(location, profiles).LoginAsync("personal", new LoginOptions(), Ct);
        var entries = await Run(location, profiles).ListSessionsAsync(Ct);
        Assert.Equal(["work", "personal"], entries.Select(entry => entry.ProfileName));
        Assert.Equal(SessionStatusKind.None, entries[0].Status!.Kind);
        Assert.Equal(SessionStatusKind.Available, entries[1].Status!.Kind);
    }

    [Fact]
    public async Task LogoutFromAnotherRunSupersedesALoginStartedWhileSignedIn()
    {
        using var location = new TestDirectory();
        await Run(location).LoginAsync("work", new LoginOptions(), Ct);
        var entered = Signal<bool>();
        var reply = Signal<ValidatedLogin>();
        var protocol = new Protocol
        {
            Login = async (_, _, _, token) =>
            {
                entered.SetResult(true);
                return await reply.Task.WaitAsync(token);
            }
        };
        var pending = Run(location, protocol: protocol).LoginAsync("work", new LoginOptions(), Ct);
        await Bounded(entered.Task);
        Assert.True((await Run(location).LogoutAsync("work", new LogoutOptions(), Ct)).LocalSessionRemoved);
        reply.SetResult(Candidate(Plaintext(), "late"));
        Assert.Equal(AuthErrorCode.OperationSuperseded,
            (await Assert.ThrowsAsync<CliAuthException>(() => pending)).ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await Run(location).GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task CorruptRecordBlocksUseButExplicitLoginReplacesIt()
    {
        using var location = new TestDirectory();
        var sessionFile = await WriteRecordAsync(location, "{");
        var auth = Run(location);

        await AssertCode(AuthErrorCode.StorageCorrupt, () => auth.GetStatusAsync("work", Ct));
        await AssertCode(AuthErrorCode.StorageCorrupt, () => auth.GetAccessTokenAsync("work", Ct));
        Assert.Equal(AuthErrorCode.StorageCorrupt, Assert.Single(await auth.ListSessionsAsync(Ct)).Error);
        Assert.Equal("{", await File.ReadAllTextAsync(sessionFile, Ct));

        // ADR 0008: login is explicit and gets a fresh epoch, so it may replace a still-corrupt record.
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(SessionStatusKind.Available, (await Run(location).GetStatusAsync("work", Ct)).Kind);
    }

    [Fact]
    public async Task NewerSchemaRecordBlocksLoginUntilExplicitLogout()
    {
        using var location = new TestDirectory();
        var sessionFile = await WriteRecordAsync(location, "{\"schemaVersion\":99}");
        var auth = Run(location);
        await AssertCode(AuthErrorCode.UnsupportedSchema, () => auth.LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal("{\"schemaVersion\":99}", await File.ReadAllTextAsync(sessionFile, Ct));
        Assert.True((await auth.LogoutAsync("work", new LogoutOptions(), Ct)).LocalSessionRemoved);
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", Ct)).Kind);
    }

    // Writes a synthetic record the way the store would: a privately created directory and an owner-only file.
    private static async Task<string> WriteRecordAsync(TestDirectory location, string content)
    {
        _ = await Run(location).ListSessionsAsync(Ct);
        var path = Path.Combine(location.Path, SessionKey.ForProfile(Plaintext()).Value + ".json");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        await using (var stream = new FileStream(path, options))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content.AsMemory(), Ct);
        }
        return path;
    }

    [Fact]
    public async Task OldPlaintextRecordCanBeRemovedByItsKeyWhenMovingPolicies()
    {
        // ADR 0008: moving a profile off Plaintext changes its key; the host deletes the old record.
        using var location = new TestDirectory();
        await Run(location).LoginAsync("work", new LoginOptions(), Ct);
        var plaintextKey = SessionKey.ForProfile(Plaintext());
        Assert.NotEqual(plaintextKey, SessionKey.ForProfile(Profile(persistence: PersistenceMode.Protected)));
        var store = new FileTokenStore(location.Path);
        await using (await store.AcquireAsync(plaintextKey, TimeSpan.FromSeconds(5), Ct))
        {
            await store.DeleteAsync(plaintextKey, Ct);
        }
        Assert.Null(await store.ReadAsync(plaintextKey, Ct));
    }

    private static CliAuthenticator Run(TestDirectory location, OidcProfile[]? profiles = null, Protocol? protocol = null)
    {
        var store = new FileTokenStore(location.Path);
        return new CliAuthenticator(profiles ?? [Plaintext()], store, protocol ?? new Protocol(), store, new Browser(), new Clock());
    }

    private static void AssertConfiguration(Action create) =>
        Assert.Equal(AuthErrorCode.InvalidConfiguration, Assert.Throws<CliAuthException>(create).ErrorCode);

    private static async Task AssertCode(AuthErrorCode expected, Func<Task> operation) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<CliAuthException>(operation)).ErrorCode);

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bijecta-plaintext-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
