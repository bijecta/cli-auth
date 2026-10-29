using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Bijecta.CliAuth.ProtocolTests;

// Publish timer creation so tests advance only after the operation has actually begun waiting.
internal sealed class DeviceTestClock(DateTimeOffset start) : TimeProvider
{
    private readonly FakeTimeProvider _inner = new(start);
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _inner.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }
    internal void Advance(TimeSpan value) => _inner.Advance(value);
    internal async Task AdvanceDelayAsync(int seconds, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(seconds);
        while (await _timers.Reader.ReadAsync(ct) != delay) { }
        _inner.Advance(delay);
    }
}
