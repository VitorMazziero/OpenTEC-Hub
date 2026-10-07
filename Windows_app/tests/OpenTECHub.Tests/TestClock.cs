namespace OpenTECHub.Tests;

/// <summary>
/// A manually advanced clock, so time-dependent behaviour can be tested without
/// sleeping.
/// </summary>
/// <remarks>
/// Written by hand rather than taking a dependency on
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> - the only thing needed is a
/// settable "now", and a test-only package is not worth carrying for that.
/// </remarks>
internal sealed class TestClock(DateTimeOffset now, bool manualWatchdog = false) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _timestamp = 1;

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    // Runner fixtures explicitly call CheckWatchdog after driving telemetry. A wall-clock
    // callback racing a 130-second simulated loop otherwise makes assertions depend on CPU load.
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => manualWatchdog ? new ManualTimer() : base.CreateTimer(callback, state, dueTime, period);

    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan delta)
    {
        _now += delta;
        _timestamp += delta.Ticks;
    }
}
