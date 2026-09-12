using OpenTECHub.Protocol;
using OpenTECHub.Services.Alarms;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The operational safety alarm engine: latch, acknowledge, deadband, timed audible silence,
/// and the six system conditions — all deterministic against an injected clock.
/// </summary>
public sealed class AlarmServiceTests
{
    private sealed class FakeAnnunciator : IAlarmAnnunciator
    {
        public bool Sounding { get; private set; }
        public int Changes { get; private set; }

        public void SetSounding(bool sounding)
        {
            Sounding = sounding;
            Changes++;
        }
    }

    private sealed class RecordingJournal : IEventJournal
    {
        public List<(AuditSource Source, AuditSeverity Severity, string Message)> Entries { get; } = [];

        public event Action<AuditEvent>? EntryAdded;

        public IReadOnlyList<AuditEvent> Snapshot() => [];

        public void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null)
        {
            Entries.Add((source, severity, message));
            EntryAdded?.Invoke(new AuditEvent(Entries.Count, DateTimeOffset.UnixEpoch, source, severity, message, detail ?? ""));
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Only the hold state matters here; the rest of the engine is not exercised.</summary>
    private sealed class StubRecipeEngine : IRecipeEngine
    {
        private RecipeDeviceWait? _waiting;

        public RecipeDeviceWait? Waiting
        {
            get => _waiting;
            set { _waiting = value; WaitingChanged?.Invoke(); }
        }

        public RecipeRunState State => RecipeRunState.Idle;
        public string? StatusReason => null;
        public RecipeDocument? Current => null;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public Task Completion => Task.CompletedTask;
        public bool CanStart(RecipeDocument recipe, out string? reason) { reason = null; return true; }
        public Task StartAsync(RecipeDocument recipe, bool resetLoopsBeforeStart = false, CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public Task StopAsync(string reason) => Task.CompletedTask;
        public void SkipWait() { }
        public bool ApplyLiveTuning(RecipeNode node) => false;
        public NodeState NodeStateOf(string nodeId) => NodeState.Waiting;
        public bool WasTraversed(RecipeConnection connection) => false;
        public OpenTECHub.Services.Control.CascadeTerms? CascadeTermsFor(string nodeId) => null;
        public event Action<string>? NodeStateChanged { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action? WaitingChanged;
        public event Action<RecipeLogEntry>? Logged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService();
            Arbiter = new CommandArbiter(Device, Clock);
            Journal = new RecordingJournal();
            Annunciator = new FakeAnnunciator();
            Recipes = new StubRecipeEngine();
            Service = new AlarmService(Device, Arbiter, Settings, Journal, Annunciator, Clock, Recipes);
        }

        public TestClock Clock { get; }
        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public CommandArbiter Arbiter { get; }
        public RecordingJournal Journal { get; }
        public FakeAnnunciator Annunciator { get; }
        public StubRecipeEngine Recipes { get; }
        public AlarmService Service { get; }

        /// <summary>Moves the clock forward, then re-evaluates — as the shell's tick would.</summary>
        public void AdvanceAndPoll(TimeSpan delta)
        {
            Clock.Advance(delta);
            Service.Poll();
        }

        public bool Latched(AlarmId id) => Service.Snapshot().Any(a => a.Id == id);

        public AlarmSnapshot? Get(AlarmId id) => Service.Snapshot().FirstOrDefault(a => a.Id == id);

        public void Dispose()
        {
            Service.Dispose();
            Arbiter.Dispose();
        }
    }

    /// <summary>A frame that trips no alarm on its own: module up, oxygen present, flow idle.</summary>
    private static SensorSnapshot HealthyFrame() => new()
    {
        SensorCommOk = true,
        OxygenCalibrated = 30,
        FlowmeterOnline = true,
        FlowControlEnabled = false,
    };

    // ── Recipe holding for an external device ────────────────────────────────

    [Fact]
    public void A_recipe_holding_for_a_device_annunciates_and_clears_when_it_answers()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(HealthyFrame());
        Assert.False(h.Latched(AlarmId.RecipeAwaitingDevice));

        h.Recipes.Waiting = new RecipeDeviceWait(
            "sp", "Fluxômetro", "a vazão de 2,5 L/min não foi confirmada pelo fluxômetro.",
            DateTimeOffset.UnixEpoch);

        Assert.True(h.Latched(AlarmId.RecipeAwaitingDevice));
        Assert.True(h.Service.IsAudible);
        Assert.Contains("Fluxômetro", h.Get(AlarmId.RecipeAwaitingDevice)!.Detail);

        // Acknowledged and then answered, the alarm returns to normal on its own.
        h.Service.Acknowledge(AlarmId.RecipeAwaitingDevice);
        h.Recipes.Waiting = null;
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1));
        Assert.False(h.Latched(AlarmId.RecipeAwaitingDevice));
    }

    // ── Link lost ────────────────────────────────────────────────────────────

    [Fact]
    public void Link_loss_latches_only_after_the_on_delay()
    {
        using var h = new Harness();

        h.Device.PushState(ConnectionState.Faulted); // condition begins, polls once
        Assert.False(h.Latched(AlarmId.LinkLost));    // still inside the 1 s on-delay

        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        Assert.True(h.Latched(AlarmId.LinkLost));
        Assert.True(h.Service.IsAudible);
        Assert.True(h.Annunciator.Sounding);
        Assert.Contains(h.Journal.Entries, e =>
            e.Source == AuditSource.Alarm && e.Severity == AuditSeverity.Error &&
            e.Message.Contains("Link perdido", StringComparison.Ordinal));
    }

    [Fact]
    public void The_exit_criterion_a_link_loss_produces_one_latched_alarm()
    {
        using var h = new Harness();

        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));

        var alarm = Assert.Single(h.Service.Snapshot());
        Assert.Equal(AlarmId.LinkLost, alarm.Id);
        Assert.True(alarm.Latched);
        Assert.False(alarm.Acknowledged);
    }

    [Fact]
    public void Manual_disconnect_resolves_link_loss_and_stops_the_annunciator()
    {
        using var h = new Harness();

        h.Device.PushState(ConnectionState.Faulted, cause: ConnectionTransitionCause.LinkLost);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        Assert.True(h.Latched(AlarmId.LinkLost));
        Assert.True(h.Annunciator.Sounding);

        h.Device.PushState(
            ConnectionState.Disconnected,
            "desconectado pelo usuário",
            ConnectionTransitionCause.UserDisconnect);

        Assert.False(h.Latched(AlarmId.LinkLost));
        Assert.False(h.Service.IsAudible);
        Assert.False(h.Annunciator.Sounding);
        Assert.Contains(h.Journal.Entries, entry =>
            entry.Message.Contains("Link perdido", StringComparison.Ordinal));
    }

    /// <summary>
    /// The banner is redrawn on <c>Changed</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// Resolving on a manual disconnect clears the latch behind the state machine's back,
    /// so the poll that follows finds no transition to report. Without an explicit
    /// notification the operator kept looking at a "Link perdido" banner for an alarm the
    /// service had already dropped — and Reconhecer could not dismiss it either, because
    /// there was no longer anything latched to acknowledge.
    /// </remarks>
    [Fact]
    public void Manual_disconnect_notifies_that_link_loss_is_gone()
    {
        using var h = new Harness();

        h.Device.PushState(ConnectionState.Faulted, cause: ConnectionTransitionCause.LinkLost);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        Assert.True(h.Latched(AlarmId.LinkLost));

        var notifications = 0;
        h.Service.Changed += () => notifications++;

        h.Device.PushState(
            ConnectionState.Disconnected,
            "desconectado pelo usuário",
            ConnectionTransitionCause.UserDisconnect);

        Assert.Equal(1, notifications);
        Assert.Empty(h.Service.Snapshot());
    }

    [Fact]
    public void Manual_disconnect_does_not_erase_an_unacknowledged_process_alarm()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(HealthyFrame() with
        {
            OxygenCalibrated = SensorReadings.NotReceived,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5.1));
        Assert.True(h.Latched(AlarmId.SensorAbsent));

        h.Device.PushState(ConnectionState.Faulted, cause: ConnectionTransitionCause.LinkLost);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));

        Assert.True(h.Latched(AlarmId.LinkLost));
        Assert.True(h.Latched(AlarmId.SensorAbsent));

        h.Device.PushState(
            ConnectionState.Disconnected,
            "desconectado pelo usuário",
            ConnectionTransitionCause.UserDisconnect);

        Assert.False(h.Latched(AlarmId.LinkLost));
        Assert.True(h.Latched(AlarmId.SensorAbsent));
        Assert.False(h.Service.IsAudible);
    }

    // ── Acknowledge / latch / deadband ───────────────────────────────────────

    [Fact]
    public void Acknowledge_silences_the_audio_but_the_alarm_stays_while_the_condition_holds()
    {
        using var h = new Harness();
        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));

        h.Service.Acknowledge(AlarmId.LinkLost);

        Assert.False(h.Service.IsAudible);
        Assert.False(h.Annunciator.Sounding);
        var alarm = Assert.Single(h.Service.Snapshot());
        Assert.True(alarm.Latched);
        Assert.True(alarm.Acknowledged);
    }

    [Fact]
    public void An_acknowledged_alarm_clears_after_the_condition_returns_and_the_deadband_passes()
    {
        using var h = new Harness();
        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        h.Service.Acknowledge(AlarmId.LinkLost);

        h.Device.PushState(ConnectionState.Connected); // condition returns to normal
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));    // past the 1 s off-deadband

        Assert.Empty(h.Service.Snapshot());
        Assert.False(h.Service.HasActiveAlarms);
    }

    [Fact]
    public void An_alarm_that_returns_to_normal_clears_itself_even_unacknowledged()
    {
        using var h = new Harness();
        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        Assert.True(h.Latched(AlarmId.LinkLost));

        // Condition clears while nobody has acknowledged it. During the off-deadband it is
        // held as returned-unacknowledged...
        h.Device.PushState(ConnectionState.Connected);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(0.5)); // still inside the 1 s off-deadband
        var alarm = Assert.Single(h.Service.Snapshot());
        Assert.True(alarm.IsReturnedUnacknowledged);
        Assert.Equal("Normalizado, não reconhecido", alarm.StateLabel);

        // ...then it clears on its own once the deadband passes — no acknowledgement needed,
        // and the occurrence stays in the journal.
        h.AdvanceAndPoll(TimeSpan.FromSeconds(0.7)); // now past the 1 s deadband
        Assert.Empty(h.Service.Snapshot());
        Assert.Contains(h.Journal.Entries, e =>
            e.Message.Contains("normalizado", StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains("Link perdido", StringComparison.Ordinal));
    }

    // ── Timed audible silence ────────────────────────────────────────────────

    [Fact]
    public void Silence_mutes_the_audio_for_the_window_then_it_sounds_again()
    {
        using var h = new Harness();
        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));
        Assert.True(h.Service.IsAudible);

        h.Service.Silence();
        Assert.False(h.Service.IsAudible);
        Assert.False(h.Annunciator.Sounding);

        // Not a permanent mute: after the window it sounds again, still unacknowledged.
        h.AdvanceAndPoll(AlarmService.SilenceWindow + TimeSpan.FromSeconds(1));
        Assert.True(h.Service.IsAudible);
        Assert.True(h.Annunciator.Sounding);
    }

    [Fact]
    public void A_new_alarm_re_sounds_through_an_active_silence()
    {
        using var h = new Harness();

        // Module offline (2 s) and the oxygen sentinel (5 s) both begin now.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            SensorCommOk = false,
            OxygenCalibrated = SensorReadings.NotReceived,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.True(h.Latched(AlarmId.ModuleOffline));
        Assert.True(h.Service.IsAudible);

        h.Service.Silence();
        Assert.False(h.Service.IsAudible);

        // The later sensor-absent alarm is a different fault; the earlier silence must
        // not hide it.
        h.AdvanceAndPoll(TimeSpan.FromSeconds(3.2)); // ~5.3 s total, still within the stale budget
        Assert.True(h.Latched(AlarmId.SensorAbsent));
        Assert.True(h.Service.IsAudible);
    }

    // ── Telemetry-driven conditions ──────────────────────────────────────────

    [Fact]
    public void Module_offline_latches_from_the_sensor_comm_flag()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(HealthyFrame() with { SensorCommOk = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.True(h.Latched(AlarmId.ModuleOffline));
    }

    [Fact]
    public void Flowmeter_offline_alarms_even_when_the_legacy_hub_flag_is_disabled()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Airflow, true);

        h.Device.PushTelemetry(HealthyFrame() with
        {
            FlowControlEnabled = false,
            FlowmeterOnline = false,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.True(h.Latched(AlarmId.FlowmeterOffline));
    }

    [Fact]
    public void Gas_left_open_when_the_flowmeter_drops_raises_the_unsupervised_alarm()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Airflow, true);

        // Gas confirmed flowing to the reactor: valve 1 routed, vent shut.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            FlowRate = 4.8,
            FlowSetpoint = 5.0,
            FlowValve1 = 1,
            FlowValveMain = 0,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.False(h.Latched(AlarmId.UnsupervisedGasFlow));

        // The node drops. The hub stops publishing flow values, so the frame carries none -
        // the node is fail-in-place, so the gas it was passing is still passing.
        h.Device.PushTelemetry(HealthyFrame() with { FlowmeterOnline = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.True(h.Latched(AlarmId.UnsupervisedGasFlow));
        Assert.Equal(AlarmSeverity.Critical, h.Get(AlarmId.UnsupervisedGasFlow)!.Severity);
        Assert.Contains("fail-in-place", h.Get(AlarmId.UnsupervisedGasFlow)!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flowmeter_that_drops_with_the_gas_shut_raises_only_the_offline_alarm()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Airflow, true);

        // Everything closed, and the residual the sensor always reads at rest.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            FlowRate = 0.05,
            FlowSetpoint = 0.0,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 1,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        h.Device.PushTelemetry(HealthyFrame() with { FlowmeterOnline = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.True(h.Latched(AlarmId.FlowmeterOffline));
        Assert.False(h.Latched(AlarmId.UnsupervisedGasFlow));
    }

    [Fact]
    public void The_unsupervised_gas_alarm_clears_when_the_node_comes_back_closed()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Airflow, true);

        h.Device.PushTelemetry(HealthyFrame() with { FlowSetpoint = 5.0, FlowRate = 4.8 });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        h.Device.PushTelemetry(HealthyFrame() with { FlowmeterOnline = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.True(h.Latched(AlarmId.UnsupervisedGasFlow));

        h.Service.Acknowledge(AlarmId.UnsupervisedGasFlow);
        h.Device.PushTelemetry(HealthyFrame() with
        {
            FlowRate = 0.0,
            FlowSetpoint = 0.0,
            FlowValveMain = 1,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.False(h.Latched(AlarmId.UnsupervisedGasFlow));
    }

    // ── External-device presence and routing ─────────────────────────────────

    /// <summary>
    /// Qualified by the Hub's own routing echo, so a device the operator deliberately
    /// switched off never raises one.
    /// </summary>
    [Fact]
    public void An_absent_external_node_latches_only_while_the_hub_is_routing_to_it()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Absorbance, true);

        // Routed off: absence is expected, and silent.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            HasBiomassTelemetry = true,
            BiomassOnline = false,
            BiomassCommEnabled = false,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.False(h.Latched(AlarmId.BiomassOffline));

        // Routed on and not answering: that is a fault.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            HasBiomassTelemetry = true,
            BiomassOnline = false,
            BiomassCommEnabled = true,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.True(h.Latched(AlarmId.BiomassOffline));
    }

    /// <summary>
    /// A Hub that predates the presence keys leaves them null. Guessing either way would be
    /// wrong, and guessing "absent" would raise a fault on every device at once.
    /// </summary>
    [Fact]
    public void A_hub_without_the_presence_keys_raises_nothing()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(HealthyFrame());
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5));

        Assert.False(h.Latched(AlarmId.BiomassOffline));
        Assert.False(h.Latched(AlarmId.ExternalPumpOffline));
        Assert.False(h.Latched(AlarmId.DistanceSensorOffline));
        Assert.False(h.Latched(AlarmId.FlaskAgitatorOffline));
        Assert.False(h.Latched(AlarmId.DeviceRoutingMismatch));
    }

    /// <summary>
    /// The Hub persists its routing flags in NVS and the app persists the switches on the
    /// PC. After a Hub reboot they can differ, and from then on every biomass or pump
    /// sub-command is dropped by the Hub without any reply at all.
    /// </summary>
    [Fact]
    public void The_hub_and_the_operator_disagreeing_about_routing_is_annunciated()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Absorbance, true);

        h.Device.PushTelemetry(HealthyFrame() with
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassCommEnabled = false,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5.1));

        Assert.True(h.Latched(AlarmId.DeviceRoutingMismatch));
        Assert.Contains(DeviceNames.Absorbance, h.Get(AlarmId.DeviceRoutingMismatch)!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Agreement_on_routing_raises_nothing()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Absorbance, true);

        h.Device.PushTelemetry(HealthyFrame() with
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassCommEnabled = true,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5.1));

        Assert.False(h.Latched(AlarmId.DeviceRoutingMismatch));
    }

    /// <summary>
    /// An external device the operator has not enabled in Controle stays silent, even when
    /// the Hub is still routing it (a flag persisted across a reboot) and it is not answering.
    /// The divergence itself is carried by the routing-mismatch alarm, not by a spurious
    /// offline.
    /// </summary>
    [Fact]
    public void An_external_offline_alarm_is_silent_when_the_operator_has_the_device_off()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested(DeviceNames.Absorbance, false);

        h.Device.PushTelemetry(HealthyFrame() with
        {
            HasBiomassTelemetry = true,
            BiomassOnline = false,
            BiomassCommEnabled = true, // Hub still routing it
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));

        Assert.False(h.Latched(AlarmId.BiomassOffline));
    }

    [Fact]
    public void Frozen_data_latches_when_telemetry_stops()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(HealthyFrame()); // last frame at t0

        // No further frames for more than three emission periods.
        h.AdvanceAndPoll(TimeSpan.FromSeconds(7));

        Assert.True(h.Latched(AlarmId.FrozenData));
    }

    [Fact]
    public void Sensor_absent_latches_when_the_oxygen_probe_sentinel_persists()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(HealthyFrame() with { OxygenCalibrated = SensorReadings.NotReceived });

        // Within the stale budget so this is "absent", not "frozen".
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5.2));

        Assert.True(h.Latched(AlarmId.SensorAbsent));
        Assert.False(h.Latched(AlarmId.FrozenData));
    }

    // ── Unacknowledged command, via the arbiter lifecycle ────────────────────

    [Fact]
    public void A_timed_out_command_latches_the_unconfirmed_alarm_and_recovery_clears_it()
    {
        using var h = new Harness();
        h.Device.RaiseCommandSentOnSend = false; // the transport buffers but never flushes

        h.Arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37.5));
        h.Clock.Advance(TimeSpan.FromSeconds(9));   // past the arbiter's accept timeout
        h.Device.PushTelemetry(HealthyFrame());     // drives the arbiter's timeout sweep
        h.Service.Poll();

        Assert.True(h.Latched(AlarmId.UnacknowledgedCommand));

        // The command recovers: a later frame is accepted by the transport.
        h.Device.RaiseCommandSentOnSend = true;
        h.Arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37.5));
        h.Service.Acknowledge(AlarmId.UnacknowledgedCommand);
        h.Service.Poll();

        Assert.False(h.Latched(AlarmId.UnacknowledgedCommand));
    }

    // ── Housekeeping ─────────────────────────────────────────────────────────

    [Fact]
    public void A_disconnect_drops_the_last_frame_so_a_stale_module_reading_does_not_linger()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(HealthyFrame() with { SensorCommOk = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        h.Service.Acknowledge(AlarmId.ModuleOffline);

        // A clean disconnect: the module condition can no longer be evaluated as active.
        h.Device.PushState(ConnectionState.Disconnected);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(3));

        Assert.False(h.Latched(AlarmId.ModuleOffline));
    }

    [Fact]
    public void The_headline_is_the_most_severe_annunciating_alarm()
    {
        using var h = new Harness();

        // A warning (sensor absent) and a critical (module offline) both active.
        h.Device.PushTelemetry(HealthyFrame() with
        {
            SensorCommOk = false,
            OxygenCalibrated = SensorReadings.NotReceived,
        });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5.2));

        Assert.True(h.Latched(AlarmId.ModuleOffline));
        Assert.True(h.Latched(AlarmId.SensorAbsent));
        Assert.Equal(AlarmId.ModuleOffline, h.Service.Headline!.Id); // critical outranks warning
    }

    // ── ASDA-B2 servo drive ──────────────────────────────────────────────────
    //
    // Four legitimate states here can be mistaken for a fault, and each of them would
    // produce an alarm nothing could ever clear. The negative tests are the point; the
    // two positive ones are the easy half.

    /// <summary>A frame with the servo present, routed and healthy.</summary>
    private static SensorSnapshot ServoFrame(
        bool hasTelemetry = true,
        bool online = true,
        bool? commEnabled = true,
        bool hasSample = true,
        int state = 2,
        int alarm = 0) => HealthyFrame() with
        {
            HasServoTelemetry = hasTelemetry,
            HasServoSample = hasSample,
            ServoOnline = online,
            ServoCommEnabled = commEnabled,
            ServoState = state,
            ServoAlarm = alarm,
            ServoRpm = 600.5,
        };

    [Fact]
    public void An_absent_servo_node_with_routing_on_raises_the_offline_alarm()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(online: false, commEnabled: true, hasSample: false));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(11));

        Assert.True(h.Latched(AlarmId.ServoDriveOffline));
    }

    /// <summary>
    /// The on-delay outlasts the Hub's own presence window plus one aggregate frame.
    /// </summary>
    /// <remarks>
    /// The Hub's servo window is 6 s and it publishes at the <c>dataDelay</c> of 2 s, so a
    /// node that misses a single push can be reported absent for up to eight seconds through
    /// no fault of its own. The two seconds the other device alarms use would fire on that.
    /// </remarks>
    [Fact]
    public void The_servo_offline_alarm_waits_out_the_hubs_own_window()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(online: false, commEnabled: true, hasSample: false));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(8));

        Assert.False(h.Latched(AlarmId.ServoDriveOffline));
    }

    [Fact]
    public void A_drive_in_alarm_raises_a_critical_alarm_carrying_the_panel_code()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(state: 3, alarm: 0x0011));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1));

        var snapshot = h.Get(AlarmId.ServoDriveAlarm);
        Assert.NotNull(snapshot);
        Assert.Equal(AlarmSeverity.Critical, snapshot!.Severity);

        // AL011 is what the drive's own panel shows for 0x0011. Decimal 17 matches nothing
        // in the manual and would send whoever looks it up to the wrong page.
        Assert.Contains("AL011", snapshot.Detail, StringComparison.Ordinal);
    }

    /// <summary>A fault code alone is enough; the bench saw one arrive before the state.</summary>
    [Fact]
    public void A_servo_fault_code_without_the_alarm_state_still_raises_it()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(state: 2, alarm: 0x0011));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1));

        Assert.True(h.Latched(AlarmId.ServoDriveAlarm));
    }

    /// <summary>
    /// A module configured without a servo: both flags false, for ever.
    /// </summary>
    /// <remarks>
    /// The bench module's permanent state after <c>{"servoComm":0}</c>, persisted in NVS and
    /// confirmed across a reboot on 2026-09-02. An alarm here would raise an event nothing
    /// could ever clear, on a module behaving exactly as configured.
    /// </remarks>
    [Fact]
    public void A_module_without_a_servo_never_alarms()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(online: false, commEnabled: false, hasSample: false));
        h.AdvanceAndPoll(TimeSpan.FromMinutes(5));

        Assert.False(h.Latched(AlarmId.ServoDriveOffline));
        Assert.False(h.Latched(AlarmId.ServoDriveAlarm));
        Assert.False(h.Latched(AlarmId.DeviceRoutingMismatch));
    }

    /// <summary>An older Hub has claimed nothing, which is not a report of failure.</summary>
    [Fact]
    public void A_hub_without_the_servo_keys_never_alarms_about_it()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(
            hasTelemetry: false, online: false, commEnabled: null, hasSample: false));
        h.AdvanceAndPoll(TimeSpan.FromMinutes(5));

        Assert.False(h.Latched(AlarmId.ServoDriveOffline));
        Assert.False(h.Latched(AlarmId.ServoDriveAlarm));
    }

    /// <summary>Routing off with the node present is a configuration, not a fault.</summary>
    [Fact]
    public void Servo_routing_switched_off_with_the_node_present_never_alarms()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame(online: true, commEnabled: false, hasSample: false));
        h.AdvanceAndPoll(TimeSpan.FromMinutes(5));

        Assert.False(h.Latched(AlarmId.ServoDriveOffline));
    }

    /// <summary>
    /// Modbus errors raise nothing on their own — not at this stage.
    /// </summary>
    /// <remarks>
    /// The bench saw exactly one error in 256 reads, on the first transaction after boot. A
    /// threshold on the running total would fire on a perfectly healthy link and never
    /// clear. The rate is on the card; the threshold waits for the 2 h soak to say what a
    /// bad rate actually looks like.
    /// </remarks>
    [Fact]
    public void Servo_modbus_errors_do_not_raise_an_alarm_yet()
    {
        using var h = new Harness();

        h.Device.PushTelemetry(ServoFrame() with { ServoCommOk = 255, ServoCommErr = 40 });
        h.AdvanceAndPoll(TimeSpan.FromMinutes(5));

        Assert.False(h.Latched(AlarmId.ServoDriveAlarm));
        Assert.False(h.Latched(AlarmId.ServoDriveOffline));
    }

    /// <summary>
    /// The Hub routing a servo the operator switched off is worth saying.
    /// </summary>
    /// <remarks>
    /// The dangerous direction is the other one: with the Hub not routing, the servo's ten
    /// values silently stop reaching the aggregate frame while the switch still reads "on".
    /// </remarks>
    [Fact]
    public void A_routing_disagreement_on_the_servo_is_annunciated()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested("Servo drive", requested: false);

        h.Device.PushTelemetry(ServoFrame(commEnabled: true));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(6));

        Assert.True(h.Latched(AlarmId.DeviceRoutingMismatch));
        Assert.Contains("Servo drive", h.Get(AlarmId.DeviceRoutingMismatch)!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Agreement_on_the_servo_routing_annunciates_nothing()
    {
        using var h = new Harness();
        h.Service.SetRoutingRequested("Servo drive", requested: true);

        h.Device.PushTelemetry(ServoFrame(commEnabled: true));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(6));

        Assert.False(h.Latched(AlarmId.DeviceRoutingMismatch));
    }

    // ── A/B/C rig: dead end and both open (plan Etapa 7) ─────────────────────

    /// <summary>A gas frame on the default wiring: B/C = valve_1, A = valve_2.</summary>
    private static SensorSnapshot GasFrame(int valve1, int valve2, double setpoint) => HealthyFrame() with
    {
        FlowValve1 = valve1,
        FlowValve2 = valve2,
        FlowValveMain = setpoint > 0 ? 0 : 1,
        FlowSetpoint = setpoint,
        FlowRate = setpoint,
    };

    /// <summary>
    /// A setpoint above zero with both inputs closed is a line with no way out. The router
    /// never commands it; Avançado can, and so can a stale state. Three seconds of it rings.
    /// </summary>
    [Fact]
    public void A_dead_ended_line_annunciates_after_three_seconds_and_clears_when_routed()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(GasFrame(0, 1, 3.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1));
        Assert.False(h.Latched(AlarmId.GasDeadEnd));

        // Both closed with the setpoint still echoed: not yet, the one-frame switch can look like this.
        h.Device.PushTelemetry(GasFrame(0, 0, 3.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2));
        Assert.False(h.Latched(AlarmId.GasDeadEnd));

        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.5));
        Assert.True(h.Latched(AlarmId.GasDeadEnd));
        Assert.Equal(AlarmSeverity.Critical, h.Get(AlarmId.GasDeadEnd)!.Severity);
        Assert.Contains("fechar a linha", h.Get(AlarmId.GasDeadEnd)!.Detail, StringComparison.Ordinal);
        Assert.True(h.Service.IsAudible);
        Assert.Contains(h.Journal.Entries, e => e.Source == AuditSource.Alarm && e.Message.Contains("Gás sem destino", StringComparison.Ordinal));

        // Giving the gas a destination clears it once acknowledged.
        h.Service.Acknowledge(AlarmId.GasDeadEnd);
        h.Device.PushTelemetry(GasFrame(0, 1, 3.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.5));
        Assert.False(h.Latched(AlarmId.GasDeadEnd));
    }

    /// <summary>Closing the line on purpose (setpoint zero) is "Fechado", not a dead end.</summary>
    [Fact]
    public void A_closed_line_with_no_setpoint_is_not_a_dead_end()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(GasFrame(0, 0, 0.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(10));
        Assert.False(h.Latched(AlarmId.GasDeadEnd));
        Assert.False(h.Latched(AlarmId.GasBothOpen));
    }

    [Fact]
    public void Both_inputs_open_is_a_warning_without_audio()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(GasFrame(1, 1, 3.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(3.5));

        Assert.True(h.Latched(AlarmId.GasBothOpen));
        Assert.Equal(AlarmSeverity.Warning, h.Get(AlarmId.GasBothOpen)!.Severity);
        Assert.False(h.Latched(AlarmId.GasDeadEnd));
        Assert.Contains("entradas 1 e 2", h.Get(AlarmId.GasBothOpen)!.Detail, StringComparison.Ordinal);
    }

    /// <summary>The other wiring reads the same pins the other way round; the anomalies do not depend on it.</summary>
    [Fact]
    public void Dead_end_and_both_open_are_wiring_independent()
    {
        using var h = new Harness();
        h.Settings.Update(s => s with { GasRig = new GasRigSettings { AirInletInput = GasInput.Input1 } });
        h.Device.PushTelemetry(GasFrame(0, 0, 2.0));
        h.AdvanceAndPoll(TimeSpan.FromSeconds(3.5));
        Assert.True(h.Latched(AlarmId.GasDeadEnd));
    }

    /// <summary>Every change of the observed route is one line in the journal, in the equipment's words.</summary>
    [Fact]
    public void The_journal_gets_one_line_per_observed_route_change()
    {
        using var h = new Harness();
        h.Device.PushTelemetry(GasFrame(0, 0, 0.0));   // Fechado — the baseline, no line yet
        h.Device.PushTelemetry(GasFrame(0, 0, 0.0));
        h.Device.PushTelemetry(GasFrame(1, 0, 3.0));   // → Descarga + N₂ (B/C)
        h.Device.PushTelemetry(GasFrame(1, 0, 3.0));
        h.Device.PushTelemetry(GasFrame(0, 1, 3.0));   // → Reator (A)
        h.Device.PushTelemetry(GasFrame(0, 0, 3.0));   // → Gás sem destino (warning)

        var lines = h.Journal.Entries.Where(e => e.Source == AuditSource.Equipment && e.Message.StartsWith("Gás: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("Gás: Fechado → Descarga + N₂ (B/C).", lines[0].Message);
        Assert.Equal(AuditSeverity.Information, lines[0].Severity);
        Assert.Equal("Gás: Descarga + N₂ (B/C) → Reator (A).", lines[1].Message);
        Assert.Equal("Gás: Reator (A) → Gás sem destino.", lines[2].Message);
        Assert.Equal(AuditSeverity.Warning, lines[2].Severity);
    }
}
