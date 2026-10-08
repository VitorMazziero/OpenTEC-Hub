namespace OpenTECHub.Services.Recipes;

/// <summary>Monotonic active time. Independent suspension reasons overlap without double counting.</summary>
public sealed class RecipeRampActiveClock(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _suspensions = new(StringComparer.Ordinal);
    private long _segmentStart = time.GetTimestamp();
    private TimeSpan _accumulated;

    public double ActiveSeconds
    {
        get
        {
            lock (_gate) return (_accumulated + (_suspensions.Count == 0
                ? time.GetElapsedTime(_segmentStart) : TimeSpan.Zero)).TotalSeconds;
        }
    }

    public bool IsSuspended { get { lock (_gate) return _suspensions.Count != 0; } }

    public void Suspend(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_suspensions.Contains(reason)) return;
            if (_suspensions.Count == 0) _accumulated += time.GetElapsedTime(_segmentStart);
            _suspensions.Add(reason);
        }
    }

    public void Resume(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (!_suspensions.Remove(reason)) return;
            if (_suspensions.Count == 0) _segmentStart = time.GetTimestamp();
        }
    }
}
