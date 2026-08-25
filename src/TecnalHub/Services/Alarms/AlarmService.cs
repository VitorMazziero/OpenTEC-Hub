using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Telemetry;

namespace TecnalHub.Services.Alarms;

/// <summary>How one alarm's latch changed on an evaluation.</summary>
internal enum AlarmTransition
{
    None,
    Raised,
    Cleared,
}

/// <summary>
/// One system alarm's state machine: on-delay to raise, latch, acknowledge, off-deadband
/// to clear.
/// </summary>
/// <remarks>
/// Latching is the point: once raised, the alarm stays until it has been acknowledged
/// <b>and</b> clear for the deadband. A condition that comes and goes while unacknowledged
/// stays in the returned-unacknowledged state — an alarm nobody saw is the one worth
/// keeping. Time is injected, so every delay is deterministic under test.
/// </remarks>
internal sealed class AlarmCondition(AlarmDefinition definition)
{
    private DateTimeOffset? _pendingSince;
    private DateTimeOffset? _clearingSince;

    public AlarmDefinition Definition => definition;

    public bool ConditionActive { get; private set; }

    public bool Latched { get; private set; }

    public bool Acknowledged { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public string Detail { get; private set; } = "";

    public bool IsAnnunciating => Latched && !Acknowledged;

    /// <summary>Advances the machine against the current raw condition.</summary>
    public AlarmTransition Evaluate(bool active, string detail, DateTimeOffset now)
    {
        ConditionActive = active;
        if (!string.IsNullOrEmpty(detail))
        {
            Detail = detail;
        }

        if (active)
        {
            _clearingSince = null;

            if (!Latched)
            {
                _pendingSince ??= now;
                if (now - _pendingSince >= definition.OnDelay)
                {
                    Latched = true;
                    Acknowledged = false;
                    ActivatedAt = now;
                    AcknowledgedAt = null;
                    _pendingSince = null;
                    return AlarmTransition.Raised;
                }
            }

            return AlarmTransition.None;
        }

        _pendingSince = null;

        if (Latched)
        {
            _clearingSince ??= now;

            // Only an acknowledged alarm clears on its own; an unacknowledged one is held
            // in the returned-unacknowledged state until the operator sees it.
            if (Acknowledged && now - _clearingSince >= definition.OffDeadband)
            {
                Reset();
                return AlarmTransition.Cleared;
            }
        }

        return AlarmTransition.None;
    }

    /// <summary>Acknowledges an occurrence. Returns true if this changed anything.</summary>
    public bool Acknowledge(DateTimeOffset now)
    {
        if (!Latched || Acknowledged)
        {
            return false;
        }

        Acknowledged = true;
        AcknowledgedAt = now;
        return true;
    }

    public AlarmSnapshot ToSnapshot() => new(
        definition.Id,
        definition.Title,
        definition.Severity,
        ConditionActive,
        Latched,
        Acknowledged,
        ActivatedAt,
        AcknowledgedAt,
        Detail);

    /// <summary>Clears a latched occurrence by an explicit operator intervention.</summary>
    public bool Resolve()
    {
        if (!Latched && _pendingSince is null)
        {
            return false;
        }

        ConditionActive = false;
        Reset();
        return true;
    }

    private void Reset()
    {
        Latched = false;
        Acknowledged = false;
        ActivatedAt = null;
        AcknowledgedAt = null;
        _pendingSince = null;
        _clearingSince = null;
    }
}

/// <summary>
/// The operational safety alarm engine: the six system alarms, latched, acknowledgeable,
/// journalled, with an audible indication under a timed silence.
/// </summary>
public interface IAlarmService : IDisposable
{
    /// <summary>Every currently latched alarm, most severe and newest first.</summary>
    IReadOnlyList<AlarmSnapshot> Snapshot();

    /// <summary>The alarm that should headline the banner, or null when none is latched.</summary>
    AlarmSnapshot? Headline { get; }

    /// <summary>How many alarms are latched and unacknowledged.</summary>
    int AnnunciatingCount { get; }

    /// <summary>True while any alarm is latched (acknowledged or not).</summary>
    bool HasActiveAlarms { get; }

    /// <summary>True while the annunciator should be sounding.</summary>
    bool IsAudible { get; }

    /// <summary>Raised on the UI thread whenever alarm state changes.</summary>
    event Action? Changed;

    /// <summary>Acknowledges one alarm.</summary>
    void Acknowledge(AlarmId id);

    /// <summary>Acknowledges every latched alarm.</summary>
    void AcknowledgeAll();

    /// <summary>Silences the audible indication for the fixed silence window; never a permanent mute.</summary>
    void Silence();

    /// <summary>Re-evaluates every condition at the current time. Idempotent.</summary>
    void Poll();
}

/// <inheritdoc cref="IAlarmService"/>
public sealed class AlarmService : IAlarmService
{
    /// <summary>Audio silence window, matching the §5.4.1 toolbar action.</summary>
    public static readonly TimeSpan SilenceWindow = TimeSpan.FromMinutes(10);

    private static readonly AlarmDefinition[] Definitions =
    [
        new(AlarmId.LinkLost, "Link perdido", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
        new(AlarmId.ModuleOffline, "Módulo sem resposta", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.FlowmeterOffline, "Fluxômetro offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.FrozenData, "Dados congelados", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.Zero),
        new(AlarmId.SensorAbsent, "Sensor ausente", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
        new(AlarmId.UnacknowledgedCommand, "Comando não confirmado", AlarmSeverity.Warning,
            TimeSpan.Zero, TimeSpan.Zero),
    ];

    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly ISettingsService _settings;
    private readonly IEventJournal _journal;
    private readonly IAlarmAnnunciator _annunciator;
    private readonly TimeProvider _time;

    private readonly Dictionary<AlarmId, AlarmCondition> _conditions;
    private readonly HashSet<ActuatorId> _timedOut = [];

    private ConnectionState _state = ConnectionState.Disconnected;
    private SensorSnapshot? _lastSnapshot;
    private DateTimeOffset? _lastFrameAt;
    private DateTimeOffset _silencedUntil = DateTimeOffset.MinValue;
    private bool _wasAudible;

    public AlarmService(
        IDeviceService device,
        ICommandArbiter arbiter,
        ISettingsService settings,
        IEventJournal journal,
        IAlarmAnnunciator annunciator,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(annunciator);
        ArgumentNullException.ThrowIfNull(time);

        _device = device;
        _arbiter = arbiter;
        _settings = settings;
        _journal = journal;
        _annunciator = annunciator;
        _time = time;
        _state = device.State;

        _conditions = Definitions.ToDictionary(d => d.Id, d => new AlarmCondition(d));

        _device.StateChanged += OnStateChanged;
        _device.TelemetryReceived += OnTelemetry;
        _arbiter.CommandTracked += OnCommandTracked;
    }

    public event Action? Changed;

    public IReadOnlyList<AlarmSnapshot> Snapshot()
        => _conditions.Values
            .Where(c => c.Latched)
            .OrderByDescending(c => c.Definition.Severity)
            .ThenByDescending(c => c.ActivatedAt ?? DateTimeOffset.MinValue)
            .Select(c => c.ToSnapshot())
            .ToArray();

    public AlarmSnapshot? Headline
    {
        get
        {
            // The one that most needs attention: annunciating over acknowledged, then by
            // severity, then newest.
            var pick = _conditions.Values
                .Where(c => c.Latched)
                .OrderByDescending(c => c.IsAnnunciating)
                .ThenByDescending(c => c.Definition.Severity)
                .ThenByDescending(c => c.ActivatedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            return pick?.ToSnapshot();
        }
    }

    public int AnnunciatingCount => _conditions.Values.Count(c => c.IsAnnunciating);

    public bool HasActiveAlarms => _conditions.Values.Any(c => c.Latched);

    public bool IsAudible => _state != ConnectionState.Disconnected && AnnunciatingCount > 0 && _time.GetUtcNow() >= _silencedUntil;

    public void Acknowledge(AlarmId id)
    {
        if (_conditions.TryGetValue(id, out var condition) && condition.Acknowledge(_time.GetUtcNow()))
        {
            _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
                $"Alarme reconhecido: {condition.Definition.Title}.");
            Recompute();
        }
    }

    public void AcknowledgeAll()
    {
        var now = _time.GetUtcNow();
        var any = false;
        foreach (var condition in _conditions.Values)
        {
            if (condition.Acknowledge(now))
            {
                any = true;
                _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
                    $"Alarme reconhecido: {condition.Definition.Title}.");
            }
        }

        if (any)
        {
            Recompute();
        }
    }

    public void Silence()
    {
        _silencedUntil = _time.GetUtcNow() + SilenceWindow;
        _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
            $"Áudio de alarme silenciado por {SilenceWindow.TotalMinutes:F0} min.");
        Recompute();
    }

    public void Poll()
    {
        var now = _time.GetUtcNow();
        var connected = _state == ConnectionState.Connected;
        var stale = IsStale(now);
        var changed = false;
        var newlyRaised = false;

        foreach (var (id, condition) in _conditions.Select(kv => (kv.Key, kv.Value)))
        {
            var (active, detail) = Condition(id, connected, stale);
            var transition = condition.Evaluate(active, detail, now);

            switch (transition)
            {
                case AlarmTransition.Raised:
                    newlyRaised = true;
                    changed = true;
                    _journal.Add(AuditSource.Alarm, ToAudit(condition.Definition.Severity),
                        $"Alarme: {condition.Definition.Title}.", condition.Detail);
                    break;
                case AlarmTransition.Cleared:
                    changed = true;
                    _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
                        $"Alarme normalizado: {condition.Definition.Title}.");
                    break;
                default:
                    break;
            }
        }

        // A fresh alarm re-sounds even through an active silence: a new fault must not be
        // hidden by a silence taken for an earlier one.
        if (newlyRaised)
        {
            _silencedUntil = DateTimeOffset.MinValue;
        }

        UpdateAudible();

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Recompute()
    {
        UpdateAudible();
        Changed?.Invoke();
    }

    private void UpdateAudible()
    {
        var audible = IsAudible;
        if (audible != _wasAudible)
        {
            _wasAudible = audible;
            _annunciator.SetSounding(audible);
        }
    }

    private bool IsStale(DateTimeOffset now)
    {
        if (_state != ConnectionState.Connected || _lastFrameAt is not { } last)
        {
            return false;
        }

        var budget = TimeSpan.FromMilliseconds(Math.Max(_settings.Current.Connection.DataDelayMs, 500) * 3);
        return now - last > budget;
    }

    private (bool Active, string Detail) Condition(AlarmId id, bool connected, bool stale) => id switch
    {
        AlarmId.LinkLost => (_state is ConnectionState.Faulted or ConnectionState.Reconnecting,
            $"Estado do enlace: {_state}."),

        AlarmId.ModuleOffline => (connected && _lastSnapshot is { SensorCommOk: false },
            "O ESP32 não está recebendo dados do módulo de sensores (SensorCommOK falso)."),

        AlarmId.FlowmeterOffline => (
            connected && _lastSnapshot is { FlowControlEnabled: true, FlowmeterOnline: false },
            "O controle de vazão está ativo, mas o fluxômetro reporta offline."),

        AlarmId.FrozenData => (stale,
            "Nenhum quadro de telemetria aceito por mais de três períodos de emissão."),

        AlarmId.SensorAbsent => (
            connected && !stale && _lastSnapshot is { } s && s.OxygenCalibrated <= SensorReadings.NotReceived,
            "A sonda de oxigênio está reportando o sentinela \"não recebido\"."),

        AlarmId.UnacknowledgedCommand => (_timedOut.Count > 0,
            $"Comando(s) sem aceitação do transporte: {string.Join(", ", _timedOut.Select(CommandActuators.Label))}."),

        _ => (false, ""),
    };

    private static AuditSeverity ToAudit(AlarmSeverity severity)
        => severity == AlarmSeverity.Critical ? AuditSeverity.Error : AuditSeverity.Warning;

    private void OnStateChanged(ConnectionStateChange change)
    {
        _state = change.State;

        if (change.Cause == ConnectionTransitionCause.UserDisconnect)
        {
            var resolvedAny = false;
            foreach (var condition in _conditions.Values)
            {
                resolvedAny |= condition.Resolve();
            }

            if (resolvedAny)
            {
                _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
                    "Alarmes encerrados pelo operador ao desconectar.");
            }
        }

        // A clean disconnect or a fresh connection resets the telemetry snapshot so a stale
        // module/flowmeter/sensor reading from before the gap cannot linger as a condition.
        if (change.State != ConnectionState.Connected)
        {
            _lastSnapshot = null;
            _lastFrameAt = null;
        }

        Poll();
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _lastFrameAt = _time.GetUtcNow();
        Poll();
    }

    private void OnCommandTracked(CommandLifecycleEntry entry)
    {
        switch (entry.Phase)
        {
            case CommandPhase.TimedOut:
                _timedOut.Add(entry.Actuator);
                break;
            case CommandPhase.TransportAccepted:
            case CommandPhase.TelemetryConfirmed:
                _timedOut.Remove(entry.Actuator);
                break;
            default:
                break;
        }

        Poll();
    }

    public void Dispose()
    {
        _device.StateChanged -= OnStateChanged;
        _device.TelemetryReceived -= OnTelemetry;
        _arbiter.CommandTracked -= OnCommandTracked;
    }
}
