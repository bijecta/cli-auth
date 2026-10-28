using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Bijecta.CliAuth.Core;
using Microsoft.Extensions.Time.Testing;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class ReceiverConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FourSlowConnectionsExpireAndReleaseSlotsForValidCallback()
    {
        var time = new ObservedTime();
        await using var receiver = new LoopbackReceiver(new BrowserCallbackOptions(), new Uri(BrowserFixture.Issuer), time);
        receiver.Start("test-state");
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, receiver.RedirectUri.Port, Ct);
                await time.Created.Reader.ReadAsync(Ct);
            }
            var valid = LoopbackTests.SendRaw(receiver.RedirectUri,
                $"GET /callback/?state=test-state&code=winner HTTP/1.1\r\nHost: {receiver.RedirectUri.Authority}\r\n\r\n", Ct);
            Assert.Equal(4, time.Connections);
            time.Fake.Advance(TimeSpan.FromSeconds(5));
            Assert.Contains("200 OK", await valid);
            Assert.Equal("winner", (await receiver.Response.WaitAsync(Ct)).Code);
            Assert.Equal(5, time.Connections);
        }
        finally { foreach (var client in clients) { client.Dispose(); } }
    }

    [Fact]
    public async Task ConcurrentValidCallbacksHaveOnlyOneWinner()
    {
        await using var receiver = new LoopbackReceiver(new BrowserCallbackOptions(), new Uri(BrowserFixture.Issuer), TimeProvider.System);
        receiver.Start("test-state");
        var replies = await Task.WhenAll(Enumerable.Range(1, 2).Select(Reply));
        Assert.Single(replies, r => r.Contains("200 OK", StringComparison.Ordinal));
        Assert.Contains((await receiver.Response.WaitAsync(Ct)).Code, new[] { "code-1", "code-2" });

        async Task<string> Reply(int i)
        {
            try
            {
                return await LoopbackTests.SendRaw(receiver.RedirectUri,
                    $"GET /callback/?state=test-state&code=code-{i} HTTP/1.1\r\nHost: {receiver.RedirectUri.Authority}\r\n\r\n", Ct);
            }
            catch (SocketException) { return string.Empty; }
            catch (IOException) { return string.Empty; }
        }
    }

    private sealed class ObservedTime : TimeProvider
    {
        internal readonly FakeTimeProvider Fake = new();
        internal readonly Channel<bool> Created = Channel.CreateUnbounded<bool>();
        internal int Connections;
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5))
            {
                Interlocked.Increment(ref Connections);
                Created.Writer.TryWrite(true);
            }
            return timer;
        }
    }
}
