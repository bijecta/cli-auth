using System.Net;
using Bijecta.CliAuth.Core;
using Microsoft.Extensions.Time.Testing;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class BackchannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PerRequestDeadlineCancelsHungTransport()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });
        using var http = ProtocolHttp.Create(time, handler);
        var request = http.GetAsync("https://issuer.example.test/metadata", Ct);
        await entered.Task.WaitAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AuthErrorCode.NetworkFailure, (await Assert.ThrowsAsync<CliAuthException>(() => request)).ErrorCode);
    }

    [Fact]
    public async Task CancellationRemainsDistinctFromRequestDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });
        using var http = ProtocolHttp.Create(TimeProvider.System, handler);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = http.GetAsync("https://issuer.example.test/metadata", cancel.Token);
        await entered.Task.WaitAsync(Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Theory]
    [InlineData(false, 1024 * 1024)]
    [InlineData(true, 256 * 1024)]
    public async Task StreamingBodyLimitIsEnforcedWithoutContentLength(bool post, int limit)
    {
        using var body = new NonSeekableStream(limit + 8192);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(body) }));
        using var http = ProtocolHttp.Create(TimeProvider.System, handler);
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "https://issuer.example.test/endpoint");
        Assert.Equal(AuthErrorCode.ProtocolFailure, (await Assert.ThrowsAsync<CliAuthException>(
            () => http.SendAsync(request, Ct))).ErrorCode);
        Assert.InRange(body.BytesRead, limit + 1, limit + 8192);
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class NonSeekableStream(int length) : Stream
    {
        internal int BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, length - BytesRead);
            Array.Fill(buffer, (byte)'a', offset, n);
            BytesRead += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..n].Fill((byte)'a');
            BytesRead += n;
            return ValueTask.FromResult(n);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
