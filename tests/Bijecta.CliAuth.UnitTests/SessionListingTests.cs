using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

public sealed class SessionListingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsEveryConfiguredProfileInConfigurationOrder()
    {
        var store = new Store();
        OidcProfile[] profiles = [Profile("zeta"), Profile("alpha"), Profile("middle")];
        await store.Inner.WriteAsync(SessionKey.ForProfile(profiles[0]), Session(profiles[0]), Ct);
        await store.Inner.WriteAsync(SessionKey.ForProfile(profiles[2]),
            new StoredSession(0, Guid.NewGuid(), SessionLifecycleState.ReauthenticationRequired), Ct);
        var auth = new CliAuthenticator(profiles, store, new Protocol(), store.Inner, new Browser(), new Clock());

        var entries = await auth.ListSessionsAsync(Ct);

        Assert.Equal(["zeta", "alpha", "middle"], entries.Select(entry => entry.ProfileName));
        Assert.All(entries, entry => Assert.Null(entry.Error));
        Assert.Equal(SessionStatusKind.Available, entries[0].Status!.Kind);
        Assert.Equal("synthetic-cached-subject", entries[0].Status!.Subject);
        Assert.Equal(SessionStatusKind.None, entries[1].Status!.Kind);
        Assert.Equal(SessionStatusKind.ReauthenticationRequired, entries[2].Status!.Kind);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task UnreadableOrBusyProfilesAreReportedWithoutHidingOthers()
    {
        var store = new Store();
        var work = Profile("work");
        var corrupt = Profile("corrupt");
        var busy = Profile("busy");
        await store.Inner.WriteAsync(SessionKey.ForProfile(work), Session(work), Ct);
        // A record whose scopes exceed the profile's binding is reported as StorageCorrupt.
        var widened = CoreTestData.Profile(name: "corrupt", scopes: ["api.read", "admin"]);
        await store.Inner.WriteAsync(SessionKey.ForProfile(corrupt), Session(widened), Ct);
        await using var held = await store.Inner.AcquireAsync(SessionKey.ForProfile(busy), TimeSpan.FromSeconds(5), Ct);
        var auth = new CliAuthenticator([corrupt, busy, work], store, new Protocol(), store.Inner, new Browser(),
            new Clock(), TimeSpan.FromMilliseconds(50));

        var entries = await auth.ListSessionsAsync(Ct);

        Assert.Equal(AuthErrorCode.StorageCorrupt, entries[0].Error);
        Assert.Null(entries[0].Status);
        Assert.Equal(AuthErrorCode.SessionBusy, entries[1].Error);
        Assert.Equal(SessionStatusKind.Available, entries[2].Status!.Kind);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAProfileError()
    {
        var store = new Store();
        var auth = new CliAuthenticator([Profile()], store, new Protocol(), store.Inner, new Browser(), new Clock());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.ListSessionsAsync(cancel.Token));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public void EntriesValidateInputAndFormatWithoutPersonalMetadata()
    {
        var status = new SessionStatus(SessionStatusKind.Available, subject: "synthetic-subject");
        var entry = new ProfileSessionStatus("work", status);
        Assert.Same(status, entry.Status);
        Assert.DoesNotContain("synthetic-subject", entry.ToString());
        Assert.Equal("ProfileSessionStatus (work, Available)", entry.ToString());
        Assert.Equal("ProfileSessionStatus (work, StorageCorrupt)",
            new ProfileSessionStatus("work", AuthErrorCode.StorageCorrupt).ToString());
        Assert.Equal(AuthErrorCode.InvalidRequest,
            Assert.Throws<CliAuthException>(() => new ProfileSessionStatus("work", (SessionStatus)null!)).ErrorCode);
        Assert.Equal(AuthErrorCode.InvalidRequest,
            Assert.Throws<CliAuthException>(() => new ProfileSessionStatus("work", (AuthErrorCode)999)).ErrorCode);
        Assert.Equal(AuthErrorCode.InvalidRequest,
            Assert.Throws<CliAuthException>(() => new ProfileSessionStatus(" ", status)).ErrorCode);
    }
}
