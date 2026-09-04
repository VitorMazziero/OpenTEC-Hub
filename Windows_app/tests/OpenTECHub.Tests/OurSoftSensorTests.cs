using OpenTECHub.Services.Control;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The conditional-OUR soft sensor core (WP8): the OUR inversion, the quasi-steady accept/refuse
/// gate, and the accepted-interval integration that never fills a refused sample with zero.
/// </summary>
public sealed class OurSoftSensorTests
{
    private static OurSensorConfig Config() => new()
    {
        OxygenSaturationMmolPerL = 0.21,
        SetpointTolerancePercentPoints = 5.0,
        RateLimitPointsPerHour = 10.0,
        GateTolerancePercentPoints = 2.0,
        RateWindowSeconds = 120.0,
        MinimumRateSamples = 3,
        MaximumIntegrationGapSeconds = 30.0,
    };

    [Fact]
    public void Our_inverts_the_oxygen_balance_at_the_operating_point()
    {
        var sensor = new OurSoftSensor(Config());

        // OUR = kLa * C* * (1 - DOT/100) = 100 * 0.21 * 0.70 = 14.7 mmol L^-1 h^-1.
        var sample = sensor.Update(timeSeconds: 0, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);

        Assert.Equal(14.7, sample.OurMmolPerLPerHour!.Value, precision: 9);
    }

    [Fact]
    public void Nothing_is_accepted_until_dot_first_reaches_the_gate_band()
    {
        var sensor = new OurSoftSensor(Config());

        // DOT sits far from the setpoint: startup, never gated, always refused.
        for (var t = 0; t <= 600; t += 10)
        {
            var sample = sensor.Update(t, dotPercent: 92, setpointPercent: 30, klaPerHour: 100);
            Assert.False(sample.Accepted);
            Assert.Equal(OurStatus.WaitingForSetpoint, sample.Status);
            Assert.Null(sample.ConditionalOurMmolPerLPerHour);
        }
    }

    [Fact]
    public void A_quasi_steady_on_band_sample_with_kla_is_accepted()
    {
        var sensor = new OurSoftSensor(Config());
        OurSample sample = default;

        for (var t = 0; t <= 40; t += 10)
        {
            sample = sensor.Update(t, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);
        }

        Assert.True(sample.Accepted);
        Assert.Equal(OurStatus.Accepted, sample.Status);
        Assert.Equal(14.7, sample.ConditionalOurMmolPerLPerHour!.Value, precision: 9);
    }

    [Fact]
    public void Out_of_band_dot_is_refused_with_no_value_not_zero()
    {
        var sensor = new OurSoftSensor(Config());
        Warmup(sensor);

        // 40% is 10 pp off a 30% setpoint: outside the ±5 pp band.
        var sample = sensor.Update(timeSeconds: 100, dotPercent: 40, setpointPercent: 30, klaPerHour: 100);

        Assert.False(sample.Accepted);
        Assert.Equal(OurStatus.OutOfBand, sample.Status);
        Assert.Null(sample.ConditionalOurMmolPerLPerHour);
        // The unconditional inference still exists; only the *conditional* value is withheld.
        Assert.NotNull(sample.OurMmolPerLPerHour);
    }

    [Fact]
    public void A_fast_moving_dot_is_refused_as_not_quasi_steady()
    {
        var sensor = new OurSoftSensor(Config());

        // On-band amplitude but a steep slope: 0.5 pp / 60 s = 30 pp/h > 10 pp/h.
        sensor.Update(0, dotPercent: 30.0, setpointPercent: 30, klaPerHour: 100);
        sensor.Update(60, dotPercent: 30.5, setpointPercent: 30, klaPerHour: 100);
        var sample = sensor.Update(120, dotPercent: 31.0, setpointPercent: 30, klaPerHour: 100);

        Assert.False(sample.Accepted);
        Assert.Equal(OurStatus.NotQuasiSteady, sample.Status);
        Assert.Null(sample.ConditionalOurMmolPerLPerHour);
    }

    [Fact]
    public void No_kla_means_no_estimate()
    {
        var sensor = new OurSoftSensor(Config());
        Warmup(sensor, klaPerHour: null);

        var sample = sensor.Update(timeSeconds: 100, dotPercent: 30, setpointPercent: 30, klaPerHour: null);

        Assert.False(sample.Accepted);
        Assert.Equal(OurStatus.NoKla, sample.Status);
        Assert.Null(sample.OurMmolPerLPerHour);
        Assert.Null(sample.ConditionalOurMmolPerLPerHour);
    }

    [Fact]
    public void The_cumulative_integrates_only_accepted_intervals()
    {
        var sensor = new OurSoftSensor(Config());
        OurSample sample = default;

        for (var t = 0; t <= 60; t += 10)
        {
            sample = sensor.Update(t, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);
        }

        // Constant 14.7 mmol/L/h, so the mean must be 14.7 and the total positive.
        Assert.True(sample.AcceptedDurationHours > 0);
        Assert.Equal(14.7, sensor.MeanMmolPerLPerHour!.Value, precision: 6);
        Assert.Equal(14.7 * sample.AcceptedDurationHours, sample.CumulativeMmolPerL, precision: 9);
    }

    [Fact]
    public void A_refused_sample_breaks_the_interval_and_freezes_the_total()
    {
        var sensor = new OurSoftSensor(Config());
        OurSample accepted = default;
        for (var t = 0; t <= 60; t += 10)
        {
            accepted = sensor.Update(t, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);
        }

        var frozen = accepted.CumulativeMmolPerL;

        // Refuse via a lost kLa: DOT stays flat, so the rate window is not disturbed and the
        // break is isolated from any quasi-steady effect.
        var refused = sensor.Update(70, dotPercent: 30, setpointPercent: 30, klaPerHour: null);
        Assert.False(refused.Accepted);
        Assert.Equal(OurStatus.NoKla, refused.Status);
        Assert.Equal(frozen, refused.CumulativeMmolPerL, precision: 12);

        // The first re-accepted frame does not integrate across the refused gap: the interval
        // restarts rather than back-filling the missing seconds.
        var reAccepted = sensor.Update(80, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);
        Assert.True(reAccepted.Accepted);
        Assert.Equal(frozen, reAccepted.CumulativeMmolPerL, precision: 12);
    }

    [Fact]
    public void Reset_clears_the_gate_and_the_total()
    {
        var sensor = new OurSoftSensor(Config());
        for (var t = 0; t <= 60; t += 10)
        {
            sensor.Update(t, dotPercent: 30, setpointPercent: 30, klaPerHour: 100);
        }

        sensor.Reset();

        Assert.Equal(0.0, sensor.CumulativeMmolPerL);
        Assert.Equal(0.0, sensor.AcceptedDurationHours);
        // Gate is cleared, so the next far-from-setpoint sample is back to WaitingForSetpoint.
        var sample = sensor.Update(70, dotPercent: 90, setpointPercent: 30, klaPerHour: 100);
        Assert.Equal(OurStatus.WaitingForSetpoint, sample.Status);
    }

    private static void Warmup(OurSoftSensor sensor, double? klaPerHour = 100)
    {
        for (var t = 0; t <= 40; t += 10)
        {
            sensor.Update(t, dotPercent: 30, setpointPercent: 30, klaPerHour: klaPerHour);
        }
    }
}
