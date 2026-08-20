using TecnalHub.Services.Control;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// The least-squares rate estimator, with the staircase-rejection property that is its
/// whole reason to exist.
/// </summary>
public class LeastSquaresRateEstimatorTests
{
    [Fact]
    public void Rate_is_zero_before_two_samples()
    {
        var estimator = new LeastSquaresRateEstimator(windowSeconds: 30);
        Assert.Equal(0.0, estimator.Rate);

        estimator.Add(0, 10);
        Assert.Equal(0.0, estimator.Rate);
    }

    [Fact]
    public void Rate_matches_a_clean_linear_ramp()
    {
        var estimator = new LeastSquaresRateEstimator(windowSeconds: 30);

        // y = 3·t + 10, sampled every 2 s.
        for (var t = 0; t <= 20; t += 2)
        {
            estimator.Add(t, (3.0 * t) + 10.0);
        }

        Assert.Equal(3.0, estimator.Rate, precision: 6);
    }

    /// <summary>
    /// The money test: a coarsely quantised ramp. An endpoint difference reads the
    /// staircase as alternating zero and huge slopes; the least-squares fit recovers the
    /// underlying trend the derivative and prediction actually need.
    /// </summary>
    [Fact]
    public void Rate_rejects_the_quantisation_staircase()
    {
        var estimator = new LeastSquaresRateEstimator(windowSeconds: 40);

        // True signal falls at 0.5 %/s; the probe only reports whole percents, so it
        // sits flat and then drops a full count.
        const double trueSlope = -0.5;
        for (var t = 0; t <= 40; t += 2)
        {
            var trueValue = 90.0 + (trueSlope * t);
            var quantised = Math.Round(trueValue);
            estimator.Add(t, quantised);
        }

        // Least squares lands within a few percent of the true slope...
        Assert.Equal(trueSlope, estimator.Rate, precision: 1);

        // ...whereas the last single step is a flat tread or a full-count drop, neither
        // of which is the true rate. This is what the estimator is protecting against.
        Assert.True(Math.Abs(estimator.Rate - trueSlope) < 0.1);
    }

    [Fact]
    public void Old_samples_leave_the_window_so_a_new_trend_is_tracked()
    {
        var estimator = new LeastSquaresRateEstimator(windowSeconds: 10);

        // First a rising trend, then a falling one. After the window has rolled past the
        // rising part, only the fall should remain.
        for (var t = 0; t <= 10; t += 2)
        {
            estimator.Add(t, 2.0 * t);
        }

        for (var t = 12; t <= 30; t += 2)
        {
            estimator.Add(t, 20.0 - (2.0 * (t - 10)));
        }

        Assert.True(estimator.Rate < 0, $"Expected a falling rate, got {estimator.Rate}");
        Assert.Equal(-2.0, estimator.Rate, precision: 6);
    }

    [Fact]
    public void Non_finite_samples_are_ignored()
    {
        var estimator = new LeastSquaresRateEstimator(windowSeconds: 30);
        estimator.Add(0, 10);
        estimator.Add(2, 12);
        estimator.Add(4, double.NaN);       // a sentinel or a parse gap
        estimator.Add(double.PositiveInfinity, 20);

        Assert.Equal(2, estimator.SampleCount);
        Assert.Equal(1.0, estimator.Rate, precision: 6);
    }

    [Fact]
    public void Window_must_be_positive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LeastSquaresRateEstimator(0));
}

/// <summary>
/// The velocity-form PID, verifying each of the three structural fixes over v.6:
/// output holds at setpoint, the integral does not wind up, and the prediction horizon
/// tames a dead-time plant.
/// </summary>
public class VelocityPidControllerTests
{
    private static CascadeTuning Tuning(
        double kp = 0.25, double ki = 0.02, double kd = 0.0,
        double predictionHorizon = 25.0, double rateWindow = 25.0)
        => new()
        {
            Kp = kp,
            Ki = ki,
            Kd = kd,
            IntegralMin = 0,
            IntegralMax = 100,
            OutputMin = 0,
            OutputMax = 100,
            PredictionHorizonSeconds = predictionHorizon,
            RateWindowSeconds = rateWindow,
            IntervalSeconds = 2,
        };

    [Fact]
    public void First_step_produces_no_proportional_or_derivative_kick()
    {
        // A large initial error must not slam the actuator: velocity form seeds the
        // previous error to the current one, so only the small integral term moves.
        var pid = new VelocityPidController(Tuning(), setpoint: 40);

        var terms = pid.Update(measurement: 5, dtSeconds: 2);

        // Only Ki·e·dt = 0.02·35·2 = 1.4 should have been applied.
        Assert.Equal(1.4, terms.DeltaOutput, precision: 6);
        Assert.Equal(1.4, terms.Output, precision: 6);
    }

    [Fact]
    public void Holds_last_output_at_setpoint_instead_of_collapsing_to_zero()
    {
        // This is the defect the velocity form fixes: a positional PID drove the output
        // to zero once the error vanished, and the organism kept consuming oxygen. The
        // velocity form holds the actuator wherever it was.
        var pid = new VelocityPidController(Tuning(), setpoint: 30);
        pid.Preload(output: 55);

        for (var i = 0; i < 200; i++)
        {
            pid.Update(measurement: 30, dtSeconds: 2);
        }

        Assert.Equal(55, pid.Output, precision: 6);
        Assert.True(pid.Output > 50, "The output must not decay away from its held level.");
    }

    [Fact]
    public void Integral_does_not_wind_up_while_the_output_is_saturated()
    {
        var pid = new VelocityPidController(Tuning(), setpoint: 95);

        // An unreachable setpoint: the measurement sits far below and never rises, so the
        // output pins at the ceiling.
        for (var i = 0; i < 500; i++)
        {
            var terms = pid.Update(measurement: 20, dtSeconds: 2);
            Assert.True(terms.Integral <= pid.Tuning.IntegralMax + 1e-9,
                $"Integral ran past its clamp: {terms.Integral}");
            Assert.True(terms.Output <= pid.Tuning.OutputMax + 1e-9);
        }

        Assert.Equal(pid.Tuning.OutputMax, pid.Output, precision: 6);

        // Now the target becomes reachable. With no wound-up integral to unwind, the
        // output must leave the rail almost immediately rather than after a long delay.
        pid.Setpoint = 10;
        var stepsToLeaveRail = 0;
        for (var i = 0; i < 20; i++)
        {
            pid.Update(measurement: 20, dtSeconds: 2);
            stepsToLeaveRail++;
            if (pid.Output < pid.Tuning.OutputMax - 1e-6)
            {
                break;
            }
        }

        Assert.True(stepsToLeaveRail <= 3,
            $"Output took {stepsToLeaveRail} steps to leave the rail - integral wound up.");
    }

    [Fact]
    public void Closed_loop_reaches_and_holds_the_setpoint()
    {
        var pid = new VelocityPidController(Tuning(), setpoint: 40);
        var plant = new FirstOrderDeadTimePlant(initialOxygen: 5, deadTimeSeconds: 25, uptake: 1.8);

        double measured = 5;
        for (var i = 0; i < 2000; i++)
        {
            var terms = pid.Update(measured, dtSeconds: 2);
            var kLa = 0.0006 * terms.Output; // effort 0-100 % maps to kLa 0-0.06 /s
            measured = plant.Step(kLa, dt: 2);
        }

        // Velocity form plus integral action drives the steady-state error to zero.
        Assert.True(Math.Abs(measured - 40) < 1.5, $"Settled at {measured}, not the setpoint.");
    }

    [Fact]
    public void Prediction_horizon_reduces_overshoot_on_a_dead_time_plant()
    {
        var overshootWithout = RunOvershoot(predictionHorizon: 0);
        var overshootWith = RunOvershoot(predictionHorizon: 40);

        // Without prediction the loop cannot see the 25 s of oxygen already on its way and
        // overshoots; the horizon anticipates it and backs off sooner.
        Assert.True(overshootWithout > 3.0,
            $"Expected a real overshoot without prediction, got {overshootWithout:F2}.");
        Assert.True(overshootWith < overshootWithout,
            $"Prediction did not help: with {overshootWith:F2} vs without {overshootWithout:F2}.");

        static double RunOvershoot(double predictionHorizon)
        {
            // Stable PI gains: with no prediction this still overshoots the dead time; the
            // horizon is the only difference between the two runs.
            var tuning = new CascadeTuning
            {
                Kp = 0.6,
                Ki = 0.05,
                Kd = 0.0,
                IntegralMin = 0,
                IntegralMax = 100,
                OutputMin = 0,
                OutputMax = 100,
                PredictionHorizonSeconds = predictionHorizon,
                RateWindowSeconds = 25,
                IntervalSeconds = 2,
            };
            var pid = new VelocityPidController(tuning, setpoint: 40);
            var plant = new FirstOrderDeadTimePlant(initialOxygen: 5, deadTimeSeconds: 25, uptake: 1.8);

            double measured = 5;
            double peak = 5;
            for (var i = 0; i < 900; i++)
            {
                var terms = pid.Update(measured, dtSeconds: 2);
                measured = plant.Step(0.0006 * terms.Output, dt: 2);
                peak = Math.Max(peak, plant.TrueOxygen);
            }

            return peak - 40; // overshoot above setpoint
        }
    }

    [Fact]
    public void A_non_positive_time_step_is_ignored()
    {
        var pid = new VelocityPidController(Tuning(), setpoint: 40);
        var first = pid.Update(measurement: 20, dtSeconds: 2);

        var ignored = pid.Update(measurement: 25, dtSeconds: 0);
        Assert.Equal(first, ignored);

        var backwards = pid.Update(measurement: 25, dtSeconds: -2);
        Assert.Equal(first, backwards);
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var pid = new VelocityPidController(Tuning(), setpoint: 40);
        for (var i = 0; i < 10; i++)
        {
            pid.Update(measurement: 10, dtSeconds: 2);
        }

        Assert.True(pid.Output > 0);

        pid.Reset();

        Assert.Equal(pid.Tuning.OutputMin, pid.Output);
        Assert.Equal(CascadeTerms.Empty, pid.LastTerms);
    }

    [Fact]
    public void Retune_keeps_the_probe_history_when_only_gains_change()
    {
        var pid = new VelocityPidController(Tuning(rateWindow: 30), setpoint: 40);
        for (var i = 0; i < 10; i++)
        {
            pid.Update(measurement: 10 + i, dtSeconds: 2);
        }

        var rateBefore = pid.LastTerms.MeasurementRate;
        Assert.True(rateBefore > 0);

        // Same window length, higher Kp: the rate estimate must survive the retune.
        pid.Retune(Tuning(kp: 8, rateWindow: 30));
        var terms = pid.Update(measurement: 20, dtSeconds: 2);

        Assert.True(terms.MeasurementRate > 0,
            "Retuning gains discarded the rate history it should have kept.");
    }
}
