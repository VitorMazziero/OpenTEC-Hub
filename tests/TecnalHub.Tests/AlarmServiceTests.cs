using TecnalHub.Protocol;
using TecnalHub.Services.Alarms;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Recipes;
using TecnalHub.Services.Telemetry;
using Xunit;

namespace TecnalHub.Tests;

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
        public Task StartAsync(RecipeDocument recipe, CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public Task StopAsync(string reason) => Task.CompletedTask;
        public void SkipWait() { }
        public bool ApplyLiveTuning(RecipeNode node) => false;
        public NodeState NodeStateOf(string nodeId) => NodeState.Waiting;
        public bool WasTraversed(RecipeConnection connection) => false;
        public TecnalHub.Services.Control.CascadeTerms? CascadeTermsFor(string nodeId) => null;
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
    public void An_alarm_that_returns_to_normal_unacknowledged_stays_in_the_list()
    {
        using var h = new Harness();
        h.Device.PushState(ConnectionState.Faulted);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(1.1));

        // Condition clears while nobody has seen it. It must NOT vanish.
        h.Device.PushState(ConnectionState.Connected);
        h.AdvanceAndPoll(TimeSpan.FromSeconds(5));

        var alarm = Assert.Single(h.Service.Snapshot());
        Assert.True(alarm.IsReturnedUnacknowledged);
        Assert.Equal("Normalizado, não reconhecido", alarm.StateLabel);

        // Only the acknowledgement clears it, since the condition is already normal.
        h.Service.Acknowledge(AlarmId.LinkLost);
        h.Service.Poll();
        Assert.Empty(h.Service.Snapshot());
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
    public void Flowmeter_offline_only_alarms_when_flow_control_is_enabled()
    {
        using var h = new Harness();

        // Flowmeter offline but flow not in use: not an alarm.
        h.Device.PushTelemetry(HealthyFrame() with { FlowmeterOnline = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(3));
        Assert.False(h.Latched(AlarmId.FlowmeterOffline));

        // Now flow control is on and the flowmeter reports offline.
        h.Device.PushTelemetry(HealthyFrame() with { FlowControlEnabled = true, FlowmeterOnline = false });
        h.AdvanceAndPoll(TimeSpan.FromSeconds(2.1));
        Assert.True(h.Latched(AlarmId.FlowmeterOffline));
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

        h.Arbiter.Dispatch(CommandOwner.Manual, TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 37.5));
        h.Clock.Advance(TimeSpan.FromSeconds(9));   // past the arbiter's accept timeout
        h.Device.PushTelemetry(HealthyFrame());     // drives the arbiter's timeout sweep
        h.Service.Poll();

        Assert.True(h.Latched(AlarmId.UnacknowledgedCommand));

        // The command recovers: a later frame is accepted by the transport.
        h.Device.RaiseCommandSentOnSend = true;
        h.Arbiter.Dispatch(CommandOwner.Manual, TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 37.5));
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
}
