using System.Diagnostics;
using System.Windows.Threading;
using Serilog;

namespace OpenTECHub.Services.Diagnostics;

/// <summary>
/// Measures UI-thread stalls: a <see cref="DispatcherTimer"/> at <see cref="DispatcherPriority.Input"/>
/// that expects to tick every <see cref="Interval"/> and records every tick that arrived more than
/// <see cref="HitchThreshold"/> late. A late tick means the dispatcher was busy with something above
/// Input priority for that long — exactly what the operator feels as a "hitch" (§5.1 of the
/// 2026-09-11 plan). DEBUG builds only; the summary goes to the log at shutdown.
/// </summary>
public sealed class UiHitchMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan HitchThreshold = TimeSpan.FromMilliseconds(30);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTickTicks;
    private readonly int[] _buckets = new int[5]; // 30-60, 60-100, 100-200, 200-500, >500 ms

    public UiHitchMonitor(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Input, OnTick, dispatcher);
        _lastTickTicks = _clock.ElapsedTicks;
    }

    public int Ticks { get; private set; }

    public int HitchCount { get; private set; }

    public double WorstMs { get; private set; }

    public double TotalHitchMs { get; private set; }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.ElapsedTicks;
        var lateMs = (now - _lastTickTicks) * 1000.0 / Stopwatch.Frequency - Interval.TotalMilliseconds;
        _lastTickTicks = now;
        Ticks++;

        if (lateMs <= HitchThreshold.TotalMilliseconds)
        {
            return;
        }

        HitchCount++;
        TotalHitchMs += lateMs;
        WorstMs = Math.Max(WorstMs, lateMs);
        _buckets[lateMs < 60 ? 0 : lateMs < 100 ? 1 : lateMs < 200 ? 2 : lateMs < 500 ? 3 : 4]++;
        Log.Debug("UI hitch: tick {LateMs:F0} ms late", lateMs);
    }

    /// <summary>One line with everything a before/after comparison needs.</summary>
    public string Summary =>
        $"UI hitches > {HitchThreshold.TotalMilliseconds:F0} ms: {HitchCount} in {Ticks} ticks " +
        $"({_clock.Elapsed.TotalMinutes:F1} min); worst {WorstMs:F0} ms; total {TotalHitchMs:F0} ms; " +
        $"buckets 30-60={_buckets[0]} 60-100={_buckets[1]} 100-200={_buckets[2]} 200-500={_buckets[3]} >500={_buckets[4]}";

    public void Dispose()
    {
        _timer.Stop();
        Log.Information("{Summary}", Summary);
    }
}
