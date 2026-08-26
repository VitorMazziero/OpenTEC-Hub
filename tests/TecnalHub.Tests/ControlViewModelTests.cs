using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>Acceptance tests for Phase 1b WP6.</summary>
public sealed class ControlViewModelTests
{
    [Fact]
    public void Bulk_apply_combines_dirty_rows_and_valves_into_one_wire_frame()
    {
        using var fixture = new ControlFixture();

        fixture.Subsystems[0].Stage(37.5, isEnabled: true);
        fixture.Flow.Stage(50.0, valve1: true, valve2: false);
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);

        Assert.True(fixture.Control.CanApplyAll);
        fixture.Control.ApplyAllCommand.Execute(null);

        Assert.Equal(
            """{"tempSetpoint":37.5,"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            Assert.Single(fixture.Device.Sent));
        Assert.True(fixture.Flow.IsAwaitingAck);
        fixture.Device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowCommandPending = false });
        Assert.Equal(0, fixture.Control.DirtyCount);
        Assert.True(fixture.Subsystems[0].AppliedIsEnabled);
        Assert.True(fixture.Subsystems[3].AppliedIsEnabled);
    }

    [Fact]
    public void Loading_a_preset_only_stages_fields_and_never_sends()
    {
        var preset = new SetpointPreset
        {
            Name = "Ensaio A",
            TemperatureCelsius = 32.5,
            TemperatureEnabled = true,
            MotorRpm = 450,
            MotorEnabled = true,
            OxygenPercent = 35,
            OxygenEnabled = true,
            FlowLitresPerMinute = 3.2,
            MaxFlowLitresPerMinute = 20,
            FlowEnabled = true,
            Valve1Open = true,
            Valve2Open = false,
            PressureKilopascal = 90,
            PressureEnabled = true,
        };

        using var fixture = new ControlFixture(new AppSettings { SetpointPresets = [preset] });

        fixture.Control.LoadPresetCommand.Execute(null);

        Assert.Empty(fixture.Device.Sent);
        Assert.Equal("32.5", fixture.Subsystems[0].SetpointText.Replace(',', '.'));
        Assert.Equal("3.20", fixture.Subsystems[3].SetpointText.Replace(',', '.'));
        Assert.True(fixture.Flow.RequestedValve1);
        Assert.False(fixture.Flow.RequestedValve2);
        Assert.Equal(20, fixture.Flow.MaximumForCommand);
        Assert.Contains("nada foi enviado", fixture.Control.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applying_valves_at_zero_flow_makes_the_inverted_vent_state_explicit()
    {
        using var fixture = new ControlFixture();

        fixture.Subsystems[3].Stage(0, isEnabled: true);
        fixture.Flow.Stage(50, valve1: true, valve2: true);
        fixture.Control.ApplyFlowStateCommand.Execute(null);

        Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":1,"v_Flow":1}""",
            Assert.Single(fixture.Device.Sent));
    }

    [Fact]
    public void Safe_stop_defaults_to_cancel_and_exposes_the_exact_command()
    {
        using var fixture = new ControlFixture();
        fixture.Dialogs.ConfirmResult = false;

        fixture.Control.SafeStopCommand.Execute(null);

        Assert.Empty(fixture.Device.Sent);
        Assert.Equal(1, fixture.Dialogs.Calls);
        Assert.Equal(
            """{"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0,"pHSetpoint":0.0,"pHError":0.17,"pHOperation":3.0,"pHMix":10.0,"pHIntensity":0.0,"nutriOperation":999.0,"nutriMix":1.0,"nutriOpCycle":500.0,"nutriMixCycle":1.0,"nutriIntensity":0.0,"antifoamOperation":0.0,"antifoamMix":2.0,"antifoamIntensity":0.0,"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":50.0,"agitatorDir":1,"pumpComm":0,"mode":0,"speed":0}""",
            fixture.Dialogs.ExactCommand);
    }

    [Fact]
    public void Confirmed_safe_stop_sends_once_and_closes_both_requested_valves()
    {
        using var fixture = new ControlFixture();
        fixture.Dialogs.ConfirmResult = true;
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);
        fixture.Flow.Stage(50, valve1: true, valve2: true);

        fixture.Control.SafeStopCommand.Execute(null);

        Assert.Single(fixture.Device.Sent);
        Assert.All(fixture.Subsystems, subsystem => Assert.False(subsystem.IsEnabled));
        Assert.False(fixture.Flow.RequestedValve1);
        Assert.False(fixture.Flow.RequestedValve2);
    }

    [Fact]
    public void Flow_setpoint_above_staged_maximum_is_refused_not_clamped()
    {
        using var fixture = new ControlFixture();

        fixture.Flow.Stage(2.0, valve1: false, valve2: false);
        fixture.Subsystems[3].Stage(2.5, isEnabled: true);

        Assert.False(fixture.Control.CanApplyAll);
        Assert.NotNull(fixture.Control.FlowRequestError);
        fixture.Control.ApplyAllCommand.Execute(null);
        Assert.Empty(fixture.Device.Sent);
    }

    [Fact]
    public void Safe_stop_stops_the_three_dosing_actuators_but_leaves_the_foam_sensor()
    {
        using var fixture = new ControlFixture();
        fixture.Dialogs.ConfirmResult = true;

        fixture.Control.SafeStopCommand.Execute(null);

        var json = Assert.Single(fixture.Device.Sent);
        Assert.Contains(""","nutriIntensity":0.0""", json, StringComparison.Ordinal);
        Assert.Contains(""","antifoamIntensity":0.0""", json, StringComparison.Ordinal);
        Assert.Contains("\"agitatorOn\":0", json, StringComparison.Ordinal);
        // Foam monitoring must survive a stop: the sensor keys are absent.
        Assert.DoesNotContain("distanceSensorComm", json, StringComparison.Ordinal);
        Assert.False(fixture.Nutrient.IsEnabled);
        Assert.False(fixture.Antifoam.IsEnabled);
        Assert.False(fixture.Agitator.IsEnabled);
    }

    [Fact]
    public void Bulk_apply_merges_the_nutrient_and_antifoam_pumps_into_one_frame()
    {
        using var fixture = new ControlFixture();

        fixture.Nutrient.IsEnabled = true;
        fixture.Nutrient.PumpSpeedPercentText = "40";
        fixture.Antifoam.IsEnabled = true;
        fixture.Antifoam.PumpSpeedPercentText = "20";

        Assert.True(fixture.Control.CanApplyAll);
        fixture.Control.ApplyAllCommand.Execute(null);

        var json = Assert.Single(fixture.Device.Sent);
        Assert.Contains(""","nutriIntensity":40.0""", json, StringComparison.Ordinal);
        Assert.Contains(""","antifoamIntensity":20.0""", json, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Control.DirtyCount);
    }

    [Fact]
    public void Oxygen_toggle_engages_cascade_and_locks_overridden_actuators()
    {
        using var fixture = new ControlFixture();
        var oxygenRow = fixture.Control.Rows[2];
        var agitationRow = fixture.Control.Rows[1];
        var aerationRow = fixture.Control.Rows[3];

        oxygenRow.SelectedOxygenMode = "Agitação";
        oxygenRow.IsCascadeEngaged = true;

        Assert.True(fixture.Cascade.IsEngaged);
        Assert.True(oxygenRow.IsCascadeEngaged);
        Assert.False(oxygenRow.CanEditOxygenMode);
        Assert.True(agitationRow.IsOverriddenByCascade);
        Assert.False(aerationRow.IsOverriddenByCascade);
        Assert.True(agitationRow.EffectiveActive);

        // Disengage via toggle
        oxygenRow.IsCascadeEngaged = false;
        Assert.False(fixture.Cascade.IsEngaged);
        Assert.False(oxygenRow.IsCascadeEngaged);
        Assert.True(oxygenRow.CanEditOxygenMode);
        Assert.False(agitationRow.IsOverriddenByCascade);
    }

    [Fact]
    public void Oxygen_toggle_reverts_when_cannot_engage()
    {
        using var fixture = new ControlFixture();
        var oxygenRow = fixture.Control.Rows[2];

        // Disconnect device to make CanEngage fail
        fixture.Device.PushState(ConnectionState.Faulted);

        oxygenRow.IsCascadeEngaged = true;

        Assert.False(fixture.Cascade.IsEngaged);
        Assert.False(oxygenRow.IsCascadeEngaged);
        Assert.Contains("Conecte-se", fixture.Control.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safe_stop_disengages_cascade()
    {
        using var fixture = new ControlFixture();
        fixture.Dialogs.ConfirmResult = true;
        var oxygenRow = fixture.Control.Rows[2];

        oxygenRow.SelectedOxygenMode = "Agitação";
        oxygenRow.IsCascadeEngaged = true;
        Assert.True(fixture.Cascade.IsEngaged);

        fixture.Control.SafeStopCommand.Execute(null);

        Assert.False(fixture.Cascade.IsEngaged);
        Assert.False(oxygenRow.IsCascadeEngaged);
    }

    [Fact]
    public void Oxygen_mode_selector_tracks_a_mode_changed_from_the_other_workspace()
    {
        using var fixture = new ControlFixture();

        fixture.Cascade.SelectMode(CascadeMode.KlaPath);

        Assert.Equal("Mapa", fixture.Control.Rows[2].SelectedOxygenMode);
    }

    [Fact]
    public void Default_preset_is_loaded_on_initialization_when_no_presets_exist()
    {
        using var fixture = new ControlFixture(new AppSettings { SetpointPresets = [] });

        Assert.NotEmpty(fixture.Control.Presets);
        Assert.NotNull(fixture.Control.SelectedPreset);
        Assert.Equal(SetpointPreset.DefaultPreset.Name, fixture.Control.SelectedPreset.Name);
    }

    [Fact]
    public void Saving_and_loading_preset_preserves_cascade_pid_and_oxygen_mode()
    {
        using var fixture = new ControlFixture();

        var customCascade = new CascadeSettings
        {
            OxygenSetpointPercent = 45.0,
            CascadePid = new ModePidSettings
            {
                Kp = 0.88,
                Ki = 0.045,
                Kd = 0.12,
            }
        };
        fixture.Settings.Update(s => s with { Cascade = customCascade });
        fixture.Control.Rows[2].SelectedOxygenMode = "Aeração";

        fixture.Dialogs.PromptResponse = "Teste PID";
        fixture.Control.SavePresetCommand.Execute(null);

        var saved = Assert.Single(fixture.Control.Presets, p => p.Name == "Teste PID");
        Assert.Equal("Aeração", saved.OxygenMode);
        Assert.NotNull(saved.Cascade);
        Assert.Equal(0.88, saved.Cascade.CascadePid.Kp);

        // Modify current and load saved
        fixture.Control.Rows[2].SelectedOxygenMode = "Agitação";
        fixture.Settings.Update(s => s with { Cascade = new CascadeSettings { CascadePid = new ModePidSettings { Kp = 0.1 } } });

        fixture.Control.SelectedPreset = saved;
        fixture.Control.LoadPresetCommand.Execute(null);

        Assert.Equal("Aeração", fixture.Control.Rows[2].SelectedOxygenMode);
        Assert.Equal(0.88, fixture.Settings.Current.Cascade.CascadePid.Kp);
    }

    private sealed class ControlFixture : IDisposable
    {
        public ControlFixture(AppSettings? initialSettings = null)
        {
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService(initialSettings ?? new AppSettings());
            Dialogs = new RecordingDialogService();
            Flow = new FlowControlViewModel(Settings.Current.Setpoints.MaxFlowLitresPerMinute);

            Subsystems =
            [
                Create("temperature", "Temperatura", "°C", 1, 15, 60, false,
                    value => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value),
                    () => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 0.0),
                    Settings.Current.Setpoints.TemperatureCelsius),
                Create("motor", "Agitação", "rpm", 0, 50, 1000, true,
                    value => CommandBuilders.MotorSetpoint((int)value),
                    () => CommandBuilders.MotorSetpoint(0),
                    Settings.Current.Setpoints.MotorRpm),


                Create("oxygen", "Oxigênio", "%", 1, 0, 100, false,
                    value => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, value),
                    () => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, 0.0),
                    Settings.Current.Setpoints.OxygenPercent),
                Create("flow", "Vazão", "L/min", 2, 0, Flow.AppliedMaxFlow, false,
                    value => Flow.BuildSetpointUsingObservedValves(value),
                    () => Flow.BuildSafeStop(),
                    Settings.Current.Setpoints.FlowLitresPerMinute,
                    (_, enabled) => Flow.CommitFromFlowSetpoint(enabled)),
                Create("pressure", "Pressão", "kPa", 1, 1, 380, false,
                    value => TecnalCommand.Create().Set(CommandKeys.PressureReference, value),
                    () => TecnalCommand.Create().Set(CommandKeys.PressureReference, 0.0),
                    Settings.Current.Setpoints.PressureKilopascal),
            ];

            var cascadeArbiter = new CommandArbiter(Device, TimeProvider.System);
            Cascade = new CascadeService(cascadeArbiter, cascadeArbiter, Settings, new FakeKlaProfileStore(), TimeProvider.System);
            PH = new PHControlViewModel(Device, Settings);
            Nutrient = new NutrientControlViewModel(Device, Settings);
            Antifoam = new AntifoamControlViewModel(Device, Settings);
            Foam = new FoamControlViewModel(Device, Settings);
            Agitator = new FlaskAgitatorViewModel(Device, Settings);
            Biomass = new BiomassControlViewModel(Device, Settings);
            Pump = new PumpControlViewModel(Device, Settings);
            Control = new ControlViewModel(
                Subsystems, Flow, PH, Nutrient, Antifoam, Foam, Agitator, Biomass, Pump,
                Device, Settings, Dialogs, Cascade);
            Device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true });
        }

        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public RecordingDialogService Dialogs { get; }
        public CascadeService Cascade { get; }
        public FlowControlViewModel Flow { get; }
        public PHControlViewModel PH { get; }
        public NutrientControlViewModel Nutrient { get; }
        public AntifoamControlViewModel Antifoam { get; }
        public FoamControlViewModel Foam { get; }
        public FlaskAgitatorViewModel Agitator { get; }
        public BiomassControlViewModel Biomass { get; }
        public PumpControlViewModel Pump { get; }
        public IReadOnlyList<SubsystemViewModel> Subsystems { get; }
        public ControlViewModel Control { get; }

        private SubsystemViewModel Create(
            string id,
            string name,
            string unit,
            int decimals,
            double minimum,
            double maximum,
            bool integer,
            Func<double, TecnalCommand> apply,
            Func<TecnalCommand> disable,
            double initial,
            Action<double, bool>? committed = null)
            => new(
                new ProcessVariableViewModel(id, name, unit, decimals),
                new SubsystemSpec(minimum, maximum, integer, apply, disable, OnCommitted: committed),
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

    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }
        public int Calls { get; private set; }
        public string ExactCommand { get; private set; } = "";
        public bool PromptResult { get; set; } = true;
        public string PromptResponse { get; set; } = "";

        public bool ConfirmDestructive(string title, string consequence, string exactCommand)
        {
            Calls++;
            ExactCommand = exactCommand;
            return ConfirmResult;
        }

        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            Calls++;
            response = PromptResponse;
            return PromptResult;
        }
    }
}
