using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Services.Alarms;

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
/// Latching means a momentary fault still raises: once the on-delay passes the alarm holds
/// even if the condition immediately clears. It then clears on its own once the condition
/// has stayed normal for the off-deadband — acknowledged or not — so a fault that has gone
/// away leaves the banner without needing a click. The occurrence is preserved in the event
/// journal either way. Time is injected, so every delay is deterministic under test.
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

            // Once the condition has been clear for the deadband the alarm clears itself,
            // acknowledged or not. A fault that has gone away should not keep the banner lit
            // waiting for a click — the occurrence still lives in the event journal, where
            // both the raise and the "normalizado" line are recorded, so nothing is lost.
            if (now - _clearingSince >= definition.OffDeadband)
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

    /// <summary>
    /// Records what an operator switch says about routing one external device.
    /// </summary>
    /// <remarks>
    /// The Hub's echo alone cannot tell a mismatch from a device the operator has simply
    /// switched off, so the alarm needs both halves. Pushed in rather than pulled, because
    /// the alarm kernel must not depend on the view-model layer.
    /// </remarks>
    void SetRoutingRequested(string device, bool requested);

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
        // Same debounce as the offline alarm it rides on, and deliberately no longer: the
        // whole point is to tell the operator that gas is still going in while nothing can
        // stop it, and every second of on-delay is a second of that going unannounced.
        new(AlarmId.UnsupervisedGasFlow, "Gás aberto sem supervisão", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.FrozenData, "Dados congelados", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.Zero),
        new(AlarmId.SensorAbsent, "Sensor ausente", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
        new(AlarmId.UnacknowledgedCommand, "Comando não confirmado", AlarmSeverity.Warning,
            TimeSpan.Zero, TimeSpan.Zero),
        // The engine already applies its own grace before it declares a hold, so this latches as
        // soon as it says so: by then the device has been silent for several telemetry periods.
        new(AlarmId.RecipeAwaitingDevice, "Receita aguardando dispositivo", AlarmSeverity.Warning,
            TimeSpan.Zero, TimeSpan.Zero),
        // The Hub has already applied its own presence window before it reports a node
        // absent, so these carry the same short debounce the flowmeter alarm uses: enough
        // to ride out one late frame, not enough to hide a real outage.
        new(AlarmId.BiomassOffline, "Absorbância offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.BiomassAcquisitionStalled, "Aquisição de biomassa interrompida", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.ExternalPumpOffline, "Bomba externa offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.DistanceSensorOffline, "Distância offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        new(AlarmId.FlaskAgitatorOffline, "Agitador de frasco offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        // Ten seconds, not two. The servo's presence window on the Hub is 6 s and the
        // aggregate frame carries it at the dataDelay of 2 s, so a node that misses one
        // push can take eight seconds to be reported absent through no fault of its own.
        // Two seconds here would fire on that.
        new(AlarmId.ServoDriveOffline, "Servo drive offline", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)),
        // No on-delay: the drive has already decided. Debouncing a fault code would only
        // delay telling the operator something the machine already knows.
        new(AlarmId.ServoDriveAlarm, "Alarme do servo drive", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.FromSeconds(2)),
        // Longer on-delay: this compares two persisted stores, and the app's own enable
        // command needs a telemetry round trip before the Hub's echo can agree with it.
        // A tighter window would fire on every legitimate toggle.
        new(AlarmId.DeviceRoutingMismatch, "Hub e aplicativo divergem sobre um dispositivo", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
        // Three seconds (plan §3.3): the one-frame switch the runners send can echo the new
        // setpoint one frame before the new pair, and a legitimate transition must not ring.
        new(AlarmId.GasDeadEnd, "Gás sem destino", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)),
        new(AlarmId.GasBothOpen, "A e B/C abertas", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)),
        new(AlarmId.ExternalBathOffline, "Banho externo offline", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathReactorPvInvalid, "PV do reator inválida", AlarmSeverity.Critical,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathCommandTimeout, "Comando do banho não confirmado", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathSequenceFault, "Falha na sequência do banho", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathSetpointMismatch, "Setpoint do banho divergente", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathCascadeSaturated, "Cascata do banho saturada", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)),
        new(AlarmId.ExternalBathReactorDeviation, "Desvio térmico do reator", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)),
        new(AlarmId.ExternalBathPvLimit, "PV do banho fora do limite", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.FromSeconds(3)),
        new(AlarmId.ExternalBathImplausibleDelta, "Diferença banho–reator implausível", AlarmSeverity.Warning,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
        new(AlarmId.ExternalBathDualActuation, "Telemetria de rota térmica inconsistente", AlarmSeverity.Critical,
            TimeSpan.Zero, TimeSpan.FromSeconds(3)),
    ];

    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly ISettingsService _settings;
    private readonly IEventJournal _journal;
    private readonly IAlarmAnnunciator _annunciator;
    private readonly TimeProvider _time;

    /// <summary>
    /// The recipe engine, when one exists. A recipe holding for an unresponsive external device is
    /// an operational condition like any other, and the operator hears about it the same way.
    /// </summary>
    private readonly IRecipeEngine? _recipes;

    private readonly Dictionary<AlarmId, AlarmCondition> _conditions;
    private readonly HashSet<ActuatorId> _timedOut = [];

    /// <summary>
    /// What the operator's external-device switches currently say, keyed by device label.
    /// </summary>
    /// <remarks>
    /// Pushed in by whoever owns those switches rather than pulled from the view-models:
    /// the alarm kernel must not depend on the UI layer, and the Hub's echo alone cannot
    /// tell a mismatch from a device the operator simply has switched off.
    /// </remarks>
    private readonly Dictionary<string, bool> _routingRequested = [];

    private ConnectionState _state = ConnectionState.Disconnected;
    private SensorSnapshot? _lastSnapshot;
    private DateTimeOffset? _lastFrameAt;

    /// <summary>
    /// Whether the gas path was open in the last frame that actually carried flow state.
    /// </summary>
    /// <remarks>
    /// The Hub omits flow values once the node is absent and the parser blanks them, so by
    /// the time the flowmeter reads offline the app can no longer see what its valves are
    /// doing. The node is fail-in-place, so what it was doing is what it is still doing:
    /// this is the only evidence there is, and without holding it the unsupervised-gas
    /// alarm could never fire.
    /// </remarks>
    private bool _gasOpenWhenLastSeen;

    /// <summary>
    /// Whether the biomass sensor was actively acquiring fresh samples before becoming silent.
    /// Only trigger the silent-reboot / sampling-stalled alarm if the sensor was actively
    /// acquiring samples before becoming silent (B06).
    /// </summary>
    private bool _biomassAcquisitionActiveWhenLastSeen;
    private DateTimeOffset? _lastBiomassSampleAt;

    /// <summary>
    /// Silence between biomass samples that reads as a stalled acquisition (B06: the node
    /// reboots into IDLE after a brownout and keeps heartbeating, so it looks online).
    /// </summary>
    /// <remarks>
    /// 2.5 sampling periods, never below 65 s (2.5 x the 25 s default). The period comes from
    /// the node's own <c>BiomassProbePeriodMs</c> echo, so a slow assay does not trip it.
    /// </remarks>
    public TimeSpan BiomassStallTimeout { get; private set; } = DefaultBiomassStallTimeout;

    private static readonly TimeSpan DefaultBiomassStallTimeout = TimeSpan.FromSeconds(65);
    private DateTimeOffset _silencedUntil = DateTimeOffset.MinValue;
    private bool _wasAudible;

    public AlarmService(
        IDeviceService device,
        ICommandArbiter arbiter,
        ISettingsService settings,
        IEventJournal journal,
        IAlarmAnnunciator annunciator,
        TimeProvider time,
        IRecipeEngine? recipes = null)
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
        _recipes = recipes;
        _state = device.State;

        _conditions = Definitions.ToDictionary(d => d.Id, d => new AlarmCondition(d));

        _device.StateChanged += OnStateChanged;
        _device.TelemetryReceived += OnTelemetry;
        _arbiter.CommandTracked += OnCommandTracked;

        if (_recipes is not null)
        {
            _recipes.WaitingChanged += Poll;
        }
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
            var (active, detail) = Condition(id, connected, stale, now);
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

    private (bool Active, string Detail) Condition(AlarmId id, bool connected, bool stale, DateTimeOffset now) => id switch
    {
        AlarmId.LinkLost => (_state is ConnectionState.Faulted or ConnectionState.Reconnecting,
            $"Estado do enlace: {_state}."),

        AlarmId.ModuleOffline => (connected && _lastSnapshot is { SensorCommOk: false },
            "O ESP32 não está recebendo dados do módulo de sensores (SensorCommOK falso)."),

        AlarmId.FlowmeterOffline => (
            connected && RoutingRequested(DeviceNames.Routing.Airflow) && _lastSnapshot is { FlowmeterOnline: false },
            "Fluxômetro Desconectado da Central: o Hub está acessível, mas perdeu o enlace interno."),

        // Not gated on the routing switch, unlike the offline alarm above. Gas physically
        // entering the reactor is worth announcing even if the operator has since turned
        // the routing off — turning a switch off in the app does not close a valve on a
        // node that is no longer listening.
        AlarmId.UnsupervisedGasFlow => (
            connected && _gasOpenWhenLastSeen && _lastSnapshot is { FlowmeterOnline: false },
            "O fluxômetro perdeu o enlace com a rota de gás aberta. O nó é fail-in-place: " +
            "mantém válvulas e setpoint no último estado, então o gás continua entrando no " +
            "reator e nem o app nem o Hub conseguem fechá-lo. Feche a vazão no local ou " +
            "restabeleça o enlace."),

        AlarmId.FrozenData => (stale,
            "Nenhum quadro de telemetria aceito por mais de três períodos de emissão."),

        AlarmId.SensorAbsent => (
            connected && !stale && _lastSnapshot is { } s && s.OxygenCalibrated <= SensorReadings.NotReceived,
            "A sonda de oxigênio está reportando o sentinela \"não recebido\"."),

        AlarmId.UnacknowledgedCommand => (_timedOut.Count > 0,
            $"Comando(s) sem aceitação do transporte: {string.Join(", ", _timedOut.Select(CommandActuators.Label))}."),

        AlarmId.RecipeAwaitingDevice => (
            _recipes?.Waiting is not null,
            _recipes?.Waiting is { } wait
                ? $"A receita está parada no bloco '{wait.NodeId}': {wait.Device} — {wait.Detail} " +
                  "Pule o bloco ou pare a receita."
                : ""),

        // Each of these is qualified twice: by the operator's own switch in Controle (an
        // external device the operator has not enabled must never alarm, even if the Hub is
        // still routing it from a flag persisted across a reboot) and by the Hub's routing
        // echo. A Hub that does not publish the echo leaves the flag null, and the alarm
        // stays silent rather than guessing — the app cannot tell an unflashed Hub from a
        // failed node. When the Hub routes a device the operator switched off, the
        // routing-mismatch alarm carries it instead of a spurious offline.
        AlarmId.BiomassOffline => (
            connected && RoutingRequested(DeviceNames.Routing.Absorbance) &&
            _lastSnapshot is { BiomassCommEnabled: true, BiomassOnline: false, HasBiomassTelemetry: true },
            "O sensor de biomassa não está respondendo à Central, embora esteja habilitado no Hub."),

        AlarmId.BiomassAcquisitionStalled => (
            connected && RoutingRequested(DeviceNames.Routing.Absorbance) &&
            _biomassAcquisitionActiveWhenLastSeen &&
            _lastSnapshot is { BiomassCommEnabled: true, BiomassOnline: true } &&
            IsBiomassAcquisitionStalled(now),
            "Aquisição de biomassa interrompida: o sensor está online mas parou de enviar amostras (possível reinício em repouso pós-queda)."),

        AlarmId.ExternalPumpOffline => (
            connected && RoutingRequested(DeviceNames.Routing.ExternalPump) &&
            _lastSnapshot is { PumpCommEnabled: true, PumpOnline: false, HasPumpTelemetry: true },
            "A bomba externa não está respondendo à Central. A dosagem em curso não pode ser confirmada."),

        AlarmId.DistanceSensorOffline => (
            connected && RoutingRequested(DeviceNames.Routing.Distance) &&
            _lastSnapshot is { DistanceCommEnabled: true, DistanceOnline: false, HasDistanceTelemetry: true },
            "O sensor de distância não está respondendo à Central; o controle automático de espuma está sem leitura."),

        AlarmId.FlaskAgitatorOffline => (
            connected && RoutingRequested(DeviceNames.Routing.FlaskAgitator) &&
            _lastSnapshot is { HasAgitatorTelemetry: true, AgitatorOnline: false },
            "O agitador de frasco não está respondendo à Central."),

        AlarmId.ServoDriveOffline => (
            connected && _lastSnapshot is { ServoCommEnabled: true, ServoOnline: false, HasServoTelemetry: true },
            "O nó do servo drive não está respondendo à Central, embora esteja habilitado no Hub."),

        // Either signal is enough. The state says the drive stopped; the code says why, and
        // the bench has seen a code arrive a frame before the state caught up.
        AlarmId.ServoDriveAlarm => (
            connected && _lastSnapshot is { HasServoSample: true } servo &&
            (servo.ServoState == 3 || servo.ServoAlarm > 0),
            _lastSnapshot is { ServoAlarm: > 0 } code
                ? $"O servo drive está em alarme: AL{code.ServoAlarm:X3}. Consulte o código no manual do ASDA-B2."
                : "O servo drive está em estado de alarme."),

        AlarmId.DeviceRoutingMismatch => RoutingMismatch(connected),

        AlarmId.GasDeadEnd => (
            connected && ObservedRoute() == ObservedGasRoute.DeadEnd,
            "O fluxômetro ecoa um setpoint acima de zero com A e B/C fechadas: a linha não tem saída e a " +
            "pressão sobe até o fechamento. Ação sugerida: fechar a linha (parada segura da vazão) ou " +
            "acionar a entrada 1 ou 2 em Controle › Vazão de Ar."),

        AlarmId.GasBothOpen => (
            connected && ObservedRoute() == ObservedGasRoute.BothOpen,
            "O fluxômetro ecoa as entradas 1 e 2 acionadas ao mesmo tempo: A e B + C abertas juntas, o ar " +
            "se divide entre o reator e a purga. Nenhum ensaio comanda isso; escolha uma entrada em " +
            "Controle › Vazão de Ar."),

        AlarmId.ExternalBathOffline => (
            connected && BathRouted() && _lastSnapshot is { HasBathTelemetry: true, BathCommEnabled: true, BathOnline: false },
            "O banho externo está selecionado e habilitado, mas o Hub não o vê online há pelo menos 10 s."),

        // The Hub's own validity/age flags (10.6) decide, exactly like the PI does; the
        // value range is only a fallback for Hubs that do not publish them.
        AlarmId.ExternalBathReactorPvInvalid => (
            connected && BathCascadeActive() && _lastSnapshot is { HasBathTelemetry: true } s &&
            (stale || (s.TemperatureValid is { } valid
                ? !valid
                : !double.IsFinite(s.Temperature) || s.Temperature <= 0.0 || s.Temperature >= 100.0)),
            "A temperatura real do reator (Tempval) está inválida ou sem atualização; a cascata fica pausada."),

        // No ACK for a pending command (10 s on-delay) or a sequence running close to the
        // Hub's 300 s completion fault. A normal hold takes tens of seconds and stays silent.
        AlarmId.ExternalBathCommandTimeout => (
            connected && BathRouted() && _lastSnapshot is { BathOnline: true } s &&
            (s.BathCommandPending == true || s.BathCommandCompletionAgeMs >= 240_000),
            "Um comando do banho está sem confirmação do nó há mais de 10 s, ou a execução passou de 240 s."),

        // Only while the cascade is active: after a stop the node is legitimately aborted/manual.
        AlarmId.ExternalBathSequenceFault => (
            connected && BathRouted() && _lastSnapshot is { } s &&
            (s.BathCascadeState.Equals("fault", StringComparison.OrdinalIgnoreCase) ||
             (BathCascadeActive() &&
              (s.BathState.Equals("error", StringComparison.OrdinalIgnoreCase) ||
               s.BathGuard.Equals("suspended", StringComparison.OrdinalIgnoreCase)))),
            _lastSnapshot is { BathCascadeFaultReason.Length: > 0 } f
                ? $"Cascata do banho em falha: {BathReasons.Describe(f.BathCascadeFaultReason)}. Corrija e use Reset falha."
                : "O C404 reportou erro ou guarda suspensa. Verifique o banho antes de retomar."),

        // The node's own target versus what its display shows, once idle; and the Hub's
        // verdicts for a refused command or a target changed from outside.
        AlarmId.ExternalBathSetpointMismatch => (
            connected && BathRouted() && _lastSnapshot is { } s &&
            (s.BathCascadeFaultReason.StartsWith("node_rejected", StringComparison.Ordinal) ||
             s.BathCascadeFaultReason == "target_override" ||
             (CascadeIsControlling(s) && !s.BathCommandCompletionPending &&
              s.BathState.Equals("done", StringComparison.OrdinalIgnoreCase) &&
              s.BathTarget is { } target && double.IsFinite(target) &&
              s.BathDisplaySp is { } display && double.IsFinite(display) &&
              Math.Abs(target - display) > 0.15)),
            "O SP mostrado pelo C404 não acompanha o alvo, ou o banho recusou/alterou o comando da cascata."),

        AlarmId.ExternalBathCascadeSaturated => (
            connected && BathRouted() && _lastSnapshot is { BathCascadeSaturated: true } s &&
            CascadeIsControlling(s),
            "A saída da cascata atingiu um limite; a temperatura do reator pode não acompanhar a referência."),

        AlarmId.ExternalBathReactorDeviation => (
            connected && BathRouted() && _lastSnapshot is { BathCascadeError: { } error } s &&
            CascadeIsControlling(s) &&
            double.IsFinite(error) && Math.Abs(error) > 2.0,
            "O erro entre Tempval e o setpoint do reator permanece acima de 2 °C."),

        AlarmId.ExternalBathPvLimit => (
            connected && BathRouted() && _lastSnapshot is { BathPv: { } pv } &&
            (!double.IsFinite(pv) || pv < 0.0 || pv > 100.0),
            "A temperatura reportada pelo C404 está fora do limite absoluto de 0–100 °C."),

        AlarmId.ExternalBathImplausibleDelta => (
            connected && BathRouted() && _lastSnapshot is { BathPv: { } bath, Temperature: var reactor } &&
            double.IsFinite(bath) && double.IsFinite(reactor) && Math.Abs(bath - reactor) > 20.0,
            "A diferença entre a temperatura do banho e a temperatura real do reator excede 20 °C."),

        // Evidence published by Hub 10.6: the last command to the original module left it
        // enabled while the external route is selected.
        AlarmId.ExternalBathDualActuation => (
            connected && _lastSnapshot is { HasBathTelemetry: true, TempControlViaBath: true, TempModuleActuatorOn: true },
            "A via externa está selecionada, mas o Hub indica que a placa original ainda recebeu um setpoint ativo (B ≠ 100B)."),

        _ => (false, ""),
    };

    /// <summary>
    /// The Hub is routing a device the operator switched off, or vice versa.
    /// </summary>
    /// <remarks>
    /// The second direction is the dangerous one: with the Hub not routing, every
    /// blank/start/threshold or pump-profile frame is dropped by
    /// <c>if (cmdFound &amp;&amp; commOn)</c> without any reply, so the app would report
    /// success for commands that never reached the node.
    /// </remarks>
    private (bool Active, string Detail) RoutingMismatch(bool connected)
    {
        if (!connected || _lastSnapshot is not { } snapshot)
        {
            return (false, "");
        }

        List<string> conflicts = [];

        // The flowmeter has the same divergence, and worse consequences: FlowControlEnabled is
        // what the Fluxômetro offline alarm is conditioned on, so a silent disagreement disables
        // that alarm along with the loop. Only compared once the Hub has spoken about the
        // flowmeter at all, so a pre-connection frame does not read as a conflict.
        Check(DeviceNames.Routing.Airflow, snapshot.FlowmeterOnline ? snapshot.FlowControlEnabled : null);

        Check(DeviceNames.Routing.Absorbance, snapshot.BiomassCommEnabled);
        Check(DeviceNames.Routing.ExternalPump, snapshot.PumpCommEnabled);
        Check(DeviceNames.Routing.Distance, snapshot.DistanceCommEnabled);
        Check(DeviceNames.Routing.ServoDrive, snapshot.ServoCommEnabled);
        var bathRoute = snapshot.TempControlViaBath is { } viaBath && snapshot.BathCommEnabled is { } bathComm
            ? viaBath && bathComm
            : (bool?)null;
        Check(DeviceNames.Routing.ExternalBath, bathRoute);

        // The banner shows one line, so the detail leads with the consequence and names the
        // devices plainly. The earlier wording spelled out the Hub's state per device and was
        // truncated before it reached the part that mattered.
        return conflicts.Count == 0
            ? (false, "")
            : (true,
               $"Comandos podem estar sendo descartados sem aviso: {string.Join(", ", conflicts)}. " +
               "Reative o dispositivo no painel Controle.");

        void Check(string device, bool? hubSays)
        {
            if (hubSays is { } routed &&
                _routingRequested.TryGetValue(device, out var requested) &&
                routed != requested)
            {
                conflicts.Add(device);
            }
        }
    }

    /// <summary>
    /// Records what an operator switch says, so the routing mismatch has both halves.
    /// </summary>
    /// <param name="device">The device label, matching the one used in the condition above.</param>
    /// <param name="requested">True when the operator has the device switched on.</param>
    public void SetRoutingRequested(string device, bool requested)
    {
        _routingRequested[device] = requested;
        if (string.Equals(device, DeviceNames.Routing.Absorbance, StringComparison.OrdinalIgnoreCase) && !requested)
        {
            _biomassAcquisitionActiveWhenLastSeen = false;
            _lastBiomassSampleAt = null;
        }
    }

    /// <summary>
    /// Whether the operator has this external device switched on in Controle.
    /// </summary>
    /// <remarks>
    /// Gates the external-device offline alarms: a device the operator has not enabled must
    /// not alarm, even when the Hub is still routing it from a flag persisted across a reboot.
    /// Unknown means "not requested" — the switch is pushed at startup, so a missing entry is
    /// the safe, silent default rather than a guess.
    /// </remarks>
    private bool RoutingRequested(string device)
        => _routingRequested.TryGetValue(device, out var requested) && requested;

    /// <summary>The Hub itself routes temperature to the bath with communication on.</summary>
    /// <remarks>
    /// Deliberately the Hub's word, not the operator's saved preference: a Hub left on the
    /// external route (NVS, another client) must still be supervised.
    /// </remarks>
    private bool BathRouted()
        => _lastSnapshot is { TempControlViaBath: true, BathCommEnabled: true };

    /// <summary>The cascade has a reactor reference in this session (null on older Hubs = assume yes).</summary>
    private bool BathCascadeActive()
        => BathRouted() && _lastSnapshot is { BathCascadeActive: not false };

    private static bool CascadeIsControlling(SensorSnapshot snapshot)
        => snapshot.BathCascadeState.Equals("controlling", StringComparison.OrdinalIgnoreCase) ||
           snapshot.BathCascadeState.Equals("actuator_busy", StringComparison.OrdinalIgnoreCase);

    private static AuditSeverity ToAudit(AlarmSeverity severity)
        => severity == AlarmSeverity.Critical ? AuditSeverity.Error : AuditSeverity.Warning;

    private void OnStateChanged(ConnectionStateChange change)
    {
        _state = change.State;

        var resolved = change.Cause == ConnectionTransitionCause.UserDisconnect &&
                       _conditions[AlarmId.LinkLost].Resolve();
        if (resolved)
        {
            _journal.Add(AuditSource.Alarm, AuditSeverity.Information,
                "Alarme encerrado pelo operador ao desconectar: Link perdido.");
        }

        // A clean disconnect or a fresh connection resets the telemetry snapshot so a stale
        // module/flowmeter/sensor reading from before the gap cannot linger as a condition.
        if (change.State != ConnectionState.Connected)
        {
            _lastSnapshot = null;
            _lastFrameAt = null;
            // Same policy, for the same reason: what the valves were doing before a gap is
            // not evidence of what they are doing after it. The first frame from a live
            // flowmeter re-establishes this, and until then the app knows nothing.
            _gasOpenWhenLastSeen = false;
            _journalledRoute = null;
            _biomassAcquisitionActiveWhenLastSeen = false;
            _lastBiomassSampleAt = null;
        }

        Poll();

        // Resolve clears the latch behind the state machine's back, so Poll finds nothing
        // to report and would not raise Changed. Without this the banner keeps showing an
        // alarm that no longer exists — and Reconhecer cannot dismiss it either, because
        // there is no longer anything latched for it to acknowledge.
        if (resolved)
        {
            Changed?.Invoke();
        }
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _lastFrameAt = _time.GetUtcNow();
        if (snapshot.FlowmeterOnline)
        {
            _gasOpenWhenLastSeen = GasPathIsOpen(snapshot);
            JournalRouteChange(snapshot);
        }

        if (snapshot.BiomassProbePeriodMs is > 0 and var probeMs)
        {
            var fromEcho = TimeSpan.FromMilliseconds(probeMs * 2.5);
            BiomassStallTimeout = fromEcho > DefaultBiomassStallTimeout ? fromEcho : DefaultBiomassStallTimeout;
        }

        if (snapshot.BiomassOnline && snapshot.BiomassCommEnabled == true && RoutingRequested(DeviceNames.Routing.Absorbance))
        {
            if (SensorReadings.IsBiomassAbsorbanceMeasured(snapshot.BiomassAbsorbance))
            {
                _biomassAcquisitionActiveWhenLastSeen = true;
                _lastBiomassSampleAt = _time.GetUtcNow();
            }
        }
        else if (snapshot.BiomassCommEnabled == false || !RoutingRequested(DeviceNames.Routing.Absorbance))
        {
            _biomassAcquisitionActiveWhenLastSeen = false;
            _lastBiomassSampleAt = null;
        }

        Poll();
    }

    /// <summary>The A/B/C wiring in force, read at each frame so a Configurações change applies at once.</summary>
    private GasRigConfiguration Rig => _settings.Current.GasRig.ToConfiguration();

    /// <summary>What the last live flowmeter frame says the gas is doing, in the rig's terms; null without one.</summary>
    private ObservedGasRoute? ObservedRoute()
    {
        if (_lastSnapshot is not { FlowmeterOnline: true } s || s.FlowValve1 < 0 || s.FlowValve2 < 0)
        {
            return null;
        }

        var setpoint = double.IsFinite(s.FlowSetpoint) && s.FlowSetpoint > 0 ? s.FlowSetpoint : 0.0;
        return GasRouting.Interpret(s.FlowValve1 != 0, s.FlowValve2 != 0, setpoint, Rig);
    }

    private ObservedGasRoute? _journalledRoute;

    /// <summary>
    /// One journal line per change of the observed route — "Gás: Reator (A) → Descarga + N₂ (B/C)".
    /// The equipment's word, not the app's: it is the echo that moved, whoever asked for it.
    /// </summary>
    private void JournalRouteChange(SensorSnapshot snapshot)
    {
        var route = ObservedRoute();
        if (route is not { } observed)
        {
            return;
        }

        if (_journalledRoute is { } previous)
        {
            if (previous != observed)
            {
                _journal.Add(
                    AuditSource.Equipment,
                    GasRouting.IsNominal(observed) ? AuditSeverity.Information : AuditSeverity.Warning,
                    $"Gás: {GasRouting.Describe(previous)} → {GasRouting.Describe(observed)}.",
                    $"{GasRouting.DescribeWire(snapshot.FlowValve1 != 0, snapshot.FlowValve2 != 0, Rig)} · setpoint {snapshot.FlowSetpoint:0.##} L/min");
            }
        }

        _journalledRoute = observed;
    }

    /// <summary>
    /// Whether this frame shows gas actually reaching the reactor.
    /// </summary>
    /// <remarks>
    /// The measured rate is used here, unlike the power assay's preflight gate. There the
    /// question is "did someone leave a valve open", which only commanded state can answer;
    /// here it is "is gas going in right now", and a sensor reading well above its residual
    /// is direct evidence of that even if the valve echo has not caught up.
    /// </remarks>
    private static bool GasPathIsOpen(SensorSnapshot s)
    {
        const double flowToleranceLpm = 0.1;
        var measuring = double.IsFinite(s.FlowRate) && s.FlowRate > flowToleranceLpm;
        var commanded = double.IsFinite(s.FlowSetpoint) && s.FlowSetpoint > flowToleranceLpm;
        var routed = s.FlowValveMain == 0 && (s.FlowValve1 == 1 || s.FlowValve2 == 1);
        return measuring || commanded || routed;
    }

    private bool IsBiomassAcquisitionStalled(DateTimeOffset now)
    {
        if (!_biomassAcquisitionActiveWhenLastSeen || _lastBiomassSampleAt is not { } lastSample)
        {
            return false;
        }

        return now - lastSample > BiomassStallTimeout;
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

        // An operator stop ends the acquisition on purpose; silence after it is not a stall.
        if (entry.Actuator == ActuatorId.Biomass &&
            entry.Summary.Contains(CommandKeys.BiomassStop, StringComparison.OrdinalIgnoreCase))
        {
            _biomassAcquisitionActiveWhenLastSeen = false;
            _lastBiomassSampleAt = null;
        }

        Poll();
    }

    public void Dispose()
    {
        _device.StateChanged -= OnStateChanged;
        _device.TelemetryReceived -= OnTelemetry;
        _arbiter.CommandTracked -= OnCommandTracked;

        if (_recipes is not null)
        {
            _recipes.WaitingChanged -= Poll;
        }
    }
}
