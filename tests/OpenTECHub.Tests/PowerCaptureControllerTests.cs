using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerCaptureControllerTests
{
    private const double Dt = 0.5; // ~2 Hz, the real Modbus cadence (§12.1)

    [Fact]
    public void Converges_On_Steady_Signal_With_Correct_Mean()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 2,
            StationarityRequiredSamples = 2,
            StationaritySlopeTolerancePercentPerSecond = 0.2,
            MinSamples = 10,
            RelativeCiFraction = 0.05,
            MaxCaptureSeconds = 120,
        };
        var capture = new PowerCaptureController(settings);

        FeedSteady(capture, mean: 2.0, dither: 0.02, rpm: 300, maxSamples: 200);

        Assert.Equal(CaptureState.Converged, capture.State);
        var result = capture.Result();
        Assert.Equal(PowerStopReason.Target, result.StopReason);
        Assert.Equal(2.0, result.MeanTorquePercent, 1);
        Assert.True(result.SampleCount >= 10);
        Assert.Equal(300, result.MeanRpm, 0);
    }

    [Fact]
    public void Stationarity_Gate_Blocks_Accumulation_While_Drifting()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 2,
            StationarityRequiredSamples = 2,
            StationaritySlopeTolerancePercentPerSecond = 0.1,
            MinSamples = 10,
            RelativeCiFraction = 0.05,
            MaxCaptureSeconds = 120,
        };
        var capture = new PowerCaptureController(settings);

        // Drift at 0.5 %/s — far above the 0.1 tolerance: never leaves Settling.
        var t = 0.0;
        for (var i = 0; i < 24; i++, t += Dt)
        {
            capture.Add(t, 1.0 + 0.5 * t, 300);
        }
        Assert.Equal(CaptureState.Settling, capture.State);
        Assert.Equal(0, capture.SampleCount); // nothing accumulated yet

        // Now hold steady: the gate opens and the point converges.
        for (var i = 0; i < 120 && !capture.IsDone; i++, t += Dt)
        {
            capture.Add(t, 6.0 + (i % 2 == 0 ? -0.02 : 0.02), 300);
        }
        Assert.Equal(CaptureState.Converged, capture.State);
        Assert.Equal(6.0, capture.Result().MeanTorquePercent, 1);
    }

    [Fact]
    public void Does_Not_Converge_Before_MinSamples()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 1,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 1000, // always stationary
            MinSamples = 40,
            RelativeCiFraction = 100, // relative target trivially met → only n_min gates
            MaxCaptureSeconds = 999,
        };
        var capture = new PowerCaptureController(settings);

        FeedSteady(capture, mean: 2.0, dither: 0.02, rpm: 300, maxSamples: 300);

        Assert.Equal(CaptureState.Converged, capture.State);
        Assert.True(capture.Result().SampleCount >= 40); // never earlier than n_min
    }

    [Fact]
    public void Times_Out_When_Target_Never_Reached()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 1,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 1000,
            MinSamples = 10,
            RelativeCiFraction = 0.02,
            CiFloorSigmaMultiple = 1.0,
            MaxCaptureSeconds = 10, // short ceiling vs the persistent scatter
        };
        var capture = new PowerCaptureController(settings);

        // High persistent scatter (±0.4) around a small mean: CI cannot enter the target in 10 s.
        var t = 0.0;
        for (var i = 0; i < 40 && !capture.IsDone; i++, t += Dt)
        {
            capture.Add(t, 1.5 + (i % 2 == 0 ? -0.4 : 0.4), 300);
        }

        Assert.Equal(CaptureState.TimedOut, capture.State);
        Assert.Equal(PowerStopReason.Tmax, capture.Result().StopReason);
    }

    [Fact]
    public void Absolute_Floor_Lets_A_Low_Signal_Converge()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 1,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 1000,
            MinSamples = 10,
            RelativeCiFraction = 0.02, // 2% of ~0.05 = 0.001, effectively unreachable
            CiFloorSigmaMultiple = 1.0,
            MaxCaptureSeconds = 400,
        };
        var capture = new PowerCaptureController(settings);

        // Near-zero mean with real scatter: only the σ-floor can stop this.
        var t = 0.0;
        for (var i = 0; i < 800 && !capture.IsDone; i++, t += Dt)
        {
            capture.Add(t, 0.05 + (i % 2 == 0 ? -0.06 : 0.06), 300);
        }

        Assert.Equal(CaptureState.Converged, capture.State);
        Assert.True(capture.Result().SampleCount >= 20);
    }

    [Fact]
    public void Reset_Restarts_Both_Gates()
    {
        var settings = new PowerTestSettings
        {
            StationarityWindowSeconds = 1,
            StationarityRequiredSamples = 1,
            StationaritySlopeTolerancePercentPerSecond = 1000,
            MinSamples = 10,
            RelativeCiFraction = 0.05,
            MaxCaptureSeconds = 120,
        };
        var capture = new PowerCaptureController(settings);
        FeedSteady(capture, mean: 2.0, dither: 0.02, rpm: 300, maxSamples: 200);
        Assert.True(capture.IsDone);

        capture.Reset();

        Assert.Equal(CaptureState.Settling, capture.State);
        Assert.Equal(0, capture.SampleCount);
        Assert.False(capture.IsDone);
    }

    private static void FeedSteady(PowerCaptureController capture, double mean, double dither, double rpm, int maxSamples)
    {
        var t = 0.0;
        for (var i = 0; i < maxSamples && !capture.IsDone; i++, t += Dt)
        {
            capture.Add(t, mean + (i % 2 == 0 ? -dither : dither), rpm);
        }
    }
}
