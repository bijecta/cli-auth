using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class SessionStatusTests
{
    [Theory]
    [InlineData(61, true, SessionStatusKind.Available)]
    [InlineData(60, true, SessionStatusKind.RefreshRequired)]
    [InlineData(59, true, SessionStatusKind.RefreshRequired)]
    [InlineData(0, true, SessionStatusKind.RefreshRequired)]
    [InlineData(-1, false, SessionStatusKind.ReauthenticationRequired)]
    [InlineData(60, false, SessionStatusKind.ReauthenticationRequired)]
    public void AppliesStrictExpiryBoundaryWithoutNetworkOrRefresh(int seconds, bool hasRefresh, SessionStatusKind expected)
    {
        var clock = new CoreTestData.Clock(CoreTestData.Now);
        var session = CoreTestData.Session(clock.Now.AddSeconds(seconds), hasRefresh ? "synthetic-refresh" : null);
        Assert.Equal(expected, SessionStatusEvaluator.Evaluate(session, clock, TimeSpan.FromSeconds(60)).Kind);
    }

    [Fact]
    public void FakeTimeCanChangeAvailabilityWithoutMutatingTheSession()
    {
        var clock = new CoreTestData.Clock(CoreTestData.Now);
        var session = CoreTestData.Session(clock.Now.AddSeconds(61));
        Assert.Equal(SessionStatusKind.Available, SessionStatusEvaluator.Evaluate(session, clock, TimeSpan.FromSeconds(60)).Kind);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(SessionStatusKind.RefreshRequired, SessionStatusEvaluator.Evaluate(session, clock, TimeSpan.FromSeconds(60)).Kind);
    }

    [Theory]
    [InlineData(SessionLifecycleState.None, SessionStatusKind.None)]
    [InlineData(SessionLifecycleState.ReauthenticationRequired, SessionStatusKind.ReauthenticationRequired)]
    [InlineData(SessionLifecycleState.Uncertain, SessionStatusKind.Uncertain)]
    public void CredentialFreeMarkersHaveExplicitLocalStatus(SessionLifecycleState state, SessionStatusKind expected)
    {
        var marker = new StoredSession(1, Guid.NewGuid(), state);
        var status = SessionStatusEvaluator.Evaluate(marker, new CoreTestData.Clock(CoreTestData.Now), TimeSpan.Zero);
        Assert.Equal(expected, status.Kind);
        Assert.Null(status.ExpiresAtUtc);
        Assert.Null(status.Subject);
    }

    [Fact]
    public void MissingSessionIsNoneAndOrphanedRefreshIsUncertain()
    {
        var clock = new CoreTestData.Clock(CoreTestData.Now);
        Assert.Equal(SessionStatusKind.None, SessionStatusEvaluator.Evaluate(null, clock, TimeSpan.Zero).Kind);
        var refreshing = CoreTestData.Session(state: SessionLifecycleState.RefreshInProgress);
        Assert.Equal(SessionStatusKind.Uncertain, SessionStatusEvaluator.Evaluate(refreshing, clock, TimeSpan.Zero).Kind);
    }
}
