using System.Globalization;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Simulator;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CalibrationMathTests
{
    [Fact]
    public void Two_point_linear_fit_reproduces_the_reference_equation()
    {
        var fit = CalibrationMath.FitLinear(
            new LinearCalibrationPoint(1000, 7),
            new LinearCalibrationPoint(2000, 4));

        Assert.Equal(-0.003, fit.Slope, precision: 12);
        Assert.Equal(10.0, fit.Intercept, precision: 12);
    }

    [Fact]
    public void Identical_raw_points_are_refused_instead_of_installing_v6_slope_one_fallback()
        => Assert.Throws<InvalidOperationException>(() => CalibrationMath.FitLinear(
            new LinearCalibrationPoint(1000, 7),
            new LinearCalibrationPoint(1000, 4)));

    [Fact]
    public void Sample_standard_deviation_matches_statistics_stdev()
        => Assert.Equal(1.0, CalibrationMath.SampleStandardDeviation([1.0, 2.0, 3.0]), precision: 12);

    [Fact]
    public void The_high_segment_is_fitted_independently_of_the_low_points()
    {
        var points = new[]
        {
            (Voltage: 0.01, Flow: 0.0),
            (Voltage: 0.03, Flow: 0.5),
            (Voltage: 0.05, Flow: 0.75),
            (Voltage: 0.10, Flow: (5 * 0.10) + 1),
            (Voltage: 0.20, Flow: (5 * 0.20) + 1),
        };

        var high = Assert.IsType<PolynomialCalibration>(CalibrationMath.FitFlowCurve(points).HighVoltage);

        Assert.Equal(0.0, high.K, precision: 12);
        Assert.Equal(5.0, high.F, precision: 8);
        Assert.Equal(1.0, high.C, precision: 8);
    }

    [Fact]
    public void The_low_segment_meets_the_high_segment_without_a_jump_or_a_kink()
    {
        var points = new[]
        {
            (Voltage: 0.0098, Flow: 0.0),
            (Voltage: 0.0244, Flow: 0.5),
            (Voltage: 0.0400, Flow: 0.75),
            (Voltage: 0.10, Flow: (5 * 0.10) + 1),
            (Voltage: 0.20, Flow: (5 * 0.20) + 1),
        };

        var fit = CalibrationMath.FitFlowCurve(points);
        var low = Assert.IsType<PolynomialCalibration>(fit.LowVoltage);
        var high = Assert.IsType<PolynomialCalibration>(fit.HighVoltage);
        var split = FlowCalibrationCurve.DefaultTransitionVoltage;

        // The two anchors that replace the old ~0.17 L/min step at the threshold.
        Assert.Equal(high.Evaluate(split), low.Evaluate(split), precision: 9);
        Assert.Equal(high.Derivative(split), low.Derivative(split), precision: 9);

        // Three low points still determine the curve exactly.
        foreach (var (voltage, flow) in points.Where(p => p.Voltage <= split))
        {
            Assert.Equal(flow, low.Evaluate(voltage), precision: 9);
        }
    }

    /// <summary>
    /// The certified bench points behind the shipped V05 calibration, in the order the
    /// flowmeter dialog lists them.
    /// </summary>
    public static readonly (double Voltage, double Flow)[] V05BenchPoints =
    [
        (0.010330, 0.0), (0.024090, 0.5), (0.040640, 0.75),
        (0.067500, 1.0), (0.157940, 2.0), (0.332030, 4.0),
        (0.502310, 6.0), (0.694720, 8.0), (0.897150, 10.0),
        (1.080840, 12.0), (1.287900, 14.0),
    ];

    [Fact]
    public void Fitting_the_certified_bench_points_regenerates_the_v05_coefficients()
    {
        var fit = CalibrationMath.FitFlowCurve(V05BenchPoints);

        var low = Assert.IsType<PolynomialCalibration>(fit.LowVoltage);
        var high = Assert.IsType<PolynomialCalibration>(fit.HighVoltage);

        // The high segment is a plain quadratic least-squares fit of the eight points above
        // the split — the curve the old dialog printed as -0.8546x² + 11.8145x + 0.1922.
        Assert.Equal(-0.854551899, high.K, precision: 6);
        Assert.Equal(11.814453070, high.F, precision: 6);
        Assert.Equal(0.192231954, high.C, precision: 6);

        // The low segment is the anchored quartic the firmware ships. The coefficients span six
        // orders of magnitude, so agreement is stated relatively: every one matches the
        // published constant to better than one part in a million, which is what is left after
        // those constants were rounded for publication.
        AssertRelative(321791.345936369, low.A);
        AssertRelative(-32589.073104291, low.B);
        AssertRelative(462.893536740, low.K);
        AssertRelative(43.294432104, low.F);
        AssertRelative(-0.464367483, low.C);

        static void AssertRelative(double expected, double actual)
            => Assert.True(
                Math.Abs(actual - expected) <= 1e-6 * Math.Abs(expected),
                $"esperado {expected:G15}, obtido {actual:G15}");
    }

    [Fact]
    public void The_firmware_default_curve_matches_the_v05_coefficients_and_is_continuous()
    {
        var curve = CalibrationMath.FirmwareDefault;
        var low = Assert.IsType<PolynomialCalibration>(curve.LowVoltage);
        var high = Assert.IsType<PolynomialCalibration>(curve.HighVoltage);

        Assert.Equal(321791.345936369, low.A, precision: 6);
        Assert.Equal(-32589.073104291, low.B, precision: 6);
        Assert.Equal(462.893536740, low.K, precision: 6);
        Assert.Equal(43.294432104, low.F, precision: 9);
        Assert.Equal(-0.464367483, low.C, precision: 6);
        Assert.Equal(-0.854551899, high.K, precision: 6);
        Assert.Equal(11.814453070, high.F, precision: 6);
        Assert.Equal(0.192231954, high.C, precision: 6);

        // The documented values at the threshold: 0,83358133 vs 0,83358145 L/min and
        // slopes 11,7213108 vs 11,7213070 — negligible in float32.
        var split = FlowCalibrationCurve.DefaultTransitionVoltage;
        Assert.Equal(0.83358133, low.Evaluate(split), precision: 6);
        Assert.Equal(0.83358145, high.Evaluate(split), precision: 6);
        Assert.Equal(11.7213108, low.Derivative(split), precision: 4);
        Assert.Equal(11.7213070, high.Derivative(split), precision: 4);
        Assert.True(Math.Abs(curve.DiscontinuityAtSplit!.Value) < 1e-6);
    }

    [Fact]
    public void Different_Vt_changes_point_classification()
    {
        // A point at 0.06 V is above the default 0.0545, but below 0.10.
        var points = new[]
        {
            (Voltage: 0.01, Flow: 0.0),
            (Voltage: 0.03, Flow: 0.5),
            (Voltage: 0.06, Flow: 0.9),   // between default and 0.10
            (Voltage: 0.15, Flow: 2.0),
            (Voltage: 0.30, Flow: 4.0),
        };

        // With default Vt = 0.0545, point 0.06 is in the high segment.
        var defaultFit = CalibrationMath.FitFlowCurve(points);
        Assert.Equal(FlowCalibrationCurve.DefaultTransitionVoltage, defaultFit.TransitionVoltage);

        // With Vt = 0.10, point 0.06 should now be in the low segment.
        var customFit = CalibrationMath.FitFlowCurve(points, transitionVoltage: 0.10);
        Assert.Equal(0.10, customFit.TransitionVoltage);

        // The two fits should produce different curves because the point classification changed.
        Assert.NotNull(customFit.LowVoltage);
        Assert.NotNull(customFit.HighVoltage);
        Assert.NotEqual(defaultFit.LowVoltage, customFit.LowVoltage);
    }

    [Fact]
    public void Continuity_holds_at_non_default_Vt()
    {
        var vt = 0.08;
        var points = new[]
        {
            (Voltage: 0.01, Flow: 0.0),
            (Voltage: 0.03, Flow: 0.5),
            (Voltage: 0.06, Flow: 0.9),
            (Voltage: 0.15, Flow: 2.0),
            (Voltage: 0.30, Flow: 4.0),
            (Voltage: 0.50, Flow: 6.0),
        };

        var fit = CalibrationMath.FitFlowCurve(points, transitionVoltage: vt);
        var low = Assert.IsType<PolynomialCalibration>(fit.LowVoltage);
        var high = Assert.IsType<PolynomialCalibration>(fit.HighVoltage);

        // Value continuity.
        Assert.Equal(high.Evaluate(vt), low.Evaluate(vt), precision: 9);

        // Derivative continuity.
        Assert.Equal(high.Derivative(vt), low.Derivative(vt), precision: 9);
    }

    [Fact]
    public void V_equal_to_Vt_uses_the_low_segment()
    {
        var low = new PolynomialCalibration(0, 10.0, 0.5);
        var high = new PolynomialCalibration(0, 20.0, 1.0);
        var vt = 0.1;
        var curve = new FlowCalibrationCurve(low, high, vt);

        // At V = Vt, the low curve should be selected (low.Evaluate(0.1) = 10*0.1 + 0.5 = 1.5).
        Assert.Equal(low.Evaluate(vt), curve.Evaluate(vt));

        // Just above Vt, the high curve should be selected.
        var justAbove = vt + 1e-10;
        Assert.Equal(high.Evaluate(justAbove), curve.Evaluate(justAbove));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(3.3)]
    [InlineData(5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Vt_below_zero_or_above_3_3_is_rejected(double badVt)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowCalibrationCurve.ValidateTransitionVoltage(badVt));

    [Fact]
    public void FitFlowCurve_with_Vt_produces_curve_with_that_transition()
    {
        var vt = 0.12;
        var points = new[]
        {
            (Voltage: 0.01, Flow: 0.0),
            (Voltage: 0.05, Flow: 0.75),
            (Voltage: 0.10, Flow: 1.2),
            (Voltage: 0.20, Flow: 3.0),
            (Voltage: 0.40, Flow: 5.0),
        };

        var fit = CalibrationMath.FitFlowCurve(points, transitionVoltage: vt);

        Assert.Equal(vt, fit.TransitionVoltage);
        Assert.NotNull(fit.LowVoltage);
        Assert.NotNull(fit.HighVoltage);
        Assert.True(Math.Abs(fit.DiscontinuityAtSplit!.Value) < 1e-6);
    }
}

public sealed class PHControlTests
{
    [Fact]
    public void Applying_pH_sends_the_complete_v6_state_in_one_frame()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PHControlViewModel(device, settings)
        {
            SetpointText = "6,80",
            InactiveBandText = "0,17",
            OperationSecondsText = "5",
            MixSecondsText = "20",
            PumpSpeedPercentText = "50",
            IsEnabled = true,
        };

        vm.ApplyCommand.Execute(null);

        Assert.Equal(
            """{"pHSetpoint":6.8,"pHError":0.17,"pHOperation":5.0,"pHMix":20.0,"pHIntensity":500.0}""",
            Assert.Single(device.Sent));
        Assert.True(vm.AppliedIsEnabled);
        Assert.Equal(50, settings.Current.PHControl.PumpSpeedPercent);
    }

    [Fact]
    public void Invalid_enabled_pH_never_falls_back_to_seven()
    {
        var device = new RecordingDeviceService();
        using var vm = new PHControlViewModel(device, new MemorySettingsService())
        {
            SetpointText = "sete",
            IsEnabled = true,
        };

        vm.ApplyCommand.Execute(null);

        Assert.False(vm.IsValid);
        Assert.True(vm.HasPendingChange);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Disabled_pH_safe_stop_is_not_blocked_by_bad_text_and_restores_valid_fields()
    {
        var device = new RecordingDeviceService();
        using var vm = new PHControlViewModel(device, new MemorySettingsService())
        {
            SetpointText = "sete",
        };

        Assert.True(vm.HasPendingChange);
        vm.ApplyCommand.Execute(null);

        Assert.Single(device.Sent);
        Assert.Equal("7,00", vm.SetpointText.Replace('.', ','));
        Assert.False(vm.HasPendingChange);
    }

    [Fact]
    public void Calibration_interlock_sends_a_complete_pH_safe_stop()
    {
        var device = new RecordingDeviceService();
        using var vm = new PHControlViewModel(device, new MemorySettingsService());

        vm.SuspendForCalibration();

        Assert.Equal(
            """{"pHSetpoint":0.0,"pHError":0.17,"pHOperation":3.0,"pHMix":10.0,"pHIntensity":0.0}""",
            Assert.Single(device.Sent));
        Assert.False(vm.AppliedIsEnabled);
    }
}

public sealed class GuidedCalibrationTests
{
    [Fact]
    public void Two_point_pH_waits_stabilises_averages_and_only_then_applies()
    {
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings
            {
                PHStabilityWindow = 2,
                PHStabilityStandardDeviation = 1,
                PHAverageSamples = 1,
            },
        };
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(initial);
        using var control = new PHControlViewModel(device, settings);
        using var vm = new PHCalibrationViewModel(device, settings, control);

        PushPH(device, 1000);
        vm.StartTwoPointCommand.Execute(null);
        Assert.Equal(PHCalibrationStage.AwaitingFirstBuffer, vm.Stage);
        Assert.Single(device.Sent); // calibration interlock

        vm.ConfirmPointCommand.Execute(null);
        PushPH(device, 1000);
        PushPH(device, 1000);
        PushPH(device, 1000);
        Assert.Equal(PHCalibrationStage.AwaitingSecondBuffer, vm.Stage);

        vm.ConfirmPointCommand.Execute(null);
        PushPH(device, 2000);
        PushPH(device, 2000);
        PushPH(device, 2000);
        Assert.Equal(PHCalibrationStage.Proposed, vm.Stage);
        Assert.Equal(initial.Calibration.PHSlope, settings.Current.Calibration.PHSlope);

        vm.ApplyProposalCommand.Execute(null);

        Assert.Equal(PHCalibrationStage.Applied, vm.Stage);
        Assert.Equal(-0.003, settings.Current.Calibration.PHSlope, precision: 12);
        Assert.Equal(10.0, settings.Current.Calibration.PHIntercept, precision: 12);
    }

    [Fact]
    public void Oxygen_capture_updates_only_the_app_linear_coefficients()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new OxygenCalibrationViewModel(device, settings)
        {
            StabilityWindowText = "2",
            StabilityThresholdText = "5",
            AverageSamplesText = "2",
        };

        PushOxygen(device, raw: 1000, calibrated: 0);
        vm.StartTwoPointCommand.Execute(null);
        vm.ConfirmPointCommand.Execute(null);

        // Stabilize and average point 1
        PushOxygen(device, raw: 1000, calibrated: 0);
        PushOxygen(device, raw: 1000, calibrated: 0);
        PushOxygen(device, raw: 1000, calibrated: 0);
        PushOxygen(device, raw: 1000, calibrated: 0);

        Assert.Equal(OxygenCalibrationStage.AwaitingSecondStandard, vm.Stage);
        vm.ConfirmPointCommand.Execute(null);

        // Stabilize and average point 2
        PushOxygen(device, raw: 2000, calibrated: 100);
        PushOxygen(device, raw: 2000, calibrated: 100);
        PushOxygen(device, raw: 2000, calibrated: 100);
        PushOxygen(device, raw: 2000, calibrated: 100);

        Assert.Equal(OxygenCalibrationStage.Proposed, vm.Stage);
        vm.ApplyProposalCommand.Execute(null);

        Assert.Equal(0.1, settings.Current.Calibration.OxygenA, precision: 12);
        Assert.Equal(-100.0, settings.Current.Calibration.OxygenB, precision: 12);
        Assert.Empty(device.Sent); // there is no O2 coefficient command on the wire
    }

    [Fact]
    public void Flow_capture_averages_distinct_telemetry_frames()
    {
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCaptureSamples = 2 },
        };
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(initial);
        using var vm = new FlowCalibrationViewModel(device, settings);
        vm.SelectedPoint!.FlowText = "1.0";
        PushFlow(device, 0.04);

        vm.SetpointText = "1.0";
        vm.SendSetpointCommand.Execute(null);
        Assert.Equal(
            // A/B/C rig: calibration blows through C = valve_1 on the default wiring (plan §1.3.1).
            """{"flowSetpoint":1.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            Assert.Single(device.Sent));

        PushFlow(device, 0.04);
        vm.CaptureVoltageCommand.Execute(null);
        PushFlow(device, 0.04);
        PushFlow(device, 0.06);

        Assert.Equal(0.05, vm.SelectedPoint.Voltage!.Value, precision: 12);
        Assert.False(vm.IsCapturing);
    }

    /// <summary>§L.5: an ack that never comes must not leave the page dead — after 15 s it says so and re-enables sending.</summary>
    [Fact]
    public void An_overdue_flowmeter_ack_reenables_sending_and_says_the_hub_keeps_retrying()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService(), time: clock);
        PushFlow(device, 0.04);
        Assert.True(vm.CanSendCurve);

        vm.SendCurveCommand.Execute(null);
        Assert.True(vm.IsAwaitingAck);
        Assert.False(vm.CanSendCurve);

        // The Hub keeps reporting the command as pending.
        clock.Advance(TimeSpan.FromSeconds(5));
        PushFlow(device, 0.04, pending: true);
        Assert.False(vm.IsAckOverdue);
        Assert.Contains("Aguardando", vm.StatusText, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(11));
        PushFlow(device, 0.04, pending: true);
        Assert.True(vm.IsAckOverdue);
        Assert.True(vm.CanSendCurve);
        Assert.Contains("continuará reenviando", vm.StatusText, StringComparison.Ordinal);

        // The ack lands: back to normal.
        PushFlow(device, 0.04);
        Assert.False(vm.IsAwaitingAck);
        Assert.False(vm.IsAckOverdue);
    }

    /// <summary>§O: the voltage can be typed, not only captured, and the two stay mirrored.</summary>
    [Theory]
    [InlineData("0.123456", 0.123456)]
    [InlineData("0,123456", 0.123456)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Typed_voltage_parses_point_or_comma_and_marks_the_point_as_typed(string text, double? expected)
    {
        var point = new FlowCalibrationPointViewModel("1.0", 0.05);
        Assert.Equal(FlowVoltageSource.Captured, point.Source);
        Assert.Equal(0.05.ToString("F6", System.Globalization.CultureInfo.CurrentCulture), point.VoltageText);

        point.VoltageText = text;

        Assert.Equal(expected, point.Voltage);
        Assert.Equal(FlowVoltageSource.Typed, point.Source);
        Assert.True(point.IsTyped);
        Assert.Equal("digitada", point.SourceLabel);
    }

    [Fact]
    public void Capture_overwrites_the_typed_text_and_marks_the_point_captured_again()
    {
        var point = new FlowCalibrationPointViewModel("1.0");
        point.VoltageText = "0.2";
        Assert.Equal(FlowVoltageSource.Typed, point.Source);

        point.SetCapturedVoltage(0.05);

        Assert.Equal(0.05, point.Voltage);
        Assert.Equal(0.05.ToString("F6", System.Globalization.CultureInfo.CurrentCulture), point.VoltageText);
        Assert.Equal(FlowVoltageSource.Captured, point.Source);
    }

    [Fact]
    public void A_typed_voltage_outside_the_adc_range_is_flagged_and_left_out_of_the_fit()
    {
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService());
        var before = vm.GetValidPoints().Count;
        var point = vm.Points[0];
        var original = point.Voltage;

        point.VoltageText = "5";

        Assert.True(point.IsVoltageOutOfRange);
        Assert.Equal(1, vm.OutOfRangePointCount);
        Assert.Equal(before - 1, vm.GetValidPoints().Count);
        Assert.Contains("fora de 0", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        point.VoltageText = original!.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(point.IsVoltageOutOfRange);
        Assert.Equal(before, vm.GetValidPoints().Count);
    }

    [Fact]
    public void Saving_points_persists_the_source_and_it_survives_a_reload()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using (var vm = new FlowCalibrationViewModel(device, settings))
        {
            vm.Points[1].VoltageText = "0.0311";
            vm.SavePointsCommand.Execute(null);
        }

        var stored = settings.Current.Calibration.FlowCalibrationPoints;
        Assert.Contains(stored, p => p.Source == FlowVoltageSource.Typed && Math.Abs(p.Voltage - 0.0311) < 1e-9);
        Assert.Contains(stored, p => p.Source == FlowVoltageSource.Captured);

        using var reloaded = new FlowCalibrationViewModel(device, settings);
        var typed = reloaded.Points.Single(p => p.IsTyped);
        Assert.Equal(0.0311, typed.Voltage!.Value, precision: 9);
        Assert.Equal("digitada", typed.SourceLabel);
    }

    [Fact]
    public void Flow_capture_locks_point_editing_and_connection_loss_clears_the_setpoint()
    {
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCaptureSamples = 2 },
        };
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService(initial));
        vm.SelectedPoint!.FlowText = "1.0";
        PushFlow(device, 0.04);

        vm.SetpointText = "1.0";
        vm.SendSetpointCommand.Execute(null);
        PushFlow(device, 0.04); // the flowmeter acknowledges the command

        // The trial setpoint is independent of the row, so another row can still record it.
        vm.AddEmptyPointCommand.Execute(null);
        Assert.True(vm.CanCapture);

        vm.CaptureVoltageCommand.Execute(null);

        Assert.False(vm.CanEditPoints);
        Assert.False(vm.AddEmptyPointCommand.CanExecute(null));

        device.PushState(ConnectionState.Reconnecting);

        Assert.False(vm.IsCapturing);
        Assert.False(vm.CanCapture);
        Assert.Contains("envie o setpoint novamente", vm.StatusText, StringComparison.CurrentCultureIgnoreCase);
    }

    [Fact]
    public void The_workspace_opens_on_the_certified_bench_points_and_the_v05_curve()
    {
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService());

        Assert.Equal(11, vm.Points.Count);
        Assert.Equal("0", vm.Points[0].FlowText);
        Assert.Equal(0.010330, vm.Points[0].Voltage!.Value, precision: 6);
        Assert.Equal(14.0, double.Parse(vm.Points[^1].FlowText, CultureInfo.CurrentCulture), precision: 6);

        // Fitting the seeded points reproduces what the flowmeter is already running.
        var reference = CalibrationMath.FirmwareDefault;
        var low = vm.Curve.LowVoltage!.Value;
        var high = vm.Curve.HighVoltage!.Value;
        Assert.Equal(reference.LowVoltage!.Value.A, low.A, precision: 3);
        Assert.Equal(reference.HighVoltage!.Value.F, high.F, precision: 6);
        Assert.True(Math.Abs(vm.Curve.DiscontinuityAtSplit!.Value) < 1e-9);
    }

    [Fact]
    public void A_settings_file_saved_with_no_points_still_opens_on_the_certified_reference()
    {
        // What a settings.json written before the reference run existed actually contains.
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCalibrationPoints = [] },
        };
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService(initial));

        Assert.Equal(11, vm.Points.Count);
        Assert.Equal(0.010330, vm.Points[0].Voltage!.Value, precision: 6);
        Assert.True(vm.Curve.IsComplete);
    }

    [Fact]
    public void Editing_the_certified_flow_keeps_the_trial_setpoint_alive()
    {
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService());
        PushFlow(device, 0.04);

        vm.SetpointText = "1.0";
        vm.SendSetpointCommand.Execute(null);
        PushFlow(device, 0.04);

        // The operator reads the real value off the certified standard and types it in; the
        // commanded setpoint must survive that edit.
        vm.SelectedPoint!.FlowText = "0.96";

        Assert.True(vm.CanCapture);
        Assert.True(vm.CanAdjust);
    }

    [Fact]
    public void Complete_flow_curve_sends_the_quartic_low_and_quadratic_high_coefficients()
    {
        var points = new[]
        {
            Point(0.0, 0.0098), Point(0.5, 0.0244), Point(0.75, 0.0400),
            Point(1.5, 0.10), Point(2.0, 0.20),
        };
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCalibrationPoints = points },
        };
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(initial);
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        vm.SendCurveCommand.Execute(null);

        var json = Assert.Single(device.Sent);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // maxFlow + a1/b1/k1/f1/c1 + k2/f2/c2 + flowTransitionVoltage: atomic 10-parameter command.
        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.Equal(50.0, root.GetProperty("maxFlow").GetDouble(), precision: 6);
        Assert.Equal(0.0545, root.GetProperty("flowTransitionVoltage").GetDouble(), precision: 4);
        var low = new PolynomialCalibration(
            root.GetProperty("k1").GetDouble(),
            root.GetProperty("f1").GetDouble(),
            root.GetProperty("c1").GetDouble())
        {
            A = root.GetProperty("a1").GetDouble(),
            B = root.GetProperty("b1").GetDouble(),
        };
        var high = new PolynomialCalibration(
            root.GetProperty("k2").GetDouble(),
            root.GetProperty("f2").GetDouble(),
            root.GetProperty("c2").GetDouble());

        // What actually reaches the flowmeter has to be continuous at the threshold.
        var split = FlowCalibrationCurve.DefaultTransitionVoltage;
        Assert.Equal(high.Evaluate(split), low.Evaluate(split), precision: 6);
        Assert.Equal(high.Derivative(split), low.Derivative(split), precision: 6);
        Assert.Equal(0.5, low.Evaluate(0.0244), precision: 6);
        Assert.Equal(5, settings.Current.Calibration.FlowCalibrationPoints.Length);

        static FlowCalibrationPoint Point(double flow, double voltage) => new()
        {
            FlowLitresPerMinute = flow,
            Voltage = voltage,
        };
    }

    [Fact]
    public void One_point_oxygen_keeps_the_slope_and_shifts_only_the_intercept()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var slope = settings.Current.Calibration.OxygenA;
        using var vm = new OxygenCalibrationViewModel(device, settings)
        {
            StabilityWindowText = "2",
            StabilityThresholdText = "5",
            AverageSamplesText = "2",
        };

        vm.IsOnePoint = true;
        Assert.Equal("100", vm.Reference1Text); // air saturation is the usual single standard

        PushOxygen(device, raw: 3000, calibrated: 60);
        vm.StartOnePointCommand.Execute(null);
        vm.ConfirmPointCommand.Execute(null);

        // Stabilize and average point 1
        PushOxygen(device, raw: 3000, calibrated: 60);
        PushOxygen(device, raw: 3000, calibrated: 60);
        PushOxygen(device, raw: 3000, calibrated: 60);
        PushOxygen(device, raw: 3000, calibrated: 60);

        Assert.Equal(OxygenCalibrationStage.Proposed, vm.Stage);
        Assert.True(vm.CanApplyProposal);

        vm.ApplyProposalCommand.Execute(null);

        var applied = settings.Current.Calibration;
        Assert.Equal(slope, applied.OxygenA, precision: 12);
        Assert.Equal(100.0, applied.DecodeOxygen(3000), precision: 9);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Oxygen_acquisition_criteria_can_be_changed_while_the_run_is_in_progress()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new OxygenCalibrationViewModel(device, settings)
        {
            StabilityWindowText = "3",
            StabilityThresholdText = "5",
            AverageSamplesText = "2",
        };

        PushOxygen(device, 1000, 0);
        vm.StartOnePointCommand.Execute(null);
        vm.ConfirmPointCommand.Execute(null);

        // Two frames into a three-frame window
        PushOxygen(device, 1000, 0);
        PushOxygen(device, 1000, 0);
        Assert.Equal(OxygenCalibrationStage.StabilizingFirst, vm.Stage);

        vm.StabilityWindowText = "2";

        // The window now holds enough stable frames, so the run advances immediately.
        Assert.Equal(OxygenCalibrationStage.AveragingFirst, vm.Stage);
    }

    [Fact]
    public void PH_acquisition_criteria_can_be_changed_while_the_run_is_in_progress()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var phControl = new PHControlViewModel(device, settings);
        using var vm = new PHCalibrationViewModel(device, settings, phControl)
        {
            StabilityWindowText = "3",
            StabilityThresholdText = "5",
            AverageSamplesText = "2",
        };

        PushPH(device, 1000);
        vm.StartOnePointCommand.Execute(null);
        vm.ConfirmPointCommand.Execute(null);

        // Two frames into a three-frame window: widening it must not restart the run.
        PushPH(device, 1000);
        PushPH(device, 1000);
        Assert.Equal(PHCalibrationStage.StabilizingFirst, vm.Stage);

        vm.StabilityWindowText = "2";

        // The window now holds enough stable frames, so the run advances immediately.
        Assert.Equal(PHCalibrationStage.AveragingFirst, vm.Stage);
    }

    private static void PushPH(RecordingDeviceService device, double raw)
        => device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            PHRaw = raw,
            PHCalibrated = 7,
        });

    private static void PushOxygen(RecordingDeviceService device, double raw, double calibrated)
        => device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            OxygenRaw = raw,
            OxygenCalibrated = calibrated,
        });

    private static void PushFlow(RecordingDeviceService device, double voltage, bool pending = false, double? transitionVoltage = FlowCalibrationCurve.DefaultTransitionVoltage)
        => device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            FlowVoltage = voltage,
            FlowmeterOnline = true,
            FlowCommandPending = pending,
            FlowTransitionVoltage = transitionVoltage,
        });

    [Fact]
    public void TransitionVoltage_Editing_RefitsCurve_AndMovesPointsBetweenSegments()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);

        // Initial default threshold is 0.0545 V
        Assert.Equal(0.0545, vm.TransitionVoltage);
        var initialLowCount = vm.LowPointCount;
        var initialHighCount = vm.HighPointCount;
        Assert.True(initialLowCount > 0);
        Assert.True(initialHighCount > 0);

        // Change transition voltage to 0.1000 V (above several points previously in the high segment)
        vm.TransitionVoltageText = "0.1000";

        Assert.Equal(0.1000, vm.TransitionVoltage);
        Assert.Null(vm.TransitionVoltageError);
        Assert.True(vm.IsTransitionVoltageValid);
        Assert.Equal(0.1000, vm.Curve.TransitionVoltage);
        Assert.True(vm.LowPointCount > initialLowCount);
        Assert.True(vm.HighPointCount < initialHighCount);
        Assert.Contains("0,1000", vm.PointDistributionText);
    }

    [Fact]
    public void TransitionVoltage_InvalidText_PreservesLastValidCurve_AndShowsError()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);

        var previousCurve = vm.Curve;
        var previousVt = vm.TransitionVoltage;

        vm.TransitionVoltageText = "invalid";
        Assert.NotNull(vm.TransitionVoltageError);
        Assert.False(vm.IsTransitionVoltageValid);
        Assert.False(vm.CanSendCurve);
        Assert.Equal(previousVt, vm.TransitionVoltage);
        Assert.Equal(previousCurve, vm.Curve);

        vm.TransitionVoltageText = "-0.01";
        Assert.NotNull(vm.TransitionVoltageError);
        Assert.False(vm.IsTransitionVoltageValid);
        Assert.False(vm.CanSendCurve);
        Assert.Equal(previousVt, vm.TransitionVoltage);

        vm.TransitionVoltageText = "3.5";
        Assert.NotNull(vm.TransitionVoltageError);
        Assert.False(vm.IsTransitionVoltageValid);
        Assert.False(vm.CanSendCurve);
        Assert.Equal(previousVt, vm.TransitionVoltage);
    }

    [Fact]
    public void SavePoints_PersistsTransitionVoltage_WithoutSendingCommands()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);

        vm.TransitionVoltageText = "0.0650";
        vm.SavePointsCommand.Execute(null);

        Assert.Empty(device.Sent);
        Assert.Equal(0.0650, settings.Current.Calibration.FlowTransitionVoltage, precision: 4);
    }

    [Fact]
    public void SendCurve_SendsAtomic10ParameterCommand_WithTransitionVoltage()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        vm.TransitionVoltageText = "0.0620";
        vm.SendCurveCommand.Execute(null);

        var json = Assert.Single(device.Sent);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.Equal(0.0620, root.GetProperty("flowTransitionVoltage").GetDouble(), precision: 4);
        Assert.True(root.TryGetProperty("maxFlow", out _));
        Assert.True(root.TryGetProperty("a1", out _));
        Assert.True(root.TryGetProperty("b1", out _));
        Assert.True(root.TryGetProperty("k1", out _));
        Assert.True(root.TryGetProperty("f1", out _));
        Assert.True(root.TryGetProperty("c1", out _));
        Assert.True(root.TryGetProperty("k2", out _));
        Assert.True(root.TryGetProperty("f2", out _));
        Assert.True(root.TryGetProperty("c2", out _));
    }

    [Fact]
    public void SendCurve_RejectsPartialOrDiscontinuousCurve()
    {
        var device = new RecordingDeviceService();
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings
            {
                // 2 points in high segment, 0 points in low segment: partial curve
                FlowCalibrationPoints =
                [
                    new() { FlowLitresPerMinute = 2.0, Voltage = 0.20 },
                    new() { FlowLitresPerMinute = 4.0, Voltage = 0.40 },
                ],
            },
        };
        var settings = new MemorySettingsService(initial);
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        Assert.False(vm.Curve.IsComplete);
        Assert.False(vm.CanSendCurve);

        vm.SendCurveCommand.Execute(null);

        Assert.Empty(device.Sent);
        Assert.Contains("exige ambos os segmentos", vm.StatusText);
    }

    [Fact]
    public void Telemetry_AckWithoutEcho_KeepsPendingState()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        vm.TransitionVoltageText = "0.0545";
        vm.SendCurveCommand.Execute(null);
        Assert.True(vm.IsAwaitingAck);

        // Telemetry arriving with FlowCommandPending = false, but NO FlowTransitionVoltage echo
        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = false,
            FlowTransitionVoltage = null,
            SensorCommOk = true,
            FlowVoltage = 0.04,
        });

        // Remains pending ack!
        Assert.True(vm.IsAwaitingAck);
        Assert.Contains("Aguardando confirmação", vm.StatusText);
    }

    [Fact]
    public void Telemetry_AckWithMismatchedEcho_EmitsWarningAndRefusesConfirmation()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        vm.TransitionVoltageText = "0.0545";
        vm.SendCurveCommand.Execute(null);
        Assert.True(vm.IsAwaitingAck);

        // Telemetry arriving with mismatched echo
        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = false,
            FlowTransitionVoltage = 0.0800,
            SensorCommOk = true,
            FlowVoltage = 0.04,
        });

        Assert.False(vm.IsAwaitingAck);
        Assert.Contains("divergente", vm.StatusText);
    }

    [Fact]
    public void Telemetry_AckWithMatchingEcho_ConfirmsCurveAndThreshold()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);
        PushFlow(device, 0.04);

        vm.TransitionVoltageText = "0.0545";
        vm.SendCurveCommand.Execute(null);
        Assert.True(vm.IsAwaitingAck);

        // Telemetry arriving with matching echo
        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = false,
            FlowTransitionVoltage = 0.0545,
            SensorCommOk = true,
            FlowVoltage = 0.04,
        });

        Assert.False(vm.IsAwaitingAck);
        Assert.Contains("confirmados pelo fluxômetro", vm.StatusText);
    }

    [Fact]
    public void LegacyNode_DisablesTransitionVoltageEditing_WithExplanation()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FlowCalibrationViewModel(device, settings);

        // Legacy node with firmware v11
        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowmeterNode = new ExternalNodeIdentity("192.168.4.4", "AA:BB:CC:DD:EE:04", "v11"),
            SensorCommOk = true,
            FlowVoltage = 0.04,
        });

        Assert.False(vm.IsTransitionVoltageEditable);
        Assert.False(vm.CanEditTransitionVoltage);
        Assert.NotNull(vm.TransitionVoltageUnsupportedReason);
        Assert.Contains("não suporta limiar editável", vm.TransitionVoltageUnsupportedReason);

        // Upgrade to firmware v12
        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowmeterNode = new ExternalNodeIdentity("192.168.4.4", "AA:BB:CC:DD:EE:04", "v12"),
            SensorCommOk = true,
            FlowVoltage = 0.04,
        });

        Assert.True(vm.IsTransitionVoltageEditable);
        Assert.True(vm.CanEditTransitionVoltage);
        Assert.Null(vm.TransitionVoltageUnsupportedReason);
    }

    [Fact]
    public void Settings_DefaultTransitionVoltage_MigratesLegacySettings()
    {
        var legacy = new CalibrationSettings();
        Assert.Equal(0.0545, legacy.FlowTransitionVoltage, precision: 4);
    }
}

public sealed class CalibrationSimulatorTests
{
    [Fact]
    public void Simulator_echoes_a_flow_setpoint_without_the_loop_flag_which_only_reports_control()
    {
        var model = new DeviceModel();

        // The Hub delivers the v05 mailbox from the setpoint alone: no flowmeterComm needed.
        Assert.True(WireCodec.ApplyCommand(model, CommandBuilders.FlowSetpoint(2.5, 50).ToJson(), out _));

        Assert.Equal(2.5, model.FlowSetpoint);
        var telemetry = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"FlowSetpoint\":2.50", telemetry, StringComparison.Ordinal);
        Assert.Contains("\"FlowControlEnabled\":false", telemetry, StringComparison.Ordinal);

        // flowmeterComm writes only the Hub's own loop-enabled flag, which it publishes back.
        Assert.True(WireCodec.ApplyCommand(
            model, CommandBuilders.FlowmeterLoopEnabled(true).ToJson(), out _));

        Assert.True(model.FlowmeterEnabled);
        Assert.Contains("\"FlowControlEnabled\":true", WireCodec.BuildTelemetry(model), StringComparison.Ordinal);
    }

    [Fact]
    public void Simulator_accepts_complete_pH_state_echo_and_flow_coefficients()
    {
        var model = new DeviceModel();
        var command = CommandBuilders.PHControl(6.8, 0.17, 5, 20, 50)
            .Merge(CommandBuilders.PHCalibration(6.98))
            .Merge(CommandBuilders.FlowCalibrationLow(2, 3, 4))
            .Merge(CommandBuilders.FlowCalibrationHigh(0, 5, 1));

        Assert.True(WireCodec.ApplyCommand(model, command.ToJson(), out var handshake));

        Assert.False(handshake);
        Assert.Equal(6.8, model.PHSetpoint);
        Assert.Equal(0.17, model.PHInactiveBand);
        Assert.Equal(5, model.PHOperationSeconds);
        Assert.Equal(20, model.PHMixSeconds);
        Assert.Equal(500, model.PHIntensity);
        Assert.Equal(6.98, model.PHDisplayValue);
        Assert.Equal(2, model.FlowK1);
        Assert.Equal(3, model.FlowF1);
        Assert.Equal(4, model.FlowC1);
        Assert.Equal(0, model.FlowK2);
        Assert.Equal(5, model.FlowF2);
        Assert.Equal(1, model.FlowC2);
    }
}
