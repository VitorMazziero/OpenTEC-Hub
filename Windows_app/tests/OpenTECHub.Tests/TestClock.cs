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
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _timestamp = 1;

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan delta)
    {
        _now += delta;
        _timestamp += delta.Ticks;
    }
}
