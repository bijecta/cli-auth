using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

public sealed class AuthenticatorCompositionTests
{
    [Fact]
    public void ConstructionSnapshotsProfilesWithoutProviderStorageBrowserOrClockWork()
    {
        var fixture = new Fixture();
        var profiles = new List<OidcProfile> { fixture.Profile };
        _ = new CliAuthenticator(profiles, fixture.Store, fixture.Protocol, fixture.Store.Inner,
            fixture.Browser, fixture.Clock);
        profiles.Clear();
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.Equal(0, fixture.Protocol.LoginCalls);
        Assert.Equal(0, fixture.Protocol.RevokeCalls);
        Assert.Equal(0, fixture.Browser.Calls);
        Assert.Equal(0, fixture.Clock.Reads);
    }

    [Fact]
    public async Task SnapshotSurvivesCallerCollectionChangesAndNamesAreOrdinal()
    {
        var store = new MemoryTokenStore();
        var profiles = new List<OidcProfile> { Profile("work"), Profile("Work") };
        var auth = new CliAuthenticator(profiles, store);
        profiles.Clear();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("Work", ct)).Kind);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetStatusAsync("WORK", ct));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Fact]
    public void RejectsDuplicateProfilesMissingLocksAndForeignIntrinsicLocks()
    {
        var memory = new MemoryTokenStore();
        AssertConfiguration(() => new CliAuthenticator([Profile(), Profile()], memory));
        AssertConfiguration(() => new CliAuthenticator([], memory));
        AssertConfiguration(() => new CliAuthenticator(null!, memory));
        AssertConfiguration(() => new CliAuthenticator([Profile()], null!));
        AssertConfiguration(() => new CliAuthenticator([null!], memory));
        AssertConfiguration(() => new CliAuthenticator([Profile()], new Store()));
        AssertConfiguration(() => new CliAuthenticator([Profile()], memory, sessionLocks: new MemoryTokenStore()));
    }

    [Fact]
    public void PersistenceMustMatchTheBackendForEveryProfile()
    {
        Assert.Equal(PersistenceMode.MemoryOnly, new MemoryTokenStore().PersistenceMode);
        AssertConfiguration(() => new CliAuthenticator([Profile(persistence: PersistenceMode.Protected)], new MemoryTokenStore()));
        AssertConfiguration(() => new CliAuthenticator([Profile(), Profile("other", PersistenceMode.Protected)], new MemoryTokenStore()));
        var protectedFake = new Store(PersistenceMode.Protected);
        AssertConfiguration(() => new CliAuthenticator([Profile()], protectedFake, protectedFake.Inner));
        _ = new CliAuthenticator([Profile(persistence: PersistenceMode.Protected)], protectedFake, protectedFake.Inner);
        var invalid = new Store((PersistenceMode)99);
        AssertConfiguration(() => new CliAuthenticator([Profile()], invalid, invalid.Inner));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void RejectsInvalidLockTimeouts(long milliseconds)
    {
        AssertConfiguration(() => new CliAuthenticator([Profile()], new MemoryTokenStore(),
            sessionLockTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task PublicInstanceHasLocalOperationsAndDeviceRemainsUnsupported()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new MemoryTokenStore();
        var auth = new CliAuthenticator([Profile()], memory);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        var missing = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct));
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, missing.ErrorCode);
        var unsupported = await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", new LoginOptions(LoginFlow.Device, (_, _) => Task.CompletedTask), ct));
        Assert.Equal(AuthErrorCode.UnsupportedCapability, unsupported.ErrorCode);
        Assert.True((await auth.LogoutAsync("work", new LogoutOptions(), ct)).LocalSessionRemoved);
    }

    [Fact]
    public async Task InvalidLockLeaseFailsBeforeAnyStoreRead()
    {
        var store = new Store();
        var auth = new CliAuthenticator([Profile()], store, new EmptyLocks());
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            auth.GetStatusAsync("work", TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCode.StorageUnavailable, error.ErrorCode);
        Assert.Equal(0, store.Reads);
    }

    private sealed class EmptyLocks : ISessionLockProvider
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, TimeSpan timeout, CancellationToken ct) =>
            ValueTask.FromResult<IAsyncDisposable>(null!);
    }

    private static void AssertConfiguration(Func<CliAuthenticator> construct) =>
        Assert.Equal(AuthErrorCode.InvalidConfiguration, Assert.Throws<CliAuthException>(construct).ErrorCode);
}
