using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
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
        Assert.Contains("não está roteando", status.StatusText, StringComparison.Ordinal);
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
            """{"flowSetpoint":0.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            Assert.Single(dispatcher.Sent));
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

        Assert.Equal(["""{"mode":0,"speed":0}""", """{"pumpComm":0}"""], ((RecordingDeviceService)device).Sent);
    }
}
