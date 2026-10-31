using Bijecta.CliAuth.Core;
using static Bijecta.CliAuth.UnitTests.CoordinatorTestData;

namespace Bijecta.CliAuth.UnitTests;

// ADR 0008: explicit login may replace a corrupt record, but only one that is still corrupt at commit.
public sealed class CorruptRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoginReplacesARecordThatStaysCorruptStartingANewGeneration()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile, generation: 9));
        fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageCorrupt);
        await fixture.Auth().LoginAsync("work", new LoginOptions(), Ct);
        var stored = await fixture.ReadAsync();
        Assert.Equal(SessionLifecycleState.Active, stored!.State);
        Assert.Equal("synthetic-subject", stored.Subject);
        Assert.Equal(0, stored.Generation);
    }

    [Fact]
    public async Task LoginIsSupersededWhenAValidSessionReplacesTheCorruptRecordMeanwhile()
    {
        var fixture = new Fixture();
        fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageCorrupt);
        var repaired = Session(fixture.Profile);
        fixture.Protocol.Login = async (profile, _, _, _) =>
        {
            fixture.Store.ReadError = null;
            await fixture.SeedAsync(repaired);
            return Candidate(profile, "late");
        };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.OperationSuperseded, error.ErrorCode);
        Assert.Same(repaired, await fixture.ReadAsync());
    }

    [Fact]
    public async Task ARecordThatBecomesCorruptDuringLoginIsReportedNotReplaced()
    {
        var fixture = new Fixture();
        var original = Session(fixture.Profile);
        await fixture.SeedAsync(original);
        fixture.Protocol.Login = (profile, _, _, _) =>
        {
            fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageCorrupt);
            return Task.FromResult(Candidate(profile));
        };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(AuthErrorCode.StorageCorrupt, error.ErrorCode);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Same(original, await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(AuthErrorCode.UnsupportedSchema)]
    [InlineData(AuthErrorCode.StorageInsecure)]
    [InlineData(AuthErrorCode.StorageUnavailable)]
    public async Task OtherStorageFailuresStillBlockLogin(AuthErrorCode code)
    {
        var fixture = new Fixture();
        fixture.Store.ReadError = new CliAuthException(code);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => fixture.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(0, fixture.Protocol.LoginCalls);
        Assert.Equal(0, fixture.Store.Writes);
    }

    [Fact]
    public async Task InsecureStorageStillAllowsExplicitLogout()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Session(fixture.Profile));
        fixture.Store.ReadError = new CliAuthException(AuthErrorCode.StorageInsecure);
        var result = await fixture.Auth().LogoutAsync("work", new LogoutOptions(true), Ct);
        Assert.True(result.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.Failed, result.RevocationOutcome);
        Assert.Null(await fixture.ReadAsync());
    }

    [Fact]
    public void InsecureStorageHasSafeMessageAndGuidance()
    {
        var error = new CliAuthException(AuthErrorCode.StorageInsecure);
        Assert.Equal("The storage location is not private to the current user.", error.Message);
        Assert.Contains("private local directory", error.RetryGuidance);
    }
}
