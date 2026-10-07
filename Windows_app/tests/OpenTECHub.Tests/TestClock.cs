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
internal sealed class TestClock(DateTimeOffset now, bool manualWatchdog = false, bool virtualTimers = false) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _timestamp = 1;
    private readonly object _timerGate = new();
    private readonly List<VirtualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public int PendingTimers { get { lock (_timerGate) return _timers.Count; } }
    public void ShiftUtc(TimeSpan delta) => _now += delta;

    // Runner fixtures explicitly call CheckWatchdog after driving telemetry. A wall-clock
    // callback racing a 130-second simulated loop otherwise makes assertions depend on CPU load.
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (virtualTimers)
        {
            var timer = new VirtualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timerGate) _timers.Add(timer);
            return timer;
        }
        return manualWatchdog ? new ManualTimer() : base.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class VirtualTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due;
        private TimeSpan _period;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timerGate)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + dueTime.Ticks;
                _period = period;
                return true;
            }
        }
        public void FireIfDue()
        {
            lock (clock._timerGate)
            {
                if (_disposed || _due > clock._timestamp) return;
                _due = _period > TimeSpan.Zero ? clock._timestamp + _period.Ticks : long.MaxValue;
            }
            callback(state);
        }
        public void Dispose() { lock (clock._timerGate) { _disposed = true; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

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
        VirtualTimer[] timers;
        lock (_timerGate) timers = _timers.ToArray();
        foreach (var timer in timers) timer.FireIfDue();
    }
}
