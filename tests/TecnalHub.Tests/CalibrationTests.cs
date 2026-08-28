using System.Globalization;
using System.Text.Json;
using TecnalHub.Protocol;
using TecnalHub.Services.Calibration;
using TecnalHub.Services.Persistence;
using TecnalHub.Simulator;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

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
        var split = FlowCalibrationCurve.SplitVoltage;

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
        var split = FlowCalibrationCurve.SplitVoltage;
        Assert.Equal(0.83358133, low.Evaluate(split), precision: 6);
        Assert.Equal(0.83358145, high.Evaluate(split), precision: 6);
        Assert.Equal(11.7213108, low.Derivative(split), precision: 4);
        Assert.Equal(11.7213070, high.Derivative(split), precision: 4);
        Assert.True(Math.Abs(curve.DiscontinuityAtSplit!.Value) < 1e-6);
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
        using var vm = new OxygenCalibrationViewModel(device, settings);

        PushOxygen(device, raw: 1000, calibrated: 0);
        vm.CapturePoint1Command.Execute(null);
        PushOxygen(device, raw: 2000, calibrated: 100);
        vm.CapturePoint2Command.Execute(null);
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
            """{"flowSetpoint":1.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            Assert.Single(device.Sent));

        PushFlow(device, 0.04);
        vm.CaptureVoltageCommand.Execute(null);
        PushFlow(device, 0.04);
        PushFlow(device, 0.06);

        Assert.Equal(0.05, vm.SelectedPoint.Voltage!.Value, precision: 12);
        Assert.False(vm.IsCapturing);
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

        // maxFlow + a1/b1/k1/f1/c1 + k2/f2/c2: the quartic low model of firmware V05.
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.Equal(50.0, root.GetProperty("maxFlow").GetDouble(), precision: 6);
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
        var split = FlowCalibrationCurve.SplitVoltage;
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
        using var vm = new OxygenCalibrationViewModel(device, settings);

        vm.IsOnePoint = true;
        Assert.Equal("100", vm.Reference1Text); // air saturation is the usual single standard

        PushOxygen(device, raw: 3000, calibrated: 60);
        vm.CapturePoint1Command.Execute(null);

        Assert.False(vm.CanCapturePoint2); // the second standard belongs to the two-point run
        Assert.True(vm.CanApplyProposal);

        vm.ApplyProposalCommand.Execute(null);

        var applied = settings.Current.Calibration;
        Assert.Equal(slope, applied.OxygenA, precision: 12);
        Assert.Equal(100.0, applied.DecodeOxygen(3000), precision: 9);
        Assert.Empty(device.Sent);
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

    private static void PushFlow(RecordingDeviceService device, double voltage)
        => device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            FlowVoltage = voltage,
            FlowmeterOnline = true,
        });
}

public sealed class CalibrationSimulatorTests
{
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
