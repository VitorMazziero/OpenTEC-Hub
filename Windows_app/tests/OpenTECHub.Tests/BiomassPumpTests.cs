using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Phase 3 WP1 (biomass sensor) and WP2 (external pump): the ViewModels, the pure profile
/// mathematics, and the arbiter ownership the two new actuators gain.
/// </summary>
public sealed class BiomassPumpTests
{
    // ── Biomass ViewModel ────────────────────────────────────────────────────

    [Fact]
    public void Biomass_enable_toggle_sends_comm_immediately()
    {
        var device = new RecordingDeviceService();
        using var vm = new BiomassControlViewModel(device, new MemorySettingsService());

        Assert.Empty(device.Sent); // construction sends nothing

        vm.IsEnabled = true;
        Assert.Equal("""{"biomassComm":1}""", Assert.Single(device.Sent));

        vm.IsEnabled = false;
        Assert.Equal("""{"biomassComm":0}""", device.Sent[^1]);
    }

    [Fact]
    public void Biomass_momentary_actions_are_gated_on_the_sensor_being_enabled()
    {
        var device = new RecordingDeviceService();
        using var vm = new BiomassControlViewModel(device, new MemorySettingsService());

        Assert.False(vm.BlankCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));

        vm.IsEnabled = true;
        device.Sent.Clear();

        // Enabling the local routing request is not evidence that the node is present.
        Assert.False(vm.BlankCommand.CanExecute(null));
        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = true });

        Assert.True(vm.BlankCommand.CanExecute(null));
        vm.BlankCommand.Execute(null);
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);

        Assert.Equal(["""{"blank":1}""", """{"start":1}""", """{"stop":1}"""], device.Sent);
    }

    [Fact]
    public void Biomass_thresholds_apply_atomically_and_persist()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(device, settings)
        {
            LowThresholdText = "5000",
            HighThresholdText = "30000",
            OptimalThresholdText = "15000",
        };

        vm.ApplyThresholdsCommand.Execute(null);

        Assert.Equal("""{"low":5000,"high":30000,"opt":15000}""", Assert.Single(device.Sent));
        Assert.Equal(5000, settings.Current.BiomassControl.LowThreshold);
        Assert.Equal(30000, settings.Current.BiomassControl.HighThreshold);
        Assert.Equal(15000, settings.Current.BiomassControl.OptimalThreshold);
    }

    [Theory]
    [InlineData("40000", "10000", "20000")] // low >= high
    [InlineData("10000", "40000", "50000")] // optimal above high
    [InlineData("10000", "40000", "10000")] // optimal equals low
    [InlineData("10000", "40000", "40000")] // optimal equals high
    [InlineData("10000", "40000", "abc")]   // not an integer
    public void Biomass_thresholds_refuse_incoherent_values(string low, string high, string optimal)
    {
        var device = new RecordingDeviceService();
        using var vm = new BiomassControlViewModel(device, new MemorySettingsService())
        {
            LowThresholdText = low,
            HighThresholdText = high,
            OptimalThresholdText = optimal,
        };

        Assert.False(vm.IsValid);
        Assert.False(vm.ApplyThresholdsCommand.CanExecute(null));
    }

    [Fact]
    public void Biomass_readouts_stay_dashes_until_a_frame_carries_absorbance()
    {
        var device = new RecordingDeviceService();
        using var vm = new BiomassControlViewModel(device, new MemorySettingsService());

        // Absorbance at the not-received sentinel: the whole block is "no data yet".
        device.PushTelemetry(new SensorSnapshot { BiomassAbsorbance = SensorReadings.NotReceived, BiomassRaw = 0 });
        Assert.Equal("—", vm.AbsorbanceText);
        Assert.Equal("—", vm.RawText);

        device.PushTelemetry(new SensorSnapshot
        {
            BiomassAbsorbance = 0.421,
            BiomassRaw = 24500,
            BiomassIntegrationTimeMs = 120,
            BiomassPwmPercent = 55.5,
        });
        var c = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(0.421.ToString("F3", c), vm.AbsorbanceText);
        Assert.Equal(24500.ToString(c), vm.RawText);
        Assert.Equal(120.ToString(c), vm.IntegrationTimeText);
        Assert.Equal(55.5.ToString("F1", c), vm.PwmText);
    }

    // ── Pump ViewModel ───────────────────────────────────────────────────────

    /// <summary>
    /// Disabling is two ordered frames, not v.6's single one. The Hub parses
    /// <c>pumpComm</c> before it reaches the pump block, so a combined
    /// <c>{"pumpComm":0,"mode":0,"speed":0}</c> clears routing and then discards its own
    /// <c>mode:0</c> — the node keeps dosing and only its telemetry goes quiet.
    /// </summary>
    [Fact]
    public void Pump_disable_stops_the_profile_before_it_clears_routing()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService());

        vm.IsEnabled = true;
        Assert.Equal("""{"pumpComm":1}""", Assert.Single(device.Sent));

        device.Sent.Clear();
        vm.IsEnabled = false;

        Assert.Equal(
            ["""{"mode":0}""", """{"pumpComm":0}"""],
            device.Sent);
    }

    [Fact]
    public void Pump_apply_sends_the_active_mode_frame_and_bumps_the_version()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpControlViewModel(device, settings);

        vm.IsEnabled = true;
        device.Sent.Clear();
        vm.SelectedModeOption = vm.ModeOptions.Single(o => o.Mode == PumpProfileMode.Linear);
        vm.LambdaLinearText = "2";
        vm.PhiLinearText = "0.25";
        vm.InitMinutesText = "0";
        vm.FinalMinutesText = "30";

        Assert.True(vm.ApplyProfileCommand.CanExecute(null));
        vm.ApplyProfileCommand.Execute(null);

        Assert.Equal(
            """{"mode":2,"init_t":0.0,"final_t":30.0,"lambda_linear":2.0,"phi_linear":0.25}""",
            Assert.Single(device.Sent));
        Assert.Equal(2, settings.Current.PumpControl.Version); // was 1
        Assert.Equal(PumpProfileMode.Linear, settings.Current.PumpControl.Mode);
    }

    [Fact]
    public void Pump_mode_selection_flips_the_parameter_visibility_flags()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), new MemorySettingsService());

        vm.SelectedModeOption = vm.ModeOptions.Single(o => o.Mode == PumpProfileMode.Piecewise);
        Assert.True(vm.ShowPiecewise);
        Assert.False(vm.ShowConstant);

        vm.SelectedModeOption = vm.ModeOptions.Single(o => o.Mode == PumpProfileMode.Polynomial);
        Assert.True(vm.ShowPolynomial);
        Assert.False(vm.ShowPiecewise);
    }

    [Fact]
    public void Pump_invalid_profile_blocks_apply_and_clears_the_preview()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService()) { IsEnabled = true };
        device.Sent.Clear();

        vm.FinalMinutesText = "0"; // final must exceed init

        Assert.False(vm.IsValid);
        Assert.Null(vm.Preview);
        Assert.False(vm.ApplyProfileCommand.CanExecute(null));
        vm.ApplyProfileCommand.Execute(null);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Pump_piecewise_requires_t0_zero_and_increasing_times()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), new MemorySettingsService());
        vm.SelectedModeOption = vm.ModeOptions.Single(o => o.Mode == PumpProfileMode.Piecewise);

        vm.PiecewiseTimesText = "5, 10";  // t0 != 0
        vm.PiecewiseFlowsText = "1, 2";
        Assert.False(vm.IsValid);

        vm.PiecewiseTimesText = "0, 10, 5"; // not increasing
        vm.PiecewiseFlowsText = "1, 2, 3";
        Assert.False(vm.IsValid);

        vm.PiecewiseTimesText = "0, 10, 20";
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void Pump_safe_stop_contributes_only_the_profile_stop_to_the_merged_frame()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), new MemorySettingsService());

        // pumpComm:0 must not travel in the merged safe frame: the Hub would clear routing
        // while parsing it and then discard the mode:0 beside it.
        Assert.Equal("""{"mode":0}""", vm.BuildSafeStop().ToJson());
        Assert.Equal("""{"pumpComm":0}""", vm.BuildRoutingDisable().ToJson());
    }

    [Fact]
    public void Proportional_gas_drives_aeration_from_pump_volume_while_active()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService())
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };
        device.Sent.Clear();

        // Q_g = (V0 + PumpVol/1000)·vvm = (1.0 + 0)·0.5 = 0.5 L/min.
        device.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });
        Assert.Equal(
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            Assert.Single(device.Sent));

        // Grows with pump volume: (1.0 + 1000/1000)·0.5 = 1.0 L/min.
        device.PushTelemetry(new SensorSnapshot { PumpVolume = 1000.0, PumpFlow = 5.0 });
        Assert.Contains("""{"flowSetpoint":1.0,"maxFlow":50.0""", device.Sent[^1]);

        // Disabling stops the coupling: a further frame sends nothing.
        vm.GasProportionalEnabled = false;
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot { PumpVolume = 2000.0, PumpFlow = 5.0 });
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Proportional_gas_stays_silent_while_the_pump_is_disabled()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService())
        {
            GasProportionalEnabled = true, // but IsEnabled is false
        };
        device.Sent.Clear();

        device.PushTelemetry(new SensorSnapshot { PumpVolume = 500.0 });
        Assert.Empty(device.Sent);
    }

    // ── Pump profile mathematics ─────────────────────────────────────────────

    [Fact]
    public void FlowAt_is_zero_before_the_start_and_never_negative()
    {
        var spec = new PumpProfileSpec(PumpProfileMode.Linear, 10, 60, Lambda: 5, Phi: -1, [], [], []);
        Assert.Equal(0.0, PumpProfileMath.FlowAt(spec, 5));   // before init_t
        Assert.Equal(5.0, PumpProfileMath.FlowAt(spec, 10));  // t' = 0 → λ
        Assert.Equal(0.0, PumpProfileMath.FlowAt(spec, 30));  // 5 + (-1)·20 = -15 → clamped 0
    }

    [Fact]
    public void FlowAt_covers_every_mode()
    {
        Assert.Equal(3.0, PumpProfileMath.FlowAt(
            new PumpProfileSpec(PumpProfileMode.Constant, 0, 60, 3, 0, [], [], []), 25));

        // Polynomial 1 + 2t' at t'=4 → 9.
        Assert.Equal(9.0, PumpProfileMath.FlowAt(
            new PumpProfileSpec(PumpProfileMode.Polynomial, 0, 60, 0, 0, [1.0, 2.0], [], []), 4));

        // Piecewise linear through (0,0),(10,10) at t'=5 → 5.
        Assert.Equal(5.0, PumpProfileMath.FlowAt(
            new PumpProfileSpec(PumpProfileMode.Piecewise, 0, 60, 0, 0, [], [0.0, 10.0], [0.0, 10.0]), 5));
    }

    [Fact]
    public void Sample_integrates_a_constant_profile_to_rate_times_time()
    {
        var spec = new PumpProfileSpec(PumpProfileMode.Constant, 0, 60, Lambda: 2, Phi: 0, [], [], []);
        var preview = PumpProfileMath.Sample(spec, count: 200);

        Assert.Equal(2.0, preview.PeakFlowMlPerMin, precision: 6);
        // ∫ 2 mL/min dt over 60 min = 120 mL.
        Assert.Equal(120.0, preview.TotalVolumeMl, precision: 3);
    }

    [Fact]
    public void BuildCommand_dispatches_to_the_matching_wire_frame()
        => Assert.Equal(
            """{"mode":3,"init_t":0.0,"final_t":45.0,"lambda_exp":2.0,"phi_exp":0.1}""",
            PumpProfileMath.BuildCommand(
                new PumpProfileSpec(PumpProfileMode.Exponential, 0, 45, 2.0, 0.1, [], [], [])).ToJson());

    // ── Actuator ownership ───────────────────────────────────────────────────

    [Fact]
    public void The_two_new_actuators_are_tracked_and_labelled()
    {
        Assert.Contains(ActuatorId.Biomass, CommandActuators.All);
        Assert.Contains(ActuatorId.ExternalPump, CommandActuators.All);
        Assert.Equal("sensor de biomassa", CommandActuators.Label(ActuatorId.Biomass));
        Assert.Equal("bomba externa", CommandActuators.Label(ActuatorId.ExternalPump));
    }

    [Theory]
    [InlineData("biomassComm", ActuatorId.Biomass)]
    [InlineData("blank", ActuatorId.Biomass)]
    [InlineData("start", ActuatorId.Biomass)]
    [InlineData("low", ActuatorId.Biomass)]
    [InlineData("pumpComm", ActuatorId.ExternalPump)]
    [InlineData("mode", ActuatorId.ExternalPump)]
    [InlineData("init_t", ActuatorId.ExternalPump)]
    [InlineData("p0", ActuatorId.ExternalPump)]
    [InlineData("p20", ActuatorId.ExternalPump)]
    [InlineData("t5", ActuatorId.ExternalPump)]
    [InlineData("q12", ActuatorId.ExternalPump)]
    public void Keys_map_to_the_expected_actuator(string key, ActuatorId expected)
        => Assert.Equal(expected, CommandActuators.ForKey(key));

    [Theory]
    [InlineData("pressureReference")] // starts with 'p' but is not p<digits>
    [InlineData("pHCal")]
    [InlineData("tempSetpoint")]      // starts with 't' but is not t<digits>
    public void Unrelated_keys_are_not_mistaken_for_pump_coefficients(string key)
        => Assert.NotEqual(ActuatorId.ExternalPump, CommandActuators.ForKey(key));

    [Fact]
    public void A_whole_piecewise_frame_touches_only_the_external_pump()
    {
        var command = CommandBuilders.PumpPiecewise(0, 60, [0.0, 30.0, 60.0], [1.0, 2.0, 3.0]);
        var actuators = CommandActuators.ActuatorsIn(command);
        Assert.Equal(ActuatorId.ExternalPump, Assert.Single(actuators));
    }

    [Fact]
    public void The_arbiter_refuses_a_manual_pump_frame_the_recipe_owns()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);

        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.ExternalPump], "cascade-owned test");

        var refused = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.PumpEnable());
        Assert.False(refused.Accepted);
        Assert.Contains(ActuatorId.ExternalPump, refused.Refused);
        Assert.Empty(device.Sent);

        // Biomass is still Manual-owned, so a blank capture goes through.
        var accepted = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.BiomassBlank());
        Assert.True(accepted.Accepted);
        Assert.Equal("""{"blank":1}""", Assert.Single(device.Sent));
    }
}
