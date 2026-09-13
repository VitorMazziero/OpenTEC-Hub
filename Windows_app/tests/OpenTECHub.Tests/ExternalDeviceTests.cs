using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>An <see cref="IManualDispatcher"/> whose verdict the test chooses.</summary>
internal sealed class StubDispatcher : IManualDispatcher
{
    public List<string> Sent { get; } = [];

    /// <summary>Frames sent through the ordered-frame path, in order.</summary>
    public List<string> SeparateFrames { get; } = [];

    /// <summary>When set, every dispatch is refused with these actuators.</summary>
    public IReadOnlyList<ActuatorId>? RefuseWith { get; set; }

    public CommandDispatchResult Dispatch(OpenTECCommand command)
    {
        if (RefuseWith is { Count: > 0 } refused)
        {
            return new CommandDispatchResult(false, refused, CommandOwner.Manual);
        }

        Sent.Add(command.ToJson());
        return new CommandDispatchResult(true, [], CommandOwner.Manual);
    }

    public CommandDispatchResult DispatchSeparateFrame(OpenTECCommand command)
    {
        if (RefuseWith is { Count: > 0 } refused)
        {
            return new CommandDispatchResult(false, refused, CommandOwner.Manual);
        }

        var json = command.ToJson();
        Sent.Add(json);
        SeparateFrames.Add(json);
        return new CommandDispatchResult(true, [], CommandOwner.Manual);
    }
}

internal sealed class StubDialogService : IDialogService
{
    public bool ConfirmResult { get; set; }
    public int ConfirmDestructiveCalls { get; private set; }
    public string? ConsequencePassed { get; private set; }
    public string? ExactCommandPassed { get; private set; }

    public bool ConfirmDestructive(string title, string consequence, string exactCommand)
    {
        ConfirmDestructiveCalls++;
        ConsequencePassed = consequence;
        ExactCommandPassed = exactCommand;
        return ConfirmResult;
    }

    public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false)
        => ConfirmResult;

    public bool PromptInput(string title, string message, out string response, string initialValue = "")
    {
        response = initialValue;
        return true;
    }

    public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.Cancel;
}

/// <summary>
/// The external-device contract: presence separated from routing separated from the
/// operator's own switch, commands that report whether they were accepted, and the two
/// firmware orderings the Hub imposes.
/// </summary>
/// <remarks>See <c>docs/PLANO_DISPOSITIVOS_EXTERNOS.md</c>.</remarks>
public sealed class ExternalDeviceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    // ── ExternalDeviceStatus ─────────────────────────────────────────────────

    /// <summary>
    /// Nothing reported is not the same as reported absent, and must never be shown as one.
    /// Blocking commands here would deadlock a node that only reports while it is working.
    /// </summary>
    [Fact]
    public void Silence_from_the_hub_is_awaiting_telemetry_not_offline()
    {
        var status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa");

        Assert.False(status.HasTelemetry);
        Assert.False(status.IsOffline);
        Assert.True(status.CanSend);
        Assert.Equal("Aguardando telemetria do Hub", status.PresenceText);
    }

    [Fact]
    public void A_reported_absence_blocks_commands_and_names_the_device()
    {
        var status = new ExternalDeviceStatus("Bomba externa", "da bomba externa");

        status.Update(hasTelemetry: true, online: false, pending: null, commEnabled: true);

        Assert.True(status.IsOffline);
        Assert.False(status.CanSend);
        Assert.False(status.ShowPendingChip);
        Assert.Equal("Bomba externa desconectado da Central", status.StatusText);
    }

    /// <summary>The Hub tracks the flowmeter-style mailbox; its answer outranks any local guess.</summary>
    [Fact]
    public void A_hub_that_tracks_the_mailbox_clears_the_pending_lock_itself()
    {
        var clock = new TestClock(Origin);
        var status = new ExternalDeviceStatus("Bomba externa", "da bomba externa", clock);

        status.MarkCommandDispatched();
        status.Update(hasTelemetry: true, online: true, pending: true, commEnabled: true);
        Assert.True(status.IsAwaitingAck);
        Assert.False(status.CanSend);

        status.Update(hasTelemetry: true, online: true, pending: false, commEnabled: true);
        Assert.False(status.IsAwaitingAck);
        Assert.True(status.CanSend);
    }

    /// <summary>
    /// Without an acknowledgement channel the lock has to time out, or the row stays stuck.
    /// A null pending flag means "the Hub said nothing", which is not a confirmation.
    /// </summary>
    [Fact]
    public void Without_an_ack_channel_the_pending_lock_expires_on_the_fallback_window()
    {
        var clock = new TestClock(Origin);
        var status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa", clock);

        status.MarkCommandDispatched();
        status.Update(hasTelemetry: true, online: true, pending: null, commEnabled: null);
        Assert.True(status.IsAwaitingAck);

        clock.Advance(ExternalDeviceStatus.AckFallbackWindow - TimeSpan.FromSeconds(1));
        status.Update(hasTelemetry: true, online: true, pending: null, commEnabled: null);
        Assert.True(status.IsAwaitingAck);

        clock.Advance(TimeSpan.FromSeconds(1));
        status.Update(hasTelemetry: true, online: true, pending: null, commEnabled: null);
        Assert.False(status.IsAwaitingAck);
    }

    [Fact]
    public void A_refused_command_releases_the_row_at_once()
    {
        var status = new ExternalDeviceStatus("Bomba externa", "da bomba externa");

        status.MarkCommandDispatched();
        Assert.False(status.CanSend);

        status.MarkCommandRefused();
        Assert.True(status.CanSend);
    }

    /// <summary>
    /// The Hub persists its routing flags in NVS and the app persists the switches on the PC.
    /// After a Hub reboot they can differ, and every sub-command for an unrouted device is
    /// dropped in silence — this is what makes that visible.
    /// </summary>
    [Fact]
    public void A_hub_that_is_not_routing_disagrees_visibly_with_the_operator_switch()
    {
        var status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa")
        {
            IsCommRequested = true,
        };

        status.Update(hasTelemetry: true, online: true, pending: null, commEnabled: false);

        Assert.True(status.HasCommMismatch);
        Assert.Contains("desabilitado", status.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hub_that_does_not_publish_routing_never_claims_a_mismatch()
    {
        var status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa")
        {
            IsCommRequested = true,
        };

        status.Update(hasTelemetry: true, online: true, pending: null, commEnabled: null);

        Assert.False(status.HasCommMismatch);
    }

    /// <summary>
    /// With the Hub unreachable, nothing is known about the node behind it. Saying "offline"
    /// would be a claim the app cannot support.
    /// </summary>
    [Fact]
    public void Losing_the_hub_drops_back_to_no_evidence_rather_than_declaring_the_node_absent()
    {
        var status = new ExternalDeviceStatus("Bomba externa", "da bomba externa");
        status.Update(hasTelemetry: true, online: false, pending: null, commEnabled: true);
        Assert.True(status.IsOffline);

        status.MarkHubUnavailable();

        Assert.False(status.HasTelemetry);
        Assert.False(status.IsOffline);
        Assert.Null(status.CommEnabledOnHub);
    }

    // ── TelemetryParser: presence and invalidation ───────────────────────────

    /// <summary>
    /// The Hub stops publishing the biomass channels once its window lapses. Holding the last
    /// sample would leave a ten-minute-old absorbance on screen looking live.
    /// </summary>
    [Fact]
    public void Biomass_readings_are_invalidated_when_the_hub_reports_the_node_absent()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"BiomassOnline":true,"BiomassAbs":0.482,"BiomassRaw":31000,"BiomassIT":100,"BiomassPWM":42.0}""");
        Assert.Equal(0.482, parser.Readings.BiomassAbsorbance, 3);
        Assert.True(parser.Readings.BiomassOnline);

        parser.Parse("""{"BiomassOnline":false}""");

        Assert.True(parser.Readings.HasBiomassTelemetry);
        Assert.False(parser.Readings.BiomassOnline);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.BiomassAbsorbance);
        Assert.Equal(0, parser.Readings.BiomassRaw);
    }

    /// <summary>
    /// A Hub without the presence flags omits them entirely, and the app still has to tell a
    /// silent node from a live one. The value keys are the only evidence left, so they age.
    /// </summary>
    [Fact]
    public void Biomass_presence_falls_back_to_ageing_against_a_hub_without_the_flag()
    {
        var clock = new TestClock(Origin);
        var parser = new TelemetryParser(
            new ParserConfig { BiomassTimeout = TimeSpan.FromSeconds(12) }, clock);

        parser.Parse("""{"BiomassAbs":0.5,"BiomassRaw":30000}""");
        Assert.True(parser.Readings.HasBiomassTelemetry);
        Assert.True(parser.Readings.BiomassOnline);

        clock.Advance(TimeSpan.FromSeconds(6));
        parser.Parse("""{"Time":10.0}""");
        Assert.True(parser.Readings.BiomassOnline);
        Assert.Equal(0.5, parser.Readings.BiomassAbsorbance, 3);

        clock.Advance(TimeSpan.FromSeconds(6));
        parser.Parse("""{"Time":16.0}""");
        Assert.False(parser.Readings.BiomassOnline);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.BiomassAbsorbance);
    }

    /// <summary>
    /// The Hub has no staleness window for the pump at all, so it republishes a dead node's
    /// last sample forever. Invalidating here is what stops the card — and the proportional-gas
    /// coupling that reads PumpVolume — from acting on those numbers.
    /// </summary>
    [Fact]
    public void Pump_readings_are_invalidated_when_the_hub_reports_the_node_absent()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"PumpOnline":true,"PumpFlow":1.25,"PumpVol":840.0,"PumpMode":2,"PumpActive":true}""");
        Assert.Equal(840.0, parser.Readings.PumpVolume, 3);
        Assert.True(parser.Readings.PumpActive);

        parser.Parse("""{"PumpOnline":false}""");

        Assert.Equal(SensorReadings.NotReceived, parser.Readings.PumpVolume);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.PumpFlow);
        Assert.Equal(-1, parser.Readings.PumpMode);
        Assert.False(parser.Readings.PumpActive);
    }

    /// <summary>Six pump channels were already on the wire and thrown away.</summary>
    [Fact]
    public void The_pump_channels_the_hub_already_published_are_parsed()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"PumpMode":3,"PumpPWM":180,"PumpSpeed":42.5,"PumpFlow":1.25,"PumpVol":840.0,"PumpTargetVol":900.0,"PumpActive":true,"PumpWaiting":false}""");

        var snapshot = parser.Readings.Snapshot();
        Assert.Equal(3, snapshot.PumpMode);
        Assert.Equal(180.0, snapshot.PumpPwm, 3);
        Assert.Equal(42.5, snapshot.PumpSpeed, 3);
        Assert.Equal(900.0, snapshot.PumpTargetVolume, 3);
        Assert.True(snapshot.PumpActive);
        Assert.False(snapshot.PumpWaiting);
    }

    /// <summary>
    /// The agitator is the one device an unflashed Hub cannot be worked around for: it pushes
    /// nothing, so there are no value keys to age. That must read as unknown, not as failure.
    /// </summary>
    [Fact]
    public void The_agitator_stays_unknown_against_a_hub_that_never_mentions_it()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"Time":10.0,"Tempval":30.0}""");

        Assert.False(parser.Readings.HasAgitatorTelemetry);
        Assert.False(parser.Readings.AgitatorOnline);
        Assert.Null(parser.Readings.AgitatorCommandPending);
    }

    [Fact]
    public void The_agitator_reports_what_is_actually_driving_the_motor_once_the_hub_publishes_it()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"AgitatorOnline":true,"AgitatorPercent":62.0,"AgitatorDir":0,"AgitatorPotActive":true,"AgitatorSource":"Pot"}""");

        var snapshot = parser.Readings.Snapshot();
        Assert.True(snapshot.AgitatorOnline);
        Assert.Equal(62.0, snapshot.AgitatorPercent, 3);
        Assert.Equal(0, snapshot.AgitatorDirection);
        Assert.True(snapshot.AgitatorPotActive);
        Assert.Equal("Pot", snapshot.AgitatorSource);
    }

    /// <summary>
    /// A missing pending key means the Hub has no acknowledgement channel, which is not the
    /// same as "nothing pending" — reading it as false would clear a lock nothing confirmed.
    /// </summary>
    [Fact]
    public void A_missing_pending_key_is_unknown_rather_than_confirmed()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"BiomassOnline":true,"BiomassAbs":0.4}""");
        Assert.Null(parser.Readings.BiomassCommandPending);

        parser.Parse("""{"BiomassOnline":true,"BiomassAbs":0.4,"BiomassCommandPending":true}""");
        Assert.True(parser.Readings.BiomassCommandPending);
    }

    // ── Biomass view-model ───────────────────────────────────────────────────

    [Fact]
    public void Device_specific_editors_wait_for_confirmed_online_presence()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var foam = new FoamControlViewModel(device, settings);
        using var biomass = new BiomassControlViewModel(device, settings);
        using var agitator = new FlaskAgitatorViewModel(device, settings);

        Assert.False(foam.ApplyCommand.CanExecute(null));
        Assert.False(biomass.ApplyThresholdsCommand.CanExecute(null));
        Assert.False(agitator.ApplyCommand.CanExecute(null));
        Assert.False(agitator.ReEnablePotCommand.CanExecute(null));

        device.PushTelemetry(new SensorSnapshot
        {
            HasDistanceTelemetry = true,
            DistanceOnline = true,
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            HasAgitatorTelemetry = true,
            AgitatorOnline = true,
        });

        Assert.True(foam.ApplyCommand.CanExecute(null));
        Assert.True(biomass.ApplyThresholdsCommand.CanExecute(null));
        Assert.True(agitator.ApplyCommand.CanExecute(null));
        Assert.True(agitator.ReEnablePotCommand.CanExecute(null));
    }

    [Fact]
    public void Pump_and_biomass_start_without_a_false_restored_parameters_message()
    {
        using var pump = new PumpControlViewModel(
            new RecordingDeviceService(), new MemorySettingsService());
        using var biomass = new BiomassControlViewModel(
            new RecordingDeviceService(), new MemorySettingsService());

        Assert.Equal("", pump.StatusText);
        Assert.Equal("", biomass.StatusText);
    }

    /// <summary>
    /// The Hub parses <c>biomassComm</c> before it reaches the biomass block, so a single
    /// <c>{"stop":1,"biomassComm":0}</c> clears routing and then discards its own stop — the
    /// node keeps acquiring behind a switch that says otherwise.
    /// </summary>
    [Fact]
    public void Biomass_disable_stops_acquisition_on_the_frame_before_it_clears_routing()
    {
        var dispatcher = new StubDispatcher();
        using var vm = new BiomassControlViewModel(
            new RecordingDeviceService(), new MemorySettingsService(), dispatcher)
        {
            IsEnabled = true,
        };
        dispatcher.Sent.Clear();

        vm.IsEnabled = false;

        Assert.Equal(["""{"stop":1}""", """{"biomassComm":0}"""], dispatcher.Sent);
        Assert.Equal(["""{"biomassComm":0}"""], dispatcher.SeparateFrames);
    }

    /// <summary>
    /// The Hub keeps one pending biomass command and the node polls it every 2 s, so a blank
    /// issued just before a start is silently replaced. The pending lock makes that mailbox
    /// depth of one visible instead of losing the operator's first click.
    /// </summary>
    [Fact]
    public void Biomass_momentary_actions_are_serialised_behind_the_pending_lock()
    {
        var dispatcher = new StubDispatcher();
        var device = new RecordingDeviceService();
        var clock = new TestClock(Origin);
        using var vm = new BiomassControlViewModel(
            device, new MemorySettingsService(), dispatcher, clock)
        {
            IsEnabled = true,
        };
        dispatcher.Sent.Clear();

        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = true });
        Assert.True(vm.BlankCommand.CanExecute(null));
        vm.BlankCommand.Execute(null);

        Assert.Equal("""{"blank":1}""", Assert.Single(dispatcher.Sent));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.ApplyThresholdsCommand.CanExecute(null));

        // The Hub confirms nothing for biomass, so the fallback window is what releases it.
        clock.Advance(ExternalDeviceStatus.AckFallbackWindow);
        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = true });

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void A_reported_absence_blocks_the_biomass_momentary_actions()
    {
        var device = new RecordingDeviceService();
        using var vm = new BiomassControlViewModel(
            device, new MemorySettingsService(), new StubDispatcher())
        {
            IsEnabled = true,
        };

        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = false });

        Assert.False(vm.BlankCommand.CanExecute(null));
        Assert.False(vm.ApplyThresholdsCommand.CanExecute(null));
    }

    /// <summary>
    /// Persisting a calibration the sensor never received would leave the app and the hardware
    /// disagreeing with no record of it (AUD-003).
    /// </summary>
    [Fact]
    public void Refused_biomass_thresholds_stay_staged_and_are_not_persisted()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(new RecordingDeviceService(), settings, dispatcher)
        {
            IsEnabled = true,
            LowThresholdText = "12000",
            HighThresholdText = "44000",
            OptimalThresholdText = "26000",
        };

        dispatcher.RefuseWith = [ActuatorId.Biomass];
        vm.ApplyThresholdsCommand.Execute(null);

        Assert.True(vm.HasPendingChange);
        Assert.Equal(10000, settings.Current.BiomassControl.LowThreshold);
        Assert.Contains("recusado", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refused_biomass_enable_puts_the_switch_back()
    {
        var dispatcher = new StubDispatcher { RefuseWith = [ActuatorId.Biomass] };
        using var vm = new BiomassControlViewModel(
            new RecordingDeviceService(), new MemorySettingsService(), dispatcher);

        vm.IsEnabled = true;

        Assert.False(vm.IsEnabled);
        Assert.Contains("recusado", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Pump view-model ──────────────────────────────────────────────────────

    /// <summary>
    /// Recording the target as sent after a refusal suppresses every retry until the calculated
    /// flow moves by the resend threshold, so the coupling stays dead long after ownership comes
    /// back (AUD-004).
    /// </summary>
    [Fact]
    public void Proportional_gas_retries_the_same_target_after_a_refusal()
    {
        var dispatcher = new StubDispatcher();
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(
            device, new MemorySettingsService(), dispatcher)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        dispatcher.Sent.Clear();

        dispatcher.RefuseWith = [ActuatorId.Aeration];
        device.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });
        Assert.Empty(dispatcher.Sent);

        // Ownership returns and the identical target must go out, not be suppressed as sent.
        dispatcher.RefuseWith = null;
        device.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });

        Assert.Equal(
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            Assert.Single(dispatcher.Sent));
    }

    [Fact]
    public void Proportional_gas_automatically_retries_and_delivers_when_aeration_ownership_returns_to_manual()
    {
        var targetDevice = new RecordingDeviceService();
        var arbiter = new CommandArbiter(targetDevice, TimeProvider.System);
        var dispatcher = new ManualDispatcher(arbiter);
        using var vm = new PumpControlViewModel(
            targetDevice, new MemorySettingsService(), dispatcher, arbiter: arbiter)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        targetDevice.Sent.Clear();

        // 1. Cascade claims Aeration.
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "Cascade engaged");

        // 2. Telemetry triggers MaybeSendProportionalGas; arbiter refuses because Aeration is owned by Automatic.
        targetDevice.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });
        Assert.Empty(targetDevice.Sent);
        Assert.Contains("recusado", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        // 3. Cascade disengages, releasing Aeration back to Manual WITHOUT ANY NEW TELEMETRY.
        arbiter.Release(CommandOwner.Automatic, "Cascade disengaged");

        // 4. The pump automatically retried upon receiving OwnershipChanged, delivering the target!
        Assert.Equal(
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            Assert.Single(targetDevice.Sent));
        Assert.Contains("restabelecida", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proportional_gas_resends_unchanged_target_when_ownership_is_released()
    {
        var targetDevice = new RecordingDeviceService();
        var arbiter = new CommandArbiter(targetDevice, TimeProvider.System);
        var dispatcher = new ManualDispatcher(arbiter);
        using var vm = new PumpControlViewModel(
            targetDevice, new MemorySettingsService(), dispatcher, arbiter: arbiter)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        targetDevice.Sent.Clear();

        // Telemetry sends initial target (0.5 LPM) and is accepted.
        targetDevice.PushTelemetry(new SensorSnapshot { PumpVolume = 0.0, PumpFlow = 0.0 });
        Assert.Single(targetDevice.Sent);
        targetDevice.Sent.Clear();

        // Recipe claims Aeration.
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Aeration], "Recipe running");

        // Recipe releases Aeration back to Manual without target having changed.
        arbiter.Release(CommandOwner.Recipe, "Recipe finished");

        // The unchanged target is re-sent to ensure hardware has the correct flow setpoint.
        Assert.Equal(
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            Assert.Single(targetDevice.Sent));
    }

    [Fact]
    public void Proportional_gas_updates_and_retries_latest_target_after_volume_changes_during_lock()
    {
        var targetDevice = new RecordingDeviceService();
        var arbiter = new CommandArbiter(targetDevice, TimeProvider.System);
        var dispatcher = new ManualDispatcher(arbiter);
        using var vm = new PumpControlViewModel(
            targetDevice, new MemorySettingsService(), dispatcher, arbiter: arbiter)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        targetDevice.Sent.Clear();

        // Cascade owns aeration.
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "Cascade engaged");

        // Pump doses 1000 mL during cascade. Qg moves from 0.5 to (1.0 + 1.0) * 0.5 = 1.0 LPM.
        targetDevice.PushTelemetry(new SensorSnapshot { PumpVolume = 1000.0, PumpFlow = 5.0 });
        Assert.Empty(targetDevice.Sent);

        // Cascade releases aeration.
        arbiter.Release(CommandOwner.Automatic, "Cascade disengaged");

        // Pump automatically sends the updated 1.0 LPM flow setpoint.
        Assert.Equal(
            """{"flowSetpoint":1.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            Assert.Single(targetDevice.Sent));
    }

    [Fact]
    public void Proportional_gas_cannot_be_enabled_when_cascade_is_engaged()
    {
        var targetDevice = new RecordingDeviceService();
        var arbiter = new CommandArbiter(targetDevice, TimeProvider.System);
        var dispatcher = new ManualDispatcher(arbiter);
        var settings = new MemorySettingsService();
        var cascade = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), TimeProvider.System);

        using var vm = new PumpControlViewModel(
            targetDevice, settings, dispatcher, arbiter: arbiter, cascade: cascade)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
        };

        // Engage cascade
        cascade.Engage(400, 2.0);
        Assert.True(cascade.IsEngaged);

        // Try to enable proportional gas
        vm.GasProportionalEnabled = true;

        Assert.False(vm.GasProportionalEnabled);
        Assert.Contains("indisponível", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proportional_gas_and_cascade_are_strictly_mutually_exclusive()
    {
        var targetDevice = new RecordingDeviceService();
        var arbiter = new CommandArbiter(targetDevice, TimeProvider.System);
        var dispatcher = new ManualDispatcher(arbiter);
        var settings = new MemorySettingsService();
        var cascade = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), TimeProvider.System);

        using var vm = new PumpControlViewModel(
            targetDevice, settings, dispatcher, arbiter: arbiter, cascade: cascade)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        Assert.True(vm.IsGasProportionalActive);

        // 1. Cascade cannot engage because proportional gas is active
        var canEngage = cascade.CanEngage(out var reason);
        Assert.False(canEngage);
        Assert.Contains("gás proporcional", reason, StringComparison.OrdinalIgnoreCase);

        // 2. Disabling proportional gas allows cascade to engage
        vm.GasProportionalEnabled = false;
        Assert.False(vm.IsGasProportionalActive);
        Assert.True(cascade.CanEngage(out _));

        // 3. Engaging cascade locks out proportional gas
        cascade.Engage(400, 2.0);
        Assert.True(cascade.IsEngaged);

        vm.GasProportionalEnabled = true;
        Assert.False(vm.GasProportionalEnabled);
        Assert.Contains("indisponível", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        // 4. Disengaging cascade frees proportional gas to be enabled again
        cascade.Disengage("Teste encerrado");
        Assert.False(cascade.IsEngaged);

        vm.GasProportionalEnabled = true;
        Assert.True(vm.GasProportionalEnabled);
    }

    /// <summary>
    /// A dead node's last volume is not a measurement. Recomputing from the zero left behind
    /// would silently drop aeration to its base rate.
    /// </summary>
    [Fact]
    public void An_absent_pump_suspends_the_proportional_gas_coupling()
    {
        var dispatcher = new StubDispatcher();
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(
            device, new MemorySettingsService(), dispatcher)
        {
            IsEnabled = true,
            InitialVolumeText = "1.0",
            VvmText = "0.5",
            GasProportionalEnabled = true,
        };

        dispatcher.Sent.Clear();

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = false,
            PumpVolume = SensorReadings.NotReceived,
        });

        Assert.Empty(dispatcher.Sent);
    }

    [Fact]
    public void The_pump_card_reports_the_node_state_the_hub_already_published()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(
            device, new MemorySettingsService(), new StubDispatcher());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpMode = 2,
            PumpTargetVolume = 900.0,
            PumpActive = false,
            PumpWaiting = true,
        });

        Assert.Equal("Linear", vm.PumpModeText);
        Assert.Equal("Aguardando janela", vm.PumpRunStateText);
        Assert.Equal("900.000", vm.PumpTargetVolumeText.Replace(',', '.'));
    }

    [Fact]
    public void A_refused_pump_profile_does_not_bump_the_persisted_version()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), settings, dispatcher)
        {
            IsEnabled = true,
            LambdaConstText = "2.5",
        };
        var before = settings.Current.PumpControl.Version;

        dispatcher.RefuseWith = [ActuatorId.ExternalPump];
        vm.ApplyProfileCommand.Execute(null);

        Assert.Equal(before, settings.Current.PumpControl.Version);
        Assert.True(vm.HasPendingChange);
    }

    // ── Flask agitator view-model ────────────────────────────────────────────

    /// <summary>
    /// The node re-reads the bench knob on its next loop whenever ActivePot is set, so the
    /// operator needs to know before pressing an ordinary stop.
    /// </summary>
    [Fact]
    public void The_agitator_card_warns_when_the_bench_potentiometer_holds_the_motor()
    {
        var device = new RecordingDeviceService();
        using var vm = new FlaskAgitatorViewModel(
            device, new MemorySettingsService(), new StubDispatcher());

        Assert.Null(vm.PotentiometerWarning);

        device.PushTelemetry(new SensorSnapshot
        {
            HasAgitatorTelemetry = true,
            AgitatorOnline = true,
            AgitatorPercent = 60.0,
            AgitatorDirection = 1,
            AgitatorPotActive = true,
            AgitatorSource = "Pot",
        });

        Assert.True(vm.IsPotentiometerInControl);
        Assert.NotNull(vm.PotentiometerWarning);
        Assert.Equal("60", vm.ActualPercentText);
        Assert.Equal("Horário", vm.ActualDirectionText);
        Assert.Equal("Potenciômetro", vm.ActualSourceText);
    }

    [Fact]
    public void A_refused_agitator_apply_does_not_commit_the_staged_state()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        using var vm = new FlaskAgitatorViewModel(new RecordingDeviceService(), settings, dispatcher)
        {
            IsEnabled = true,
            MagnitudePercentText = "70",
        };

        dispatcher.RefuseWith = [ActuatorId.FlaskAgitator];
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.AppliedIsEnabled);
        Assert.True(vm.HasPendingChange);
        Assert.Contains("recusado", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Ordered frames on the real buffer ────────────────────────────────────

    /// <summary>
    /// The outgoing buffer merges by design — several setpoints in one gesture leave as one
    /// frame. That is wrong for a pair the firmware's parse order makes interact, and the
    /// ordered-frame path is what keeps them apart.
    /// </summary>
    [Fact]
    public void An_ordered_frame_is_not_merged_into_the_buffered_one()
    {
        IDeviceService device = new RecordingDeviceService();

        device.Send(CommandBuilders.PumpStopProfile());
        device.SendAfterCurrentFrame(CommandBuilders.PumpRoutingDisabled());

        Assert.Equal(["""{"mode":0}""", """{"pumpComm":0}"""], ((RecordingDeviceService)device).Sent);
    }

    // ── Distance node configuration ───────────────────────────────────────────

    [Fact]
    public void Distance_node_config_validates_range_and_updates_status()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var foam = new FoamControlViewModel(device, settings);

        // Initial staged values come from default settings
        Assert.True(foam.OffsetMmText is "20" or "20.0" or "20,0");
        Assert.Equal("1000", foam.SamplePeriodMsText);
        Assert.Equal("1000", foam.SendPeriodMsText);
        Assert.True(foam.IsValidNodeConfig);
        Assert.Null(foam.NodeConfigValidationError);

        // Out of range offset ([-50, 200])
        foam.OffsetMmText = "250";
        Assert.False(foam.IsValidNodeConfig);
        Assert.NotNull(foam.NodeConfigValidationError);
        Assert.False(foam.CanSendNodeConfig);

        foam.OffsetMmText = "-60";
        Assert.False(foam.IsValidNodeConfig);

        foam.OffsetMmText = "20";
        Assert.True(foam.IsValidNodeConfig);

        // Out of range sample period ([100, 60000])
        foam.SamplePeriodMsText = "50";
        Assert.False(foam.IsValidNodeConfig);
        Assert.False(foam.CanSendNodeConfig);

        foam.SamplePeriodMsText = "70000";
        Assert.False(foam.IsValidNodeConfig);

        foam.SamplePeriodMsText = "500";
        Assert.True(foam.IsValidNodeConfig);

        // Out of range send period ([100, 60000])
        foam.SendPeriodMsText = "50";
        Assert.False(foam.IsValidNodeConfig);

        foam.SendPeriodMsText = "70000";
        Assert.False(foam.IsValidNodeConfig);

        foam.SendPeriodMsText = "2000";
        Assert.True(foam.IsValidNodeConfig);
    }

    [Fact]
    public void Distance_node_config_dispatches_command_and_persists_settings()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var dispatcher = new StubDispatcher();
        using var foam = new FoamControlViewModel(device, settings, dispatcher);

        device.PushTelemetry(new SensorSnapshot
        {
            HasDistanceTelemetry = true,
            DistanceOnline = true,
            DistanceCommEnabled = true,
            DistanceOffsetMm = 20.0,
        });

        Assert.True(foam.CanEditNodeConfig);
        Assert.True(foam.CanSendNodeConfig);

        foam.OffsetMmText = "25";
        foam.SamplePeriodMsText = "500";
        foam.SendPeriodMsText = "2000";

        foam.SendNodeConfigCommand.Execute(null);

        Assert.Single(dispatcher.Sent);
        var sentJson = dispatcher.Sent[0];
        Assert.Contains("\"distanceOffsetMm\":25", sentJson);
        Assert.Contains("\"distanceSamplePeriodMs\":500", sentJson);
        Assert.Contains("\"distanceSendPeriodMs\":2000", sentJson);

        Assert.Equal(25.0, settings.Current.FoamControl.DistanceOffsetMm);
        Assert.Equal(500, settings.Current.FoamControl.DistanceSamplePeriodMs);
        Assert.Equal(2000, settings.Current.FoamControl.DistanceSendPeriodMs);
        Assert.Contains("enviada", foam.NodeConfigStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Distance_node_config_reset_confirms_destructive_and_sends_command()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var dispatcher = new StubDispatcher();
        var dialog = new StubDialogService();
        using var foam = new FoamControlViewModel(device, settings, dispatcher, dialog);

        device.PushTelemetry(new SensorSnapshot
        {
            HasDistanceTelemetry = true,
            DistanceOnline = true,
            DistanceCommEnabled = true,
            DistanceOffsetMm = 20.0,
        });

        // Case 1: Operator cancels confirmation
        dialog.ConfirmResult = false;
        foam.ResetNodeConfigCommand.Execute(null);

        Assert.Equal(1, dialog.ConfirmDestructiveCalls);
        Assert.Empty(dispatcher.Sent);
        Assert.Contains("offset 20 mm", dialog.ConsequencePassed ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Restaurar", dialog.ExactCommandPassed);

        // Case 2: Operator confirms
        dialog.ConfirmResult = true;
        foam.ResetNodeConfigCommand.Execute(null);

        Assert.Equal(2, dialog.ConfirmDestructiveCalls);
        Assert.Single(dispatcher.Sent);
        Assert.Contains("\"distanceResetNvs\":1", dispatcher.Sent[0]);
        Assert.Contains("restauração", foam.NodeConfigStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Distance_node_telemetry_echoes_update_applied_properties()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var foam = new FoamControlViewModel(device, settings);

        Assert.Equal("—", foam.AppliedOffsetText);
        Assert.Equal("—", foam.AppliedSamplePeriodMsText);
        Assert.Equal("—", foam.AppliedSendPeriodMsText);
        Assert.False(foam.CanEditNodeConfig);

        device.PushTelemetry(new SensorSnapshot
        {
            HasDistanceTelemetry = true,
            DistanceOnline = true,
            DistanceOffsetMm = 25.5,
            DistanceSamplePeriodMs = 500,
            DistanceSendPeriodMs = 1500,
        });

        Assert.True(foam.CanEditNodeConfig);
        Assert.Contains("25", foam.AppliedOffsetText);
        Assert.Equal("500 ms", foam.AppliedSamplePeriodMsText);
        Assert.Equal("1500 ms", foam.AppliedSendPeriodMsText);
    }

    // ── Airflow controller tuning ─────────────────────────────────────────────

    [Fact]
    public void Flow_tuning_validates_ranges_and_updates_status()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: dispatcher, settings: settings);

        // Initial staged values come from default settings
        Assert.True(flow.KpText is "0.8" or "0,8");
        Assert.True(flow.KiText is "0.15" or "0,15");
        Assert.True(flow.FfGainText is "0.106" or "0,106");
        Assert.True(flow.FfOffsetText is "0.01033" or "0,01033");
        Assert.True(flow.RampRateText is "2" or "2.0" or "2,0");
        Assert.True(flow.IsTuningValid);
        Assert.Null(flow.TuningValidationError);

        // Kp range (0, 100]
        flow.KpText = "0";
        Assert.False(flow.IsTuningValid);
        Assert.NotNull(flow.TuningValidationError);

        flow.KpText = "150";
        Assert.False(flow.IsTuningValid);

        flow.KpText = "0.8";
        Assert.True(flow.IsTuningValid);

        // Ki range [0, 100]
        flow.KiText = "-1";
        Assert.False(flow.IsTuningValid);

        flow.KiText = "0";
        Assert.True(flow.IsTuningValid);

        // FfGain range [0, 10]
        flow.FfGainText = "15";
        Assert.False(flow.IsTuningValid);

        flow.FfGainText = "0.106";
        Assert.True(flow.IsTuningValid);

        // FfOffset range [0, 5]
        flow.FfOffsetText = "6";
        Assert.False(flow.IsTuningValid);

        flow.FfOffsetText = "0.01033";
        Assert.True(flow.IsTuningValid);

        // RampRate range (0, 100]
        flow.RampRateText = "0";
        Assert.False(flow.IsTuningValid);

        flow.RampRateText = "2.0";
        Assert.True(flow.IsTuningValid);
    }

    [Fact]
    public void Flow_tuning_dispatches_command_and_persists_settings_when_accepted()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: dispatcher, settings: settings);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowControlEnabled = true,
            FlowKp = 0.8,
            FlowKi = 0.15,
            FlowFfGain = 0.106,
            FlowFfOffset = 0.01033,
            FlowRampRate = 2.0,
        });

        Assert.True(flow.CanEditTuning);
        Assert.True(flow.CanSendTuning);

        flow.KpText = "1.2";
        flow.KiText = "0.25";
        flow.FfGainText = "0.15";
        flow.FfOffsetText = "0.02";
        flow.RampRateText = "3.0";

        flow.SendTuningCommand.Execute(null);

        Assert.Single(dispatcher.Sent);
        var sentJson = dispatcher.Sent[0];
        Assert.Contains("\"flowKp\":1.2", sentJson);
        Assert.Contains("\"flowKi\":0.25", sentJson);
        Assert.Contains("\"flowFfGain\":0.15", sentJson);
        Assert.Contains("\"flowFfOffset\":0.02", sentJson);
        Assert.Contains("\"flowRampRate\":3", sentJson);

        Assert.Equal(1.2, settings.Current.FlowControl.Kp);
        Assert.Equal(0.25, settings.Current.FlowControl.Ki);
        Assert.Equal(0.15, settings.Current.FlowControl.FfGain);
        Assert.Equal(0.02, settings.Current.FlowControl.FfOffset);
        Assert.Equal(3.0, settings.Current.FlowControl.RampRate);
        Assert.Contains("enviada", flow.TuningStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Flow_tuning_refusal_by_arbiter_displays_message_and_preserves_settings()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: dispatcher, settings: settings);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowControlEnabled = true,
            FlowKp = 0.8,
        });

        dispatcher.RefuseWith = [ActuatorId.Aeration];

        flow.KpText = "1.5";
        flow.SendTuningCommand.Execute(null);

        Assert.Contains("recusado", flow.TuningStatusText, StringComparison.OrdinalIgnoreCase);
        // Settings remain at original values
        Assert.Equal(0.8, settings.Current.FlowControl.Kp);
    }

    [Fact]
    public void Flow_tuning_telemetry_echoes_and_readouts_update()
    {
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService();
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: dispatcher, settings: settings);

        Assert.Equal("—", flow.AppliedKpText);
        Assert.Equal("—", flow.AppliedKiText);
        Assert.Equal("—", flow.AppliedFfGainText);
        Assert.Equal("—", flow.AppliedFfOffsetText);
        Assert.Equal("—", flow.AppliedRampRateText);
        Assert.Equal("—", flow.FlowOutputText);
        Assert.Equal("—", flow.FlowSetpointCorrectedText);
        Assert.False(flow.CanEditTuning);

        flow.UpdateTelemetry(new SensorSnapshot
        {
            FlowmeterOnline = true,
            FlowControlEnabled = true,
            FlowKp = 0.8,
            FlowKi = 0.15,
            FlowFfGain = 0.106,
            FlowFfOffset = 0.01033,
            FlowRampRate = 2.0,
            FlowOutput = 3.25,
            FlowSetpointCorrected = 1.75,
        });

        Assert.True(flow.CanEditTuning);
        Assert.Contains("0.8", flow.AppliedKpText.Replace(',', '.'));
        Assert.Contains("0.15", flow.AppliedKiText.Replace(',', '.'));
        Assert.Contains("0.106", flow.AppliedFfGainText.Replace(',', '.'));
        Assert.Contains("0.01033", flow.AppliedFfOffsetText.Replace(',', '.'));
        Assert.Contains("2", flow.AppliedRampRateText.Replace(',', '.'));
        Assert.Contains("3.25", flow.FlowOutputText.Replace(',', '.'));
        Assert.Contains("1.75", flow.FlowSetpointCorrectedText.Replace(',', '.'));
    }
}
