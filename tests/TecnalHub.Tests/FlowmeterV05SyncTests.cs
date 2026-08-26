using System.IO;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

public sealed class FlowmeterV05SyncTests
{
    [Fact]
    public void Control_and_panel_expose_pending_and_offline_flowmeter_chips()
    {
        var controlXaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "ControlView.xaml"));
        var panelXaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "SynopticView.xaml"));

        Assert.Contains("FlowControl.ShowPendingChip", controlXaml, StringComparison.Ordinal);
        Assert.Contains("FlowControl.IsFlowmeterOffline", controlXaml, StringComparison.Ordinal);
        Assert.Contains("CanSendFlowCommands", controlXaml, StringComparison.Ordinal);
        Assert.Contains("FlowControl.ShowPendingChip", panelXaml, StringComparison.Ordinal);
        Assert.Contains("FlowControl.IsFlowmeterOffline", panelXaml, StringComparison.Ordinal);
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
        Assert.Contains("confirmado", fixture.Control.StatusText, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("Fluxômetro Desconectado da Central", fixture.Control.StatusText);
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
    public void Calibration_commands_require_online_flowmeter_and_no_pending_ack()
    {
        var device = new RecordingDeviceService();
        using var calibration = new FlowCalibrationViewModel(device, new MemorySettingsService());
        calibration.SelectedPoint!.FlowText = "1.0";

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true });
        Assert.True(calibration.CanPrepare);

        device.PushTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowCommandPending = true,
        });
        Assert.False(calibration.CanPrepare);
        Assert.False(calibration.CanAdjust);
        Assert.False(calibration.CanSendCurve);

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
        Assert.False(calibration.CanPrepare);
        Assert.Equal("Fluxômetro Desconectado da Central.", calibration.StatusText);
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
                    value => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value), 30),
                Create("motor", "Agitação", "rpm", 0, 50, 1000, true,
                    value => CommandBuilders.MotorSetpoint((int)value), 300),
                Create("oxygen", "Oxigênio", "%", 1, 0, 100, false,
                    value => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, value), 30),
                new SubsystemViewModel(
                    new ProcessVariableViewModel("flow", "Vazão", "L/min", 2),
                    new SubsystemSpec(
                        0, 50, false,
                        value => Flow.BuildSetpointUsingObservedValves(value),
                        () => Flow.BuildSafeStop(),
                        OnCommitted: (_, enabled) => Flow.CommitFromFlowSetpoint(enabled),
                        CanApplyNow: () => Flow.CanSendFlowCommands),
                    Device,
                    1),
                Create("pressure", "Pressão", "kPa", 1, 1, 380, false,
                    value => TecnalCommand.Create().Set(CommandKeys.PressureReference, value), 100),
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
            Func<double, TecnalCommand> apply,
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
    }
}
