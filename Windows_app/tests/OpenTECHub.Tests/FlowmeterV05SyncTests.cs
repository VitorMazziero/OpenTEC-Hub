using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class FlowmeterV05SyncTests
{
    /// <summary>
    /// The flowmeter's chips are now the shared control, not a hand-built pair. It was the only
    /// device rendering presence its own way — and, because of that, the only one without the
    /// routing chip, despite the Hub persisting <c>flowComm</c> exactly as it persists the others.
    /// </summary>
    [Fact]
    public void Control_and_panel_expose_the_flowmeter_chips_through_the_shared_control()
    {
        var controlXaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "ControlView.xaml"));
        var panelXaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "SynopticView.xaml"));

        Assert.Contains("FlowControl.Status", controlXaml, StringComparison.Ordinal);
        Assert.Contains("CanSendFlowCommands", controlXaml, StringComparison.Ordinal);

        // No hand-built copies left. Qualified by FlowControl on purpose: the bare property
        // names still appear on the rows, where they feed the state dot so it carries the same
        // severity as the chip beside it.
        Assert.DoesNotContain("FlowControl.ShowPendingChip", controlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowControl.IsFlowmeterOffline", controlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowControl.IsFlowmeterOffline", panelXaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Hub persists flowComm in NVS and the app persists the Vazão de Ar switch on the PC.
    /// A reboot can leave them disagreeing, and FlowControlEnabled is what the Fluxômetro offline
    /// alarm is conditioned on — so a silent divergence disables that alarm along with the loop.
    /// </summary>
    [Fact]
    public void The_flowmeter_reports_a_routing_disagreement_like_every_other_device()
    {
        var flow = new FlowControlViewModel(50) { IsLoopRequested = true };

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowControlEnabled = false,
        });

        Assert.True(flow.Status.HasCommMismatch);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowControlEnabled = true,
        });

        Assert.False(flow.Status.HasCommMismatch);
    }

    [Fact]
    public void Flow_state_tracks_pending_and_internal_link_independently()
    {
        var flow = new FlowControlViewModel(50);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = true,
        });

        Assert.True(flow.IsFlowmeterOnline);
        Assert.True(flow.IsFlowCommandPending);
        Assert.True(flow.IsAwaitingAck);
        Assert.False(flow.CanSendFlowCommands);
        Assert.Equal("Aguardando confirmação do fluxômetro...", flow.PendingStatusText);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        Assert.False(flow.IsFlowCommandPending);
        Assert.False(flow.IsAwaitingAck);
        Assert.True(flow.CanSendFlowCommands);
    }

    [Fact]
    public void Main_shutoff_is_staged_independently_and_reports_physical_state()
    {
        var flow = new FlowControlViewModel(50);
        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowValveMain = 0,
        });

        flow.RequestedMainValveClosed = true;

        Assert.True(flow.HasPendingChange);
        Assert.Equal("Aberta", flow.MainValveActualText);
        Assert.True(flow.TryBuildRequested(6.5, flowEnabled: true, out var command));
        Assert.Equal(
            """{"flowSetpoint":6.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            command.ToJson());
    }

    [Fact]
    public void Control_blocks_flow_while_pending_and_releases_it_after_ack()
    {
        using var fixture = new SyncFixture();
        fixture.PushFlow(online: true, pending: false);
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);

        Assert.True(fixture.Control.CanApplyFlowState);

        fixture.PushFlow(online: true, pending: true);
        Assert.False(fixture.Control.CanApplyFlowState);
        Assert.Contains("Aguardando confirmação", fixture.Control.StatusText, StringComparison.Ordinal);

        fixture.PushFlow(online: true, pending: false);
        Assert.True(fixture.Control.CanApplyFlowState);
    }

    [Fact]
    public void Control_keeps_staged_flow_until_the_hub_confirms_application()
    {
        using var fixture = new SyncFixture();
        fixture.PushFlow(online: true, pending: false);
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);

        fixture.Control.ApplyFlowStateCommand.Execute(null);

        Assert.Single(fixture.Device.Sent);
        Assert.True(fixture.Flow.IsAwaitingAck);
        Assert.True(fixture.Subsystems[3].HasPendingChange);

        fixture.PushFlow(online: true, pending: true);
        Assert.True(fixture.Subsystems[3].HasPendingChange);

        fixture.PushFlow(online: true, pending: false);
        Assert.False(fixture.Subsystems[3].HasPendingChange);
        Assert.Equal(0, fixture.Control.DirtyCount);
        Assert.Equal("", fixture.Control.StatusText);
    }

    [Fact]
    public void Control_blocks_and_warns_when_only_the_internal_flowmeter_link_is_down()
    {
        using var fixture = new SyncFixture();
        fixture.PushFlow(online: true, pending: false);
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);

        fixture.PushFlow(online: false, pending: false);

        Assert.Equal(ConnectionState.Connected, fixture.Device.State);
        Assert.False(fixture.Control.CanApplyFlowState);
        Assert.DoesNotContain("Desconectado da Central", fixture.Control.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Control.FlowRequestError);
    }

    [Fact]
    public void Hub_v7_operational_payloads_use_only_the_reliable_flow_keys()
    {
        var setpoint = CommandBuilders.FlowSetpoint(2.5, 50, valve1: true, valve2: false).ToJson();
        var safeStop = CommandBuilders.FlowSafeStop(50).ToJson();

        Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            setpoint);
        Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            safeStop);
        Assert.DoesNotContain("flowmeterComm", setpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("flowmeterComm", safeStop, StringComparison.Ordinal);
    }

    [Fact]
    public void The_loop_flag_is_its_own_frame_and_never_rides_inside_the_v05_payload()
    {
        // flowmeterComm is not routed to the v05, so it stays out of the frames above. It is
        // still the only writer of the Hub's FlowControlEnabled, so it is sent where the loop is
        // switched — on its own, alongside the setpoint or the safe-stop.
        Assert.Equal("""{"flowmeterComm":1}""", CommandBuilders.FlowmeterLoopEnabled(true).ToJson());
        Assert.Equal("""{"flowmeterComm":0}""", CommandBuilders.FlowmeterLoopEnabled(false).ToJson());

        var enable = CommandBuilders.FlowSetpoint(2.5, 50)
            .Merge(CommandBuilders.FlowmeterLoopEnabled(true)).ToJson();
        Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0,"flowmeterComm":1}""",
            enable);
    }

    [Fact]
    public void Calibration_commands_require_online_flowmeter_and_no_pending_ack()
    {
        var device = new RecordingDeviceService();
        using var calibration = new FlowCalibrationViewModel(device, new MemorySettingsService());
        calibration.SelectedPoint!.FlowText = "1.0";

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true });
        Assert.True(calibration.CanSendSetpoint);

        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = true,
        });
        Assert.False(calibration.CanSendSetpoint);
        Assert.False(calibration.CanAdjust);
        Assert.False(calibration.CanSendCurve);

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
        Assert.False(calibration.CanSendSetpoint);
        Assert.Equal("Fluxômetro Desconectado da Central.", calibration.StatusText);
    }

    [Fact]
    public void TryBuildRequested_rejects_sub_cutoff_setpoints()
    {
        var flow = new FlowControlViewModel(50);
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowControlEnabled = true });

        // Zero is accepted (safe cutoff)
        Assert.True(flow.TryBuildRequested(0.0, flowEnabled: true, out var cmdZero));
        Assert.Contains("flowSetpoint", cmdZero.ToJson());

        // >= 0.10 is accepted
        Assert.True(flow.TryBuildRequested(0.10, flowEnabled: true, out var cmdCutoff));
        Assert.True(flow.TryBuildRequested(1.5, flowEnabled: true, out var cmdNormal));

        // Sub-cutoff range (0.00, 0.10) is rejected
        Assert.False(flow.TryBuildRequested(0.01, flowEnabled: true, out _));
        Assert.False(flow.TryBuildRequested(0.05, flowEnabled: true, out _));
        Assert.False(flow.TryBuildRequested(0.09, flowEnabled: true, out _));
    }

    [Fact]
    public void EnableWifiReconnect_dispatches_reconnectWifi_command()
    {
        var dispatcher = new StubDispatcher();
        var flow = new FlowControlViewModel(50, dispatcher: dispatcher);

        flow.EnableWifiReconnectCommand.Execute(null);

        Assert.Single(dispatcher.Sent);
        Assert.Contains("\"reconnectWifi\":1", dispatcher.Sent[0]);
    }

    [Fact]
    public void Flowmeter_plausibility_diagnostic_detects_valve_flow_mismatch()
    {
        var flow = new FlowControlViewModel(50);

        // Nominal: open route, active flow
        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowValveMain = 0,
            FlowValve1 = 1,
            FlowValve2 = 0,
            FlowSetpoint = 3.0,
            FlowRate = 2.9,
        });
        Assert.Null(flow.FlowPlausibilityWarning);

        // Anomaly 1: commanded open with target >= 2.0, but zero flow (< 0.2)
        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowValveMain = 0,
            FlowValve1 = 1,
            FlowValve2 = 0,
            FlowSetpoint = 3.0,
            FlowRate = 0.05,
        });
        Assert.NotNull(flow.FlowPlausibilityWarning);
        Assert.Contains("alimentação elétrica", flow.FlowPlausibilityWarning);

        // Anomaly 2: commanded shut, but unintended flow detected (> 0.5)
        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowValveMain = 1,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowSetpoint = 0.0,
            FlowRate = 1.2,
        });
        Assert.NotNull(flow.FlowPlausibilityWarning);
        Assert.Contains("vedação mecânica", flow.FlowPlausibilityWarning);
    }

    [Fact]
    public void NodeFirmwareCatalog_validates_v11_and_v11_0()
    {
        Assert.True(OpenTECHub.Services.Communication.NodeFirmwareCatalog.IsValidated(
            OpenTECHub.Services.Communication.NodeFirmwareCatalog.Flowmeter, "v11"));
        Assert.True(OpenTECHub.Services.Communication.NodeFirmwareCatalog.IsValidated(
            OpenTECHub.Services.Communication.NodeFirmwareCatalog.Flowmeter, "v11.0"));
        Assert.False(OpenTECHub.Services.Communication.NodeFirmwareCatalog.IsValidated(
            OpenTECHub.Services.Communication.NodeFirmwareCatalog.Flowmeter, "v10"));
        Assert.False(OpenTECHub.Services.Communication.NodeFirmwareCatalog.IsValidated(
            OpenTECHub.Services.Communication.NodeFirmwareCatalog.Flowmeter, "v9.0"));
    }

    private sealed class SyncFixture : IDisposable
    {
        public SyncFixture()
        {
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService();
            Flow = new FlowControlViewModel(50);
            Subsystems =
            [
                Create("temperature", "Temperatura", "°C", 1, 15, 60, false,
                    value => OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, value), 30),
                Create("motor", "Agitação", "rpm", 0, 15, 1000, true,
                    value => CommandBuilders.MotorSetpoint((int)value), 300),
                Create("oxygen", "Oxigênio", "%", 1, 0, 100, false,
                    value => OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, value), 30),
                new SubsystemViewModel(
                    new ProcessVariableViewModel("flow", "Vazão", "L/min", 2),
                    new SubsystemSpec(
                        0, 50, false,
                        value => Flow.BuildSetpointPreservingRoute(value),
                        () => Flow.BuildSafeStop(),
                        OnCommitted: (_, enabled) => Flow.CommitFromFlowSetpoint(enabled),
                        CanApplyNow: () => Flow.CanSendFlowCommands),
                    Device,
                    1),
                Create("pressure", "Pressão", "kPa", 1, 1, 380, false,
                    value => OpenTECCommand.Create().Set(CommandKeys.PressureReference, value), 100),
            ];

            PH = new PHControlViewModel(Device, Settings);
            Nutrient = new NutrientControlViewModel(Device, Settings);
            Antifoam = new AntifoamControlViewModel(Device, Settings);
            Foam = new FoamControlViewModel(Device, Settings);
            Agitator = new FlaskAgitatorViewModel(Device, Settings);
            Biomass = new BiomassControlViewModel(Device, Settings);
            Pump = new PumpControlViewModel(Device, Settings);
            var arbiter = new CommandArbiter(Device, TimeProvider.System);
            Cascade = new CascadeService(arbiter, arbiter, Settings, new FakeKlaProfileStore(), TimeProvider.System);
            Control = new ControlViewModel(
                Subsystems, Flow, PH, Nutrient, Antifoam, Foam, Agitator, Biomass, Pump,
                new ServoDriveViewModel(Device),
                Device, Settings, new NullDialogService(), Cascade);
        }

        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public FlowControlViewModel Flow { get; }
        public IReadOnlyList<SubsystemViewModel> Subsystems { get; }
        public PHControlViewModel PH { get; }
        public NutrientControlViewModel Nutrient { get; }
        public AntifoamControlViewModel Antifoam { get; }
        public FoamControlViewModel Foam { get; }
        public FlaskAgitatorViewModel Agitator { get; }
        public BiomassControlViewModel Biomass { get; }
        public PumpControlViewModel Pump { get; }
        public CascadeService Cascade { get; }
        public ControlViewModel Control { get; }

        public void PushFlow(bool online, bool pending)
            => Device.PushTelemetry(new SensorSnapshot
            {
                FlowmeterOnline = online,
                FlowCommandPending = pending,
            });

        private SubsystemViewModel Create(
            string id,
            string name,
            string unit,
            int decimals,
            double minimum,
            double maximum,
            bool integer,
            Func<double, OpenTECCommand> apply,
            double initial)
            => new(
                new ProcessVariableViewModel(id, name, unit, decimals),
                new SubsystemSpec(
                    minimum,
                    maximum,
                    integer,
                    apply,
                    () => apply(0)),
                Device,
                initial);

        public void Dispose()
        {
            Control.Dispose();
            PH.Dispose();
            Antifoam.Dispose();
            Foam.Dispose();
            Biomass.Dispose();
            Pump.Dispose();
            Cascade.Dispose();
        }
    }

    private sealed class NullDialogService : IDialogService
    {
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => false;

        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => false;

        public bool PromptInput(
            string title,
            string message,
            out string response,
            string initialValue = "")
        {
            response = initialValue;
            return false;
        }

        public OpenTECHub.Services.Dialogs.RecipeStartOption PromptRecipeStart(string recipeName) => OpenTECHub.Services.Dialogs.RecipeStartOption.StartPreserving;
    }
}
