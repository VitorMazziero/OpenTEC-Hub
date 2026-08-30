namespace OpenTECHub.Simulator;

/// <summary>
/// Time source for the simulation loop and process dynamics.
/// </summary>
public interface ISimulatorClock
{
    /// <summary>Current timestamp in the simulated universe.</summary>
    DateTimeOffset Now { get; }

    /// <summary>Advances the clock by the given simulated elapsed delta.</summary>
    void Advance(TimeSpan delta);

    /// <summary>True if this clock runs accelerated virtual time rather than wall-clock.</summary>
    bool IsAccelerated { get; }
}

/// <summary>
/// Normal wall-clock time source backed by <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class WallClock : ISimulatorClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Advance(TimeSpan delta)
    {
        // No-op for real-time wall clock
    }

    public bool IsAccelerated => false;
}

/// <summary>
/// Virtual accelerated clock for headless batch simulation and regression testing.
/// </summary>
public sealed class AcceleratedClock : ISimulatorClock
{
    private DateTimeOffset _currentTime;

    public AcceleratedClock(DateTimeOffset? startTime = null)
    {
        _currentTime = startTime ?? new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset Now => _currentTime;

    public void Advance(TimeSpan delta)
    {
        _currentTime += delta;
    }

    public bool IsAccelerated => true;
}
