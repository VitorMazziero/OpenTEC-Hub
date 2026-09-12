using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Etapa 2 of the A/B/C plan: every producer of a gas command goes through
/// <see cref="GasRouting"/>, reads the wiring from settings, and none of them can put a
/// dead-ended line on the wire.
/// </summary>
public class GasRouteProducersTests
{
    private static MemorySettingsService SettingsWith(GasInput airInlet)
        => new(new AppSettings { GasRig = new GasRigSettings { AirInletInput = airInlet } });

    // ── Detail pane / shell: preserve the observed route, never a dead end ───

    [Fact]
    public void Detail_pane_setpoint_keeps_the_observed_route()
    {
        var flow = new FlowControlViewModel(initialMaxFlow: 50.0, settings: SettingsWith(GasInput.Input2));

        // Observed on B/C (valve_1 on the default wiring): a setpoint edit stays there.
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = 1, FlowValve2 = 0, FlowSetpoint = 2.0 });
        Assert.Equal(
            """{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            flow.BuildSetpointPreservingRoute(3.0).ToJson());

        // Observed on A: stays on A.
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = 0, FlowValve2 = 1, FlowSetpoint = 2.0 });
        Assert.Equal(
            """{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            flow.BuildSetpointPreservingRoute(3.0).ToJson());
    }

    [Fact]
    public void Detail_pane_setpoint_never_reproduces_a_dead_ended_line()
    {
        var flow = new FlowControlViewModel(initialMaxFlow: 50.0, settings: SettingsWith(GasInput.Input2));

        // Both outputs observed closed with a setpoint above zero (or a closed rig at rest):
        // a new setpoint goes to the reactor rather than into a closed line.
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = 0, FlowValve2 = 0, FlowSetpoint = 0.0 });
        Assert.Equal(
            """{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            flow.BuildSetpointPreservingRoute(3.0).ToJson());

        // A zero setpoint on a closed rig stays closed.
        Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            flow.BuildSetpointPreservingRoute(0.0).ToJson());
    }

    [Fact]
    public void Detail_pane_setpoint_follows_the_configured_wiring()
    {
        // A on input 1: the observed pair (1,0) *is* the reactor, and an observed dead end goes to valve_1.
        var flow = new FlowControlViewModel(initialMaxFlow: 50.0, settings: SettingsWith(GasInput.Input1));
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = 0, FlowValve2 = 0, FlowSetpoint = 0.0 });
        Assert.Equal(
            """{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            flow.BuildSetpointPreservingRoute(3.0).ToJson());
    }

    // ── Cascade ──────────────────────────────────────────────────────────────

    [Fact]
    public void Cascade_frame_sends_air_to_the_reactor_on_the_configured_wiring()
    {
        var result = new CascadeActuationResult(AerationLpm: 2.5, OxygenSetpoint: 40.0, AgitationRpm: 300, Terms: default);
        Assert.Contains("\"valve_1\":0,\"valve_2\":1", CascadeController.BuildCommand(result, GasRigConfiguration.Default).ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"valve_1\":1,\"valve_2\":0", CascadeController.BuildCommand(result, new GasRigConfiguration(GasInput.Input1)).ToJson(), StringComparison.Ordinal);

        var idle = result with { AerationLpm = 0.0 };
        Assert.Contains("\"valve_1\":0,\"valve_2\":0,\"v_Flow\":1", CascadeController.BuildCommand(idle, GasRigConfiguration.Default).ToJson(), StringComparison.Ordinal);
    }

    // ── Calibration: through C by default, reactor on request ────────────────

    [Fact]
    public void Flow_calibration_blows_through_C_unless_the_operator_picks_the_reactor()
    {
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, SettingsWith(GasInput.Input2));
        device.PushTelemetry(new SensorSnapshot { SensorCommOk = true, FlowVoltage = 0.04, FlowmeterOnline = true });

        Assert.Equal(GasRoute.VentAndNitrogen, vm.CalibrationRoute);
        Assert.Contains("N₂ fechado na fonte", vm.CalibrationRouteText, StringComparison.Ordinal);

        vm.SetpointText = "1.0";
        vm.SendSetpointCommand.Execute(null);
        Assert.Equal("""{"flowSetpoint":1.0,"valve_1":1,"valve_2":0,"v_Flow":0}""", device.Sent.Last());

        vm.CalibrateThroughReactor = true;
        Assert.Equal(GasRoute.Reactor, vm.CalibrationRoute);
        vm.SetpointText = "1.5";
        vm.SendSetpointCommand.Execute(null);
        Assert.Equal("""{"flowSetpoint":1.5,"valve_1":0,"valve_2":1,"v_Flow":0}""", device.Sent.Last());

        // Zero closes everything, whichever route is selected.
        vm.SetpointText = "0";
        vm.SendSetpointCommand.Execute(null);
        Assert.Equal("""{"flowSetpoint":0.0,"valve_1":0,"valve_2":0,"v_Flow":1}""", device.Sent.Last());
    }

    [Fact]
    public void Calibration_builder_refuses_a_closed_route_with_flow()
        => Assert.Throws<ArgumentException>(() => CommandBuilders.FlowCalibrationSetpoint(1.0, GasRoute.Closed, GasRigConfiguration.Default));

    // ── Proportional gas of the external pump ────────────────────────────────

    [Fact]
    public void Proportional_gas_goes_to_the_reactor_on_the_configured_wiring()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, SettingsWith(GasInput.Input1))
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };
        device.Sent.Clear();

        device.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });

        Assert.Equal(
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            Assert.Single(device.Sent));
    }
}
