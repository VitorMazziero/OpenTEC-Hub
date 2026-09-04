using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Where a capture is in the two-gate adaptive stop (§12.1).</summary>
public enum CaptureState
{
    /// <summary>Porta 1: waiting for the torque mean to stop drifting.</summary>
    Settling,

    /// <summary>Porta 2: accumulating until the confidence interval enters the target.</summary>
    Accumulating,

    /// <summary>Stopped because the CI target was reached — a clean point.</summary>
    Converged,

    /// <summary>Stopped because t_max elapsed before the target — best effort, flagged.</summary>
    TimedOut,
}

/// <summary>The mean and precision of one finished (or timed-out) capture, in torque terms.</summary>
public sealed record CaptureResult(
    int SampleCount,
    double MeanTorquePercent,
    double TorqueCi95Percent,
    double MeanRpm,
    double ElapsedSeconds,
    PowerStopReason StopReason);

/// <summary>
/// The heart of the automatic test (§12.1): the two-gate, confidence-driven capture of ONE
/// operating point. Pure and headless — it is fed torque samples and reports when the point is
/// done, so it can be unit-tested without any device. The runner (state machine, commands,
/// persistence) wraps it.
///
/// The whole stop test lives in torque space: at a fixed rotation, power = τ·ω with ω constant,
/// so the CI-relative-to-mean is identical for torque and power, and the absolute floor is the
/// drive's own torque scatter. That is why this class needs only <see cref="PowerTestSettings"/> —
/// the conversion to watts and Np happens later, in the analysis engine.
/// </summary>
public sealed class PowerCaptureController
{
    private readonly PowerTestSettings _settings;
    private readonly RunningStatistics _torqueStats = new();
    private readonly RunningStatistics _rpmStats = new();
    private readonly List<(double Time, double Torque)> _stationarityWindow = [];

    private int _stationaryConsecutive;
    private double _accumulationStartSeconds;
    private double _lastSampleSeconds;
    private bool _hasStart;
    private double _startSeconds;

    public PowerCaptureController(PowerTestSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public CaptureState State { get; private set; } = CaptureState.Settling;
    public int SampleCount => _torqueStats.Count;
    public bool IsDone => State is CaptureState.Converged or CaptureState.TimedOut;

    /// <summary>The confidence half-width on the torque mean right now, for the live indicator (§15).</summary>
    public double CurrentTorqueCi95Percent => _torqueStats.ConfidenceHalfWidth95;
    public double CurrentMeanTorquePercent => _torqueStats.Mean;

    /// <summary>Feed one telemetry sample. No effect once the capture is done.</summary>
    public void Add(double monotonicSeconds, double torquePercent, double rpm)
    {
        if (IsDone)
        {
            return;
        }

        if (!_hasStart)
        {
            _hasStart = true;
            _startSeconds = monotonicSeconds;
        }
        _lastSampleSeconds = monotonicSeconds;

        if (State == CaptureState.Settling)
        {
            HandleSettling(monotonicSeconds, torquePercent);
            return;
        }

        HandleAccumulating(monotonicSeconds, torquePercent, rpm);
    }

    /// <summary>Restart the capture in place (§12.1 recapture). Clears both gates.</summary>
    public void Reset()
    {
        _torqueStats.Reset();
        _rpmStats.Reset();
        _stationarityWindow.Clear();
        _stationaryConsecutive = 0;
        _hasStart = false;
        State = CaptureState.Settling;
    }

    public CaptureResult Result(PowerStopReason overrideReason = PowerStopReason.Target)
    {
        var reason = State switch
        {
            CaptureState.Converged => PowerStopReason.Target,
            CaptureState.TimedOut => PowerStopReason.Tmax,
            _ => overrideReason,
        };

        return new CaptureResult(
            _torqueStats.Count,
            _torqueStats.Mean,
            _torqueStats.ConfidenceHalfWidth95,
            _rpmStats.Count > 0 ? _rpmStats.Mean : 0.0,
            _hasStart ? _lastSampleSeconds - _startSeconds : 0.0,
            reason);
    }

    // ---- Porta 1: stationarity -----------------------------------------------------------

    private void HandleSettling(double seconds, double torquePercent)
    {
        _stationarityWindow.Add((seconds, torquePercent));

        var cutoff = seconds - _settings.StationarityWindowSeconds;
        while (_stationarityWindow.Count > 0 && _stationarityWindow[0].Time < cutoff)
        {
            _stationarityWindow.RemoveAt(0);
        }

        // Need a filled window and at least a few points before trusting a slope.
        if (_stationarityWindow.Count < 3 ||
            seconds - _stationarityWindow[0].Time < _settings.StationarityWindowSeconds * 0.5)
        {
            return;
        }

        // |slope| in %/s (percentage points of nominal per second). Below the tolerance means the
        // transient is over; a spike resets the consecutive count.
        var slope = Math.Abs(LinearSlope(_stationarityWindow));
        if (slope <= _settings.StationaritySlopeTolerancePercentPerSecond)
        {
            _stationaryConsecutive++;
        }
        else
        {
            _stationaryConsecutive = 0;
        }

        if (_stationaryConsecutive >= _settings.StationarityRequiredSamples)
        {
            State = CaptureState.Accumulating;
            _torqueStats.Reset();
            _rpmStats.Reset();
            _accumulationStartSeconds = seconds;
        }
    }

    // ---- Porta 2: precision ---------------------------------------------------------------

    private void HandleAccumulating(double seconds, double torquePercent, double rpm)
    {
        _torqueStats.Add(torquePercent);
        _rpmStats.Add(rpm);

        if (_torqueStats.Count >= _settings.MinSamples)
        {
            var ci = _torqueStats.ConfidenceHalfWidth95;

            // Hybrid target, whichever is reached first = the looser bound (§12.1, Q1):
            // relative to the mean, or an absolute floor from the observed torque scatter
            // (the tightest CI worth chasing given the drive's own noise, evaluated at n_min).
            var relativeTarget = _settings.RelativeCiFraction * Math.Abs(_torqueStats.Mean);
            var floorTarget = _settings.CiFloorSigmaMultiple * _torqueStats.StandardDeviation
                              / Math.Sqrt(_settings.MinSamples);
            var target = Math.Max(relativeTarget, floorTarget);

            if (ci <= target)
            {
                State = CaptureState.Converged;
                return;
            }
        }

        if (seconds - _accumulationStartSeconds >= _settings.MaxCaptureSeconds)
        {
            State = CaptureState.TimedOut;
        }
    }

    /// <summary>Ordinary-least-squares slope of torque vs time over the window.</summary>
    private static double LinearSlope(List<(double Time, double Torque)> window)
    {
        var n = window.Count;
        double sumT = 0, sumY = 0;
        foreach (var (t, y) in window)
        {
            sumT += t;
            sumY += y;
        }
        var meanT = sumT / n;
        var meanY = sumY / n;

        double sTT = 0, sTY = 0;
        foreach (var (t, y) in window)
        {
            var dt = t - meanT;
            sTT += dt * dt;
            sTY += dt * (y - meanY);
        }

        return sTT > 0 ? sTY / sTT : 0.0;
    }
}
