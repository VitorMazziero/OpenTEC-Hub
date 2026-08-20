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
    public void Flow_fit_uses_quadratic_low_and_linear_high_segments()
    {
        var points = new[]
        {
            (Voltage: 0.01, Flow: Polynomial(0.01)),
            (Voltage: 0.03, Flow: Polynomial(0.03)),
            (Voltage: 0.05, Flow: Polynomial(0.05)),
            (Voltage: 0.10, Flow: (5 * 0.10) + 1),
            (Voltage: 0.20, Flow: (5 * 0.20) + 1),
        };

        var fit = CalibrationMath.FitFlowCurve(points);

        var low = Assert.IsType<PolynomialCalibration>(fit.LowVoltage);
        var high = Assert.IsType<PolynomialCalibration>(fit.HighVoltage);
        Assert.Equal(2.0, low.K, precision: 8);
        Assert.Equal(3.0, low.F, precision: 8);
        Assert.Equal(4.0, low.C, precision: 8);
        Assert.Equal(0.0, high.K, precision: 12);
        Assert.Equal(5.0, high.F, precision: 8);
        Assert.Equal(1.0, high.C, precision: 8);

        static double Polynomial(double x) => (2 * x * x) + (3 * x) + 4;
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
            """{"pHSetpoint":0.0,"pHError":0.15,"pHOperation":1.0,"pHMix":60.0,"pHIntensity":0.0}""",
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
        Assert.Empty(device.Sent); // there is no O2 coefficient command in v.6
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

        vm.PrepareSelectedPointCommand.Execute(null);
        Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":1.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            Assert.Single(device.Sent));

        PushFlow(device, 0.04);
        vm.CaptureVoltageCommand.Execute(null);
        PushFlow(device, 0.04);
        PushFlow(device, 0.06);

        Assert.Equal(0.05, vm.SelectedPoint.Voltage!.Value, precision: 12);
        Assert.False(vm.IsCapturing);
    }

    [Fact]
    public void Flow_capture_locks_point_editing_and_connection_loss_requires_reprepare()
    {
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCaptureSamples = 2 },
        };
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, new MemorySettingsService(initial));
        vm.SelectedPoint!.FlowText = "1.0";

        var preparedPoint = vm.SelectedPoint;
        vm.PrepareSelectedPointCommand.Execute(null);
        vm.AddEmptyPointCommand.Execute(null);
        Assert.False(vm.CanCapture); // another row cannot consume the prepared command
        vm.SelectedPoint = preparedPoint;

        PushFlow(device, 0.04);
        vm.CaptureVoltageCommand.Execute(null);

        Assert.False(vm.CanEditPoints);
        Assert.False(vm.AddEmptyPointCommand.CanExecute(null));

        device.PushState(ConnectionState.Reconnecting);

        Assert.False(vm.IsCapturing);
        Assert.False(vm.CanCapture);
        Assert.Contains("prepare novamente", vm.StatusText, StringComparison.CurrentCultureIgnoreCase);
    }

    [Fact]
    public void Complete_flow_curve_sends_all_six_v6_coefficients_and_persists_points()
    {
        var points = new[]
        {
            Point(4.0302, 0.01), Point(4.0918, 0.03), Point(4.155, 0.05),
            Point(1.5, 0.10), Point(2.0, 0.20),
        };
        var initial = new AppSettings
        {
            Calibration = new CalibrationSettings { FlowCalibrationPoints = points },
        };
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(initial);
        using var vm = new FlowCalibrationViewModel(device, settings);

        vm.SendCurveCommand.Execute(null);

        var json = Assert.Single(device.Sent);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal(2.0, root.GetProperty("k1").GetDouble(), precision: 6);
        Assert.Equal(3.0, root.GetProperty("f1").GetDouble(), precision: 6);
        Assert.Equal(4.0, root.GetProperty("c1").GetDouble(), precision: 6);
        Assert.Equal(0.0, root.GetProperty("k2").GetDouble(), precision: 12);
        Assert.Equal(5.0, root.GetProperty("f2").GetDouble(), precision: 6);
        Assert.Equal(1.0, root.GetProperty("c2").GetDouble(), precision: 6);
        Assert.Equal(5, settings.Current.Calibration.FlowCalibrationPoints.Length);

        static FlowCalibrationPoint Point(double flow, double voltage) => new()
        {
            FlowLitresPerMinute = flow,
            Voltage = voltage,
        };
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
