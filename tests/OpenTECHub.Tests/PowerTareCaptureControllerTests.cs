using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerTareCaptureControllerTests
{
    [Fact]
    public void Tare_rung_uses_speed_stationarity_and_confidence_gates_and_keeps_raw_samples()
    {
        var settings = new PowerTestSettings
        {
            SpeedToleranceRpm = 2,
            SpeedStableSamples = 3,
            MaxSpeedSettlingSeconds = 10,
            StationarityWindowSeconds = 1,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 100,
            MinSamples = 12,
            RelativeCiFraction = 1,
            MaxCaptureSeconds = 30,
        };
        var capture = new PowerTareCaptureController(settings, 300);

        var t = 0.0;
        capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, 1.0, 280);
        t += 0.25;
        capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, 1.0, 299);
        t += 0.25;
        capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, 1.0, 301);
        Assert.Equal(TareCaptureState.StabilizingSpeed, capture.State);
        t += 0.25;
        capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, 1.0, 300);
        Assert.Equal(TareCaptureState.StabilizingTorque, capture.State);

        for (var i = 0; i < 200 && !capture.IsDone; i++)
        {
            t += 0.25;
            var torque = 1.2 + (i % 2 == 0 ? -0.03 : 0.03);
            var rpm = 300 + (i % 2 == 0 ? -0.5 : 0.5);
            capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, torque, rpm);
        }

        Assert.Equal(TareCaptureState.Converged, capture.State);
        var result = capture.Result();
        Assert.Equal(PowerStopReason.Target, result.StopReason);
        Assert.True(result.SampleCount >= settings.MinSamples);
        Assert.Equal(1.2, result.MeanTorquePercent, 2);
        Assert.Equal(300, result.MeanRpm, 1);
        Assert.True(result.TorqueStandardDeviationPercent > 0);
        Assert.True(result.RpmStandardDeviation > 0);
        Assert.True(result.RpmCi95 > 0);
        Assert.Equal(result.SampleCount, capture.Samples.Count(sample => sample.Counted));
        Assert.Contains(capture.Samples, sample => sample.Phase == TareCapturePhase.StabilizingSpeed);
        Assert.Contains(capture.Samples, sample => sample.Phase == TareCapturePhase.StabilizingTorque);
        Assert.Contains(capture.Samples, sample => sample.Phase == TareCapturePhase.Accumulating);

        var document = new PowerTestDocument
        {
            MotorRatedTorqueNm = 1.27,
            Calibration = new TorqueCalibration { Scale = 1.5, Offset = 0.01, MotorRatedTorqueNm = 1.27 },
        };
        var point = capture.CreatePoint(document);
        var expectedPowerW = capture.Samples
            .Where(sample => sample.Counted && sample.Attempt == capture.Attempt)
            .Average(sample => PowerCalc.ShaftPower(
                1.5 * (sample.TorquePercent / 100.0 * 1.27) + 0.01,
                sample.RpmMeasured));
        Assert.Equal(expectedPowerW, point.PVoidW, 6);
        Assert.Equal(result.TorqueStandardDeviationPercent, point.SigmaTauPercent, 8);
        Assert.Equal(result.TorqueCi95Percent, point.TorqueCi95Percent, 8);
        Assert.Equal(result.RpmCi95, point.RpmCi95, 8);
        Assert.True(point.PVoidCi95W > 0);
    }

    [Fact]
    public void Tare_rung_times_out_when_measured_speed_never_enters_the_band()
    {
        var settings = new PowerTestSettings
        {
            SpeedToleranceRpm = 2,
            SpeedStableSamples = 3,
            MaxSpeedSettlingSeconds = 3,
        };
        var capture = new PowerTareCaptureController(settings, 300);

        capture.Add(DateTimeOffset.UnixEpoch, 0, 1.0, 250);
        capture.AdvanceTime(3);

        Assert.True(capture.IsDone);
        Assert.Equal(TareCaptureState.SpeedTimedOut, capture.State);
        Assert.Equal(0, capture.SampleCount);
    }

    [Fact]
    public void Tare_rung_retries_both_statistical_gates_before_final_timeout()
    {
        var settings = new PowerTestSettings
        {
            SpeedToleranceRpm = 2,
            SpeedStableSamples = 1,
            StationarityWindowSeconds = 0.5,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 100,
            MinSamples = 10_000,
            RelativeCiFraction = 0,
            CiFloorSigmaMultiple = 0,
            MaxCaptureSeconds = 2,
            MaxTries = 2,
        };
        var capture = new PowerTareCaptureController(settings, 300);
        var t = 0.0;
        capture.Add(DateTimeOffset.UnixEpoch, t, 1.0, 300);

        for (var i = 0; i < 100 && !capture.IsDone; i++)
        {
            t += 0.25;
            capture.Add(DateTimeOffset.UnixEpoch.AddSeconds(t), t, 1.0 + (i % 2) * 0.1, 300);
        }

        Assert.Equal(TareCaptureState.CaptureTimedOut, capture.State);
        Assert.Equal(2, capture.Attempt);
        Assert.Contains(capture.Samples, sample => sample.Attempt == 1);
        Assert.Contains(capture.Samples, sample => sample.Attempt == 2);
        Assert.Equal(PowerStopReason.NotConverged, capture.Result().StopReason);
    }
}
