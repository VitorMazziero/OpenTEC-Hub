using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>State of one statistically controlled in-air tare rung.</summary>
public enum TareCaptureState
{
    StabilizingSpeed,
    StabilizingTorque,
    Accumulating,
    Converged,
    SpeedTimedOut,
    CaptureTimedOut,
}

/// <summary>
/// Applies the same speed, stationarity and confidence gates used by a power run to one tare
/// rotation. Time and samples are supplied by the caller, keeping the metrology deterministic and
/// independently testable from the device/UI orchestration.
/// </summary>
public sealed class PowerTareCaptureController
{
    private readonly PowerTestSettings _settings;
    private readonly PowerCaptureController _capture;
    private readonly double _startedSeconds;
    private int _speedStableCount;
    private double _lastSeconds;

    public PowerTareCaptureController(PowerTestSettings settings, double targetRpm, double startedSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(targetRpm) || targetRpm < 15.0 || targetRpm > 1000.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRpm));
        }
        if (!double.IsFinite(startedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(startedSeconds));
        }
        if (!double.IsFinite(settings.SpeedToleranceRpm) || settings.SpeedToleranceRpm <= 0 ||
            settings.SpeedStableSamples < 1 ||
            !double.IsFinite(settings.MaxSpeedSettlingSeconds) || settings.MaxSpeedSettlingSeconds <= 0 ||
            settings.MaxTries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Speed and retry limits must be positive.");
        }

        _settings = settings;
        _capture = new PowerCaptureController(settings);
        _startedSeconds = startedSeconds;
        _lastSeconds = startedSeconds;
        TargetRpm = targetRpm;
    }

    public double TargetRpm { get; }
    public TareCaptureState State { get; private set; } = TareCaptureState.StabilizingSpeed;
    public int Attempt { get; private set; } = 1;
    public int MaxAttempts => _settings.MaxTries;
    public double MaxAllowedTorquePercent => _settings.MaxTorquePercent;
    public double MaxAllowedRpm => Math.Max(_settings.MaxRpm, TargetRpm) + _settings.SpeedToleranceRpm;
    public int SampleCount => _capture.SampleCount;
    public double CurrentMeanTorquePercent => _capture.CurrentMeanTorquePercent;
    public double CurrentTorqueCi95Percent => _capture.CurrentTorqueCi95Percent;
    public double CurrentTargetTorqueCi95Percent => _capture.CurrentTargetTorqueCi95Percent;
    public double ElapsedSeconds => Math.Max(0, _lastSeconds - _startedSeconds);
    public bool IsDone => State is TareCaptureState.Converged or TareCaptureState.SpeedTimedOut or TareCaptureState.CaptureTimedOut;
    public List<TareSample> Samples { get; } = [];

    /// <summary>Adds one fresh, valid servo frame. Invalid or out-of-order values are ignored.</summary>
    public void Add(DateTimeOffset timestampUtc, double monotonicSeconds, double torquePercent, double rpm)
    {
        if (IsDone || !double.IsFinite(monotonicSeconds) || monotonicSeconds < _lastSeconds ||
            !double.IsFinite(torquePercent) || !double.IsFinite(rpm))
        {
            return;
        }

        _lastSeconds = monotonicSeconds;
        var phase = State switch
        {
            TareCaptureState.StabilizingSpeed => TareCapturePhase.StabilizingSpeed,
            TareCaptureState.StabilizingTorque => TareCapturePhase.StabilizingTorque,
            _ => TareCapturePhase.Accumulating,
        };
        var counted = State == TareCaptureState.Accumulating;
        Samples.Add(new TareSample(
            timestampUtc,
            Math.Max(0, monotonicSeconds - _startedSeconds),
            TargetRpm,
            rpm,
            torquePercent,
            phase,
            counted,
            Attempt));

        if (State == TareCaptureState.StabilizingSpeed)
        {
            _speedStableCount = Math.Abs(rpm - TargetRpm) <= _settings.SpeedToleranceRpm
                ? _speedStableCount + 1
                : 0;
            if (_speedStableCount >= _settings.SpeedStableSamples)
            {
                _capture.Reset();
                State = TareCaptureState.StabilizingTorque;
            }
            return;
        }

        _capture.Add(monotonicSeconds, torquePercent, rpm);
        if (_capture.State == CaptureState.Accumulating)
        {
            State = TareCaptureState.Accumulating;
        }
        else if (_capture.State == CaptureState.Converged)
        {
            State = TareCaptureState.Converged;
        }
        else if (_capture.State == CaptureState.TimedOut)
        {
            if (Attempt < _settings.MaxTries)
            {
                Attempt++;
                _capture.Reset();
                State = TareCaptureState.StabilizingTorque;
            }
            else
            {
                State = TareCaptureState.CaptureTimedOut;
            }
        }
    }

    /// <summary>Advances wall time even when the drive is sending no usable sample.</summary>
    public void AdvanceTime(double monotonicSeconds)
    {
        if (IsDone || !double.IsFinite(monotonicSeconds) || monotonicSeconds < _lastSeconds)
        {
            return;
        }

        _lastSeconds = monotonicSeconds;
        if (State == TareCaptureState.StabilizingSpeed &&
            monotonicSeconds - _startedSeconds >= _settings.MaxSpeedSettlingSeconds)
        {
            State = TareCaptureState.SpeedTimedOut;
        }
    }

    public CaptureResult Result()
    {
        var reason = State == TareCaptureState.Converged
            ? PowerStopReason.Target
            : PowerStopReason.NotConverged;
        return _capture.Result(reason);
    }

    /// <summary>Converts the converged statistics into the persisted, calibrated tare point.</summary>
    public TarePoint CreatePoint(PowerTestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (State != TareCaptureState.Converged)
        {
            throw new InvalidOperationException("A tare point can only be created after convergence.");
        }

        var result = Result();
        var ratedTorqueNm = document.Calibration?.MotorRatedTorqueNm ?? document.MotorRatedTorqueNm;
        double ToTorqueNm(double torquePercent) => document.Calibration is { } calibration
            ? calibration.Scale * (torquePercent / 100.0 * calibration.MotorRatedTorqueNm) + calibration.Offset
            : torquePercent / 100.0 * ratedTorqueNm;

        // Work directly in power space instead of multiplying two independently summarized
        // means. This retains the torque/RPM covariance present in the raw in-air observations.
        var powerStats = new RunningStatistics();
        foreach (var sample in Samples)
        {
            if (sample.Counted && sample.Attempt == Attempt)
            {
                powerStats.Add(PowerCalc.ShaftPower(ToTorqueNm(sample.TorquePercent), sample.RpmMeasured));
            }
        }

        return new TarePoint(
            TargetRpm,
            Math.Max(0.0, powerStats.Mean),
            result.TorqueStandardDeviationPercent)
        {
            SampleCount = result.SampleCount,
            MeanRpmMeasured = result.MeanRpm,
            RpmStandardDeviation = result.RpmStandardDeviation,
            RpmCi95 = result.RpmCi95,
            MeanTorquePercent = result.MeanTorquePercent,
            TorqueCi95Percent = result.TorqueCi95Percent,
            PVoidCi95W = powerStats.ConfidenceHalfWidth95,
            ElapsedSeconds = ElapsedSeconds,
            Attempts = Attempt,
            StopReason = result.StopReason,
        };
    }
}
