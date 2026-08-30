using System.Globalization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Phase 2 WP7 — the cultivation dosing auxiliaries: nutrient, antifoam, the level/foam
/// sensor and the separate flask agitator. Pins the wire, the arbiter ownership and the
/// dosing ViewModels. (Distinct from <see cref="Wp7Tests"/>, which is Phase 1b's page WP7.)
/// </summary>
public sealed class DosingAuxiliariesTests
{
    // ── Wire: the frozen command shapes ──────────────────────────────────────

    [Fact]
    public void Nutrient_frame_is_atomic_and_intensity_is_not_multiplied_by_ten()
        => Assert.Equal(
            """{"nutriOperation":2.0,"nutriMix":30.0,"nutriOpCycle":3.0,"nutriMixCycle":4.0,"nutriIntensity":80.0}""",
            CommandBuilders.NutrientControl(2, 30, 3, 4, 80.0).ToJson());

    [Fact]
    public void Nutrient_safe_stop_zeroes_intensity_and_keeps_timing_and_cycles()
        => Assert.Equal(
            """{"nutriOperation":2.0,"nutriMix":30.0,"nutriOpCycle":3.0,"nutriMixCycle":4.0,"nutriIntensity":0.0}""",
            CommandBuilders.NutrientControlSafeStop(2, 30, 3, 4).ToJson());

    [Fact]
    public void Antifoam_frame_carries_the_raw_percent_intensity()
        => Assert.Equal(
            """{"antifoamOperation":5.0,"antifoamMix":60.0,"antifoamIntensity":45.0}""",
            CommandBuilders.AntifoamControl(5, 60, 45.0).ToJson());

    [Fact]
    public void Antifoam_safe_stop_zeroes_operation_and_intensity_and_keeps_mix()
        => Assert.Equal(
            """{"antifoamOperation":0.0,"antifoamMix":60.0,"antifoamIntensity":0.0}""",
            CommandBuilders.AntifoamControlSafeStop(60).ToJson());

    [Fact]
    public void Foam_config_sends_the_sensor_enable_reference_and_three_timers()
        => Assert.Equal(
            """{"distanceSensorComm":1,"distanceSensorReference":100.0,"foamStartDelay_s":30.0,"foamPulse_s":2.0,"foamInterval_s":30.0}""",
            CommandBuilders.FoamControl(true, 100.0, 30, 2, 30).ToJson());

    [Fact]
    public void Clockwise_agitator_carries_magnitude_and_direction_one()
        => Assert.Equal(
            """{"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":75.0,"agitatorDir":1}""",
            CommandBuilders.FlaskAgitator(on: true, automatic: false, signedPercent: 75.0).ToJson());

    /// <summary>The signed operator value must never leak: -75 becomes magnitude 75, direction 0.</summary>
    [Fact]
    public void Counter_clockwise_agitator_splits_the_sign_off_the_magnitude()
    {
        var json = CommandBuilders.FlaskAgitator(on: true, automatic: false, signedPercent: -75.0).ToJson();

        Assert.Equal("""{"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":75.0,"agitatorDir":0}""", json);
        Assert.DoesNotContain("-75", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Agitator_ordinary_stop_leaves_the_potentiometer_preference_alone()
        => Assert.Equal(
            """{"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":50.0,"agitatorDir":0}""",
            CommandBuilders.FlaskAgitatorOff(-50.0).ToJson());

    /// <summary>
    /// The Hub turns an off command into <c>ActivePot = agitatorReEnablePot</c>, and the node
    /// re-reads the bench knob on its next loop whenever that is set. A safe stop that left the
    /// flag alone would be undone by a knob sitting at 60 %.
    /// </summary>
    [Fact]
    public void Agitator_safe_stop_locks_the_potentiometer_out()
        => Assert.Equal(
            """{"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":50.0,"agitatorDir":0,"agitatorReEnablePot":0}""",
            CommandBuilders.FlaskAgitatorSafeStop(-50.0).ToJson());

    [Fact]
    public void Re_enabling_the_potentiometer_is_a_single_key()
        => Assert.Equal("""{"agitatorReEnablePot":1}""", CommandBuilders.FlaskAgitatorReEnablePot().ToJson());

    // ── Arbiter ownership ────────────────────────────────────────────────────

    [Fact]
    public void The_three_dosing_pumps_are_owned_actuators_and_foam_config_is_not()
    {
        Assert.Equal(ActuatorId.Nutrient, CommandActuators.ForKey(CommandKeys.NutriIntensity));
        Assert.Equal(ActuatorId.Antifoam, CommandActuators.ForKey(CommandKeys.AntifoamOperation));
        Assert.Equal(ActuatorId.FlaskAgitator, CommandActuators.ForKey(CommandKeys.AgitatorOn));

        // The level/foam sensor keys stay unowned, like calibration.
        Assert.Null(CommandActuators.ForKey(CommandKeys.DistanceSensorComm));
        Assert.Null(CommandActuators.ForKey(CommandKeys.FoamPulseSeconds));

        Assert.Contains(ActuatorId.Nutrient, CommandActuators.All);
        Assert.Contains(ActuatorId.Antifoam, CommandActuators.All);
        Assert.Contains(ActuatorId.FlaskAgitator, CommandActuators.All);
    }

    [Fact]
    public void A_recipe_owning_nutrient_refuses_a_manual_nutrient_frame()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Nutrient], "teste");

        var result = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.NutrientControl(1, 60, 1, 1, 50));

        Assert.False(result.Accepted);
        Assert.Contains(ActuatorId.Nutrient, result.Refused);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Foam_config_passes_the_arbiter_even_while_antifoam_is_owned_elsewhere()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Antifoam], "teste");

        var result = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FoamControl(true, 100, 30, 2, 30));

        Assert.True(result.Accepted);
        Assert.Single(device.Sent);
    }

    // ── ViewModels ───────────────────────────────────────────────────────────

    [Fact]
    public void Nutrient_apply_sends_the_full_frame_and_persists_and_reports_duty_cycle()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new NutrientControlViewModel(device, settings)
        {
            IsEnabled = true,
            OperationSecondsText = "30",
            MixSecondsText = "10",
            OperationCyclesText = "2",
            MixCyclesText = "3",
            PumpSpeedPercentText = "60",
        };

        vm.ApplyCommand.Execute(null);

        Assert.Equal(
            """{"nutriOperation":30.0,"nutriMix":10.0,"nutriOpCycle":2.0,"nutriMixCycle":3.0,"nutriIntensity":60.0}""",
            Assert.Single(device.Sent));
        Assert.Equal(60.0, settings.Current.NutrientControl.PumpSpeedPercent);
        Assert.True(vm.AppliedIsEnabled);
        Assert.Equal(75.0, vm.AppliedDutyCyclePercent); // 30 / (30 + 10)
        Assert.Equal(60.0, vm.AppliedPumpSpeedPercent);
    }

    [Fact]
    public void Nutrient_disabled_apply_is_a_safe_stop_and_a_typo_never_blocks_it()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new NutrientControlViewModel(device, settings) { IsEnabled = true };

        vm.PumpSpeedPercentText = "abc";
        Assert.False(vm.CanApply);

        vm.IsEnabled = false;
        Assert.True(vm.CanApply);
        vm.ApplyCommand.Execute(null);

        Assert.EndsWith(""","nutriIntensity":0.0}""", Assert.Single(device.Sent), StringComparison.Ordinal);
    }

    [Fact]
    public void Antifoam_shows_live_telemetry_and_applies_the_raw_intensity()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new AntifoamControlViewModel(device, settings)
        {
            IsEnabled = true,
            OperationSecondsText = "5",
            MixSecondsText = "40",
            PumpSpeedPercentText = "30",
        };

        device.PushTelemetry(new SensorSnapshot { Antifoam = 18.0 });
        Assert.Contains("18", vm.LiveAntifoamText, StringComparison.Ordinal);

        vm.ApplyCommand.Execute(null);
        Assert.Equal(
            """{"antifoamOperation":5.0,"antifoamMix":40.0,"antifoamIntensity":30.0}""",
            Assert.Single(device.Sent));
        Assert.Equal(30.0, vm.AppliedPumpSpeedPercent);
    }

    [Fact]
    public void Foam_apply_sends_the_sensor_config_and_never_carries_a_pump_key()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new FoamControlViewModel(device, settings)
        {
            SensorEnabled = true,
            ReferenceMillimetresText = "120",
            StartDelaySecondsText = "15",
            PulseSecondsText = "3",
            IntervalSecondsText = "45",
        };

        vm.ApplyCommand.Execute(null);

        var json = Assert.Single(device.Sent);
        Assert.Equal(
            """{"distanceSensorComm":1,"distanceSensorReference":120.0,"foamStartDelay_s":15.0,"foamPulse_s":3.0,"foamInterval_s":45.0}""",
            json);
        Assert.DoesNotContain("antifoam", json, StringComparison.Ordinal);
        Assert.True(settings.Current.FoamControl.SensorEnabled);
        Assert.Equal(120.0, vm.AppliedReferenceMillimetres);
    }

    [Fact]
    public void Agitator_counter_clockwise_selection_reaches_the_wire_as_direction_zero()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new FlaskAgitatorViewModel(device, settings)
        {
            IsEnabled = true,
            MagnitudePercentText = "80",
            CounterClockwise = true,
        };

        vm.ApplyCommand.Execute(null);

        Assert.Equal(
            """{"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":0}""",
            Assert.Single(device.Sent));
        Assert.False(vm.Clockwise);
        Assert.Equal(80.0, vm.AppliedMagnitudePercent);
    }

    [Fact]
    public void Agitator_re_enable_pot_is_a_momentary_action()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new FlaskAgitatorViewModel(device, settings);

        vm.ReEnablePotCommand.Execute(null);

        Assert.Equal("""{"agitatorReEnablePot":1}""", Assert.Single(device.Sent));
    }

    [Fact]
    public void Agitator_slider_and_entry_stay_in_step()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new FlaskAgitatorViewModel(device, settings);

        vm.MagnitudePercent = 42.0;
        Assert.Equal("42", vm.MagnitudePercentText);

        vm.MagnitudePercentText = "17";
        Assert.Equal(17.0, vm.MagnitudePercent);
    }

    [Fact]
    public void Biomass_communication_is_immediate_and_thresholds_remain_a_separate_action()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(device, settings)
        {
            IsEnabled = true,
            LowThresholdText = "12000",
            HighThresholdText = "36000",
            OptimalThresholdText = "24000",
        };

        Assert.Equal("""{"biomassComm":1}""", Assert.Single(device.Sent));
        Assert.True(vm.IsEnabled);

        vm.ApplyThresholdsCommand.Execute(null);
        Assert.Equal(2, device.Sent.Count);
        Assert.Equal(24000, settings.Current.BiomassControl.OptimalThreshold);
    }

    /// <summary>The wire must never see a pt-BR comma: the builder formats invariantly.</summary>
    [Fact]
    public void Comma_entry_for_a_dosing_reference_reaches_the_wire_as_a_point()
    {
        var parsed = double.Parse("120,5".Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

        Assert.Equal(
            """{"distanceSensorComm":0,"distanceSensorReference":120.5,"foamStartDelay_s":30.0,"foamPulse_s":2.0,"foamInterval_s":30.0}""",
            CommandBuilders.FoamControl(false, parsed, 30, 2, 30).ToJson());
    }
}
