using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class MemoryTokenStoreTests
{
    [Fact]
    public async Task StoresImmutableEnvelopesAndSeparatesKeysAndInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var otherKey = SessionKey.ForProfile(CoreTestData.Profile(name: "other"));
        var session = CoreTestData.Session();
        Assert.Null(await store.ReadAsync(key, ct));
        await using var lease = await store.AcquireAsync(key, TimeSpan.FromSeconds(1), ct);
        await store.WriteAsync(key, session, ct);
        Assert.Same(session, await store.ReadAsync(key, ct));
        Assert.Null(await store.ReadAsync(otherKey, ct));
        Assert.Null(await new MemoryTokenStore().ReadAsync(key, ct));
    }

    [Fact]
    public async Task DeleteRemovesTheRecordAndRepeatedDeleteIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var session = CoreTestData.Session(generation: 5);
        await using var lease = await store.AcquireAsync(key, TimeSpan.FromSeconds(1), ct);
        await store.WriteAsync(key, session, ct);
        await store.DeleteAsync(key, ct);
        Assert.Null(await store.ReadAsync(key, ct));
        await store.DeleteAsync(key, ct);
        Assert.Null(await store.ReadAsync(key, ct));
    }

    [Fact]
    public async Task DeleteOfAMissingKeySucceedsWithoutCreatingARecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        await store.DeleteAsync(key, ct);
        Assert.Null(await store.ReadAsync(key, ct));
    }

    [Fact]
    public async Task SameKeyWaitsWhileAnotherKeyCanProgressAndDoubleDisposeIsSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var other = SessionKey.ForProfile(CoreTestData.Profile(name: "other"));
        var first = await store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct);
        var waiting = store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct).AsTask();
        Assert.False(waiting.IsCompleted);
        await using var otherLease = await store.AcquireAsync(other, TimeSpan.FromSeconds(1), ct);
        await first.DisposeAsync();
        await using var second = await waiting;
        await first.DisposeAsync();
        var third = store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct).AsTask();
        Assert.False(third.IsCompleted);
        await second.DisposeAsync();
        await using var thirdLease = await third;
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotReleaseTheOwnerOrCancelAnotherWaiter()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var owner = await store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelled = store.AcquireAsync(key, TimeSpan.FromSeconds(2), cancellation.Token).AsTask();
        var surviving = store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(surviving.IsCompleted);
        await owner.DisposeAsync();
        await using var survivor = await surviving;
    }

    [Fact]
    public async Task LockTimeoutFailsWithoutDiscardingTheLiveOwnersGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var owner = await store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct);
        var failure = await Assert.ThrowsAsync<CliAuthException>(async () =>
            await store.AcquireAsync(key, TimeSpan.FromMilliseconds(20), ct));
        Assert.Equal(AuthErrorCode.SessionBusy, failure.ErrorCode);
        var next = store.AcquireAsync(key, TimeSpan.FromSeconds(2), ct).AsTask();
        Assert.False(next.IsCompleted);
        await owner.DisposeAsync();
        await using var lease = await next;
    }

    [Fact]
    public async Task CancelledMutationLeavesTheExistingSessionUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var session = CoreTestData.Session();
        await store.WriteAsync(key, session, ct);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.DeleteAsync(key, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.WriteAsync(key, CoreTestData.Session(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ReadAsync(key, cancellation.Token));
        Assert.Same(session, await store.ReadAsync(key, ct));
    }

    [Fact]
    public async Task DeleteRemovesASessionAtTheMaximumGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        var session = CoreTestData.Session(generation: long.MaxValue);
        await store.WriteAsync(key, session, ct);
        // Removal computes no successor generation, so it cannot overflow.
        await store.DeleteAsync(key, ct);
        Assert.Null(await store.ReadAsync(key, ct));
    }

    [Fact]
    public async Task OneHundredCooperatingMutationsDoNotLoseGenerations()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryTokenStore();
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        await store.WriteAsync(key, CoreTestData.Session(), ct);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await using var lease = await store.AcquireAsync(key, TimeSpan.FromSeconds(5), ct);
            var previous = Assert.IsType<StoredSession>(await store.ReadAsync(key, ct));
            await Task.Yield();
            await store.WriteAsync(key, new StoredSession(previous.Generation + 1, previous.Epoch,
                SessionLifecycleState.Active, previous.AccessToken, previous.RefreshToken,
                previous.Issuer, previous.ClientId, previous.Subject), ct);
        }));
        Assert.Equal(100, (await store.ReadAsync(key, ct))!.Generation);
    }
}
