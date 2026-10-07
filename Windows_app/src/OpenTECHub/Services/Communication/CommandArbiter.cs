using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>Where a dispatched command has reached in its lifecycle.</summary>
/// <remarks>
/// The stages are honest about what the wire can actually tell us. Every actuator
/// reaches <see cref="TransportAccepted"/> — the transport confirmed the write left the
/// PC. Only aeration reaches <see cref="TelemetryConfirmed"/>, because it is the one
/// actuator whose applied setpoint the firmware echoes back (<c>FlowSetpoint</c>, with
/// <c>FlowCommandAck</c> alongside). Temperature, agitation, oxygen, pressure and pH have
/// no setpoint echo, so they rest at <see cref="TransportAccepted"/> and say so rather
/// than claiming a confirmation the wire never gave.
/// </remarks>
public enum CommandPhase
{
    /// <summary>Buffered for sending; not yet written to the transport.</summary>
    Issued,

    /// <summary>The transport reported the frame was written.</summary>
    TransportAccepted,

    /// <summary>Telemetry echoed the commanded value back (aeration only).</summary>
    TelemetryConfirmed,

    /// <summary>Never accepted within the budget — the link was down or dropped.</summary>
    TimedOut,
}

/// <summary>The live lifecycle of the most recent command to one actuator.</summary>
public sealed record CommandLifecycleEntry(
    ActuatorId Actuator,
    CommandOwner Owner,
    CommandPhase Phase,
    DateTimeOffset IssuedAt,
    DateTimeOffset UpdatedAt,
    string ConfirmationChannel,
    string Summary);

/// <summary>The outcome of a dispatch attempt.</summary>
/// <param name="Accepted">True when every touched actuator was owned by the requester and the frame was sent.</param>
/// <param name="Refused">The actuators owned by someone else, when refused; empty otherwise.</param>
public sealed record CommandDispatchResult(
    bool Accepted,
    IReadOnlyList<ActuatorId> Refused,
    CommandOwner Requester)
{
    public static CommandDispatchResult Nothing(CommandOwner requester) => new(false, [], requester);
}

/// <summary>A refused command, for the audit journal.</summary>
public sealed record CommandRejection(
    CommandOwner Requester,
    IReadOnlyList<ActuatorConflict> Conflicts,
    string CommandJson);

/// <summary>One actuator a requester could not write, and who holds it.</summary>
public sealed record ActuatorConflict(ActuatorId Actuator, CommandOwner Owner);

/// <summary>An explicit, journalled change of who owns a set of actuators.</summary>
/// <param name="To">The new owner.</param>
/// <param name="Actuators">The actuators whose ownership changed.</param>
/// <param name="Reason">Why the transfer happened, for the journal.</param>
/// <param name="IsSafeAbort">True when the transfer was forced by a link/feedback loss.</param>
/// <param name="LastDesired">
/// The last known lifecycle of each affected actuator, so a new owner can initialise
/// from the current commanded state and transfer bumplessly.
/// </param>
public sealed record OwnershipTransfer(
    CommandOwner To,
    IReadOnlyList<ActuatorId> Actuators,
    string Reason,
    bool IsSafeAbort,
    IReadOnlyList<CommandLifecycleEntry> LastDesired);

/// <summary>
/// The single gate onto the wire: one command queue, one owner per actuator.
/// </summary>
/// <remarks>
/// <para>
/// Every command reaches the transport through <see cref="Dispatch"/>. A command is sent
/// only if the requester owns every actuator it touches; otherwise the whole frame is
/// refused — never partially applied — and journalled. Manual owns all actuators until
/// something explicitly <see cref="Claim"/>s them, and a link or feedback loss revokes
/// any non-Manual ownership back to Manual (<b>safe abort</b>).
/// </para>
/// <para>
/// The arbiter also implements <see cref="IDeviceService"/> so that nothing in the
/// application can reach <c>Send</c> without passing through it: the plain
/// <see cref="IDeviceService.Send"/> is exactly a Manual dispatch.
/// </para>
/// </remarks>
public interface ICommandArbiter
{
    /// <summary>Current owner of every actuator.</summary>
    IReadOnlyDictionary<ActuatorId, CommandOwner> Ownership { get; }

    /// <summary>The live per-actuator command lifecycle.</summary>
    IReadOnlyList<CommandLifecycleEntry> Lifecycle { get; }

    /// <summary>Who owns <paramref name="actuator"/> right now.</summary>
    CommandOwner OwnerOf(ActuatorId actuator);

    /// <summary>Sends <paramref name="command"/> if <paramref name="requester"/> owns everything it touches.</summary>
    CommandDispatchResult Dispatch(CommandOwner requester, OpenTECCommand command);

    /// <summary>
    /// The same ownership check, but the frame is sent on its own rather than merged.
    /// </summary>
    /// <remarks>
    /// Needed where merging would defeat the command: the Hub drops an external device's
    /// sub-commands once that device's routing flag is clear, so a stop and the disable
    /// that follows it must be two frames.
    /// </remarks>
    CommandDispatchResult DispatchSeparateFrame(CommandOwner requester, OpenTECCommand command)
        => Dispatch(requester, command);

    /// <summary>
    /// Dispatches a privileged safety stop frame, bypassing ownership conflicts, and optionally returns
    /// all actuators to Manual (raising OwnershipRevoked if requested).
    /// </summary>
    CommandDispatchResult DispatchSafety(OpenTECCommand command, string reason, bool returnToManual = true);

    /// <summary>
    /// The same safety dispatch, but the frame is sent on its own rather than merged.
    /// </summary>
    CommandDispatchResult DispatchSeparateSafetyFrame(OpenTECCommand command, string reason, bool returnToManual = true);

    /// <summary>Transfers a set of actuators to <paramref name="owner"/>, explicitly and journalled.</summary>
    OwnershipTransfer Claim(CommandOwner owner, IReadOnlyList<ActuatorId> actuators, string reason);

    /// <summary>Returns actuators held by <paramref name="owner"/> to Manual.</summary>
    OwnershipTransfer Release(CommandOwner owner, string reason);

    /// <summary>Returns every actuator to Manual — the operator taking the wire back.</summary>
    OwnershipTransfer ReturnToManual(string reason, bool isSafeAbort = false);

    /// <summary>Raised on an explicit ownership transfer.</summary>
    event Action<OwnershipTransfer>? OwnershipChanged;

    /// <summary>Raised when a command is refused because the requester does not own an actuator.</summary>
    event Action<CommandRejection>? CommandRejected;

    /// <summary>Raised whenever an actuator's command lifecycle advances.</summary>
    event Action<CommandLifecycleEntry>? CommandTracked;

    /// <summary>Raised when a link or feedback loss forced ownership back to Manual.</summary>
    event Action<OwnershipTransfer>? OwnershipRevoked;
}

/// <inheritdoc cref="ICommandArbiter"/>
public sealed partial class CommandArbiter : ICommandAuthorityArbiter, IDeviceService, IDisposable
{
    /// <summary>How close the echoed flow setpoint must be to count as confirmed, in L/min.</summary>
    private const double FlowConfirmToleranceLpm = 0.1;

    private const string AerationChannel = "eco de FlowSetpoint / FlowCommandAck";
    private const string NoEchoChannel = "sem eco (somente aceitação do transporte)";

    private readonly IDeviceService _inner;
    private readonly TimeProvider _time;
    private readonly ILogger<CommandArbiter> _log;
    private readonly TimeSpan _acceptTimeout;

    private readonly Lock _gate = new();
    private readonly Dictionary<ActuatorId, CommandOwner> _ownership = new();
    private readonly Dictionary<ActuatorId, CommandLifecycleEntry> _lifecycle = new();

    /// <summary>Pending flow confirmation target, kept out of the immutable entry.</summary>
    private double? _flowConfirmTarget;

    private bool _disposed;

    public CommandArbiter(
        IDeviceService inner,
        TimeProvider time,
        ILogger<CommandArbiter>? log = null,
        TimeSpan? acceptTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(time);

        _inner = inner;
        _time = time;
        _log = log ?? NullLogger<CommandArbiter>.Instance;
        // Four device emission periods at the field 2 s cadence: long enough that a late
        // frame is not a false timeout, short enough to notice a command that never left.
        _acceptTimeout = acceptTimeout ?? TimeSpan.FromSeconds(8);

        foreach (var actuator in CommandActuators.All)
        {
            _ownership[actuator] = CommandOwner.Manual;
        }

        _inner.StateChanged += OnInnerStateChanged;
        _inner.TelemetryReceived += OnInnerTelemetry;
        _inner.RawTelemetryReceived += OnInnerRawTelemetry;
        _inner.DeviceLogReceived += OnInnerDeviceLog;
        _inner.NodeDiagReceived += OnInnerNodeDiag;
        _inner.CommandSent += OnInnerCommandSent;
        _inner.SessionTimeZeroed += OnInnerSessionTimeZeroed;
    }

    // ── ICommandArbiter ──────────────────────────────────────────────────────

    public IReadOnlyDictionary<ActuatorId, CommandOwner> Ownership
    {
        get { lock (_gate) { return new Dictionary<ActuatorId, CommandOwner>(_ownership); } }
    }

    public IReadOnlyList<CommandLifecycleEntry> Lifecycle
    {
        get
        {
            lock (_gate)
            {
                return CommandActuators.All
                    .Where(_lifecycle.ContainsKey)
                    .Select(a => _lifecycle[a])
                    .ToArray();
            }
        }
    }

    public CommandOwner OwnerOf(ActuatorId actuator)
    {
        lock (_gate) { return _ownership.GetValueOrDefault(actuator, CommandOwner.Manual); }
    }

    public event Action<OwnershipTransfer>? OwnershipChanged;

    public event Action<CommandRejection>? CommandRejected;

    public event Action<CommandLifecycleEntry>? CommandTracked;

    public event Action<OwnershipTransfer>? OwnershipRevoked;

    public CommandDispatchResult Dispatch(CommandOwner requester, OpenTECCommand command)
        => Dispatch(requester, command, separateFrame: false);

    public CommandDispatchResult DispatchSeparateFrame(CommandOwner requester, OpenTECCommand command)
        => Dispatch(requester, command, separateFrame: true);

    private CommandDispatchResult Dispatch(CommandOwner requester, OpenTECCommand command, bool separateFrame,
        CommandAuthorityLease? authority = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = OpenTECCommand.Create().Merge(command);
        if (command.IsEmpty)
        {
            return CommandDispatchResult.Nothing(requester);
        }

        var actuators = CommandActuators.ActuatorsIn(command);

        List<CommandLifecycleEntry> issued = [];
        CommandRejection? rejection = null;

        lock (_gate)
        {
            var conflicts = actuators
                .Where(a => !CanDispatchUnderLock(requester, a, authority))
                .Select(a => new ActuatorConflict(a, _ownership.GetValueOrDefault(a, CommandOwner.Manual)))
                .ToArray();

            if (conflicts.Length > 0)
            {
                // Atomic: one owned-by-another actuator refuses the whole frame. A
                // partial send is worse than none — it leaves the reactor in a state
                // neither owner asked for.
                rejection = new CommandRejection(requester, conflicts, command.ToJson());
            }
            else
            {
                if (authority is not null) _drainedReservations.Remove(authority.ReservationId);
                var now = _time.GetUtcNow();

                foreach (var actuator in CommandActuators.All.Where(actuators.Contains))
                {
                    var isAeration = actuator == ActuatorId.Aeration;
                    var entry = new CommandLifecycleEntry(
                        actuator,
                        requester,
                        CommandPhase.Issued,
                        now,
                        now,
                        isAeration ? AerationChannel : NoEchoChannel,
                        Describe(actuator, command));
                    _lifecycle[actuator] = entry;
                    issued.Add(entry);

                    if (isAeration)
                    {
                        // Only an aeration command changes what we watch for; a later
                        // temperature dispatch must not clear a pending flow confirmation.
                        _flowConfirmTarget =
                            TryParseValue(command.GetRawValue(CommandKeys.FlowSetpoint), out var target)
                                ? target
                                : null;
                    }
                }
            }
        }

        if (rejection is not null)
        {
            _log.LogWarning(
                "Command from {Requester} refused: {Actuators} owned by another owner",
                requester, string.Join(", ", rejection.Conflicts.Select(c => c.Actuator)));
            CommandRejected?.Invoke(rejection);
            return new CommandDispatchResult(false, [.. rejection.Conflicts.Select(c => c.Actuator)], requester);
        }

        // Track before sending: the inner transport may raise CommandSent synchronously,
        // and the entry must already exist for that to advance it to TransportAccepted.
        foreach (var entry in issued)
        {
            CommandTracked?.Invoke(entry);
        }

        // Ownership may change during callbacks above. Recheck at the actual enqueue boundary
        // and serialize it with transfers: no old-owner frame may enter after a handoff.
        lock (_gate)
        {
            var lateConflicts = actuators.Where(a => !CanDispatchUnderLock(requester, a, authority)).ToArray();
            if (lateConflicts.Length > 0)
            {
                return new CommandDispatchResult(false, lateConflicts, requester);
            }
            RecordDesiredUnderLock(command);
            if (separateFrame) _inner.SendAfterCurrentFrame(command);
            else _inner.Send(command);
        }

        return new CommandDispatchResult(true, [], requester);
    }

    public CommandDispatchResult DispatchSafety(OpenTECCommand command, string reason, bool returnToManual = true)
        => DispatchSafetyInternal(command, reason, separateFrame: false, returnToManual);

    public CommandDispatchResult DispatchSeparateSafetyFrame(OpenTECCommand command, string reason, bool returnToManual = true)
        => DispatchSafetyInternal(command, reason, separateFrame: true, returnToManual);

    private CommandDispatchResult DispatchSafetyInternal(OpenTECCommand command, string reason, bool separateFrame, bool returnToManual)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = OpenTECCommand.Create().Merge(command);
        if (command.IsEmpty)
        {
            return CommandDispatchResult.Nothing(CommandOwner.Manual);
        }

        if (returnToManual)
        {
            ReturnToManual($"parada de segurança: {reason}", isSafeAbort: true);
        }

        var actuators = CommandActuators.ActuatorsIn(command);
        List<CommandLifecycleEntry> issued = [];

        lock (_gate)
        {
            RecordDesiredUnderLock(command);
            var now = _time.GetUtcNow();
            foreach (var actuator in CommandActuators.All.Where(actuators.Contains))
            {
                var isAeration = actuator == ActuatorId.Aeration;
                var entry = new CommandLifecycleEntry(
                    actuator,
                    CommandOwner.Manual,
                    CommandPhase.Issued,
                    now,
                    now,
                    isAeration ? AerationChannel : NoEchoChannel,
                    Describe(actuator, command));
                _lifecycle[actuator] = entry;
                issued.Add(entry);

                if (isAeration)
                {
                    _flowConfirmTarget =
                        TryParseValue(command.GetRawValue(CommandKeys.FlowSetpoint), out var target)
                            ? target
                            : null;
                }
            }
        }

        _log.LogInformation("Safety dispatch sent to wire for {Actuators} ({Reason})",
            string.Join(", ", actuators), reason);

        foreach (var entry in issued)
        {
            CommandTracked?.Invoke(entry);
        }

        lock (_gate) _inner.SendSafetyFrame(command);

        return new CommandDispatchResult(true, [], CommandOwner.Manual);
    }

    public OwnershipTransfer Claim(CommandOwner owner, IReadOnlyList<ActuatorId> actuators, string reason)
    {
        ArgumentNullException.ThrowIfNull(actuators);
        return Transfer(owner, actuators, reason, isSafeAbort: false);
    }

    public OwnershipTransfer Release(CommandOwner owner, string reason)
    {
        IReadOnlyList<ActuatorId> held;
        lock (_gate)
        {
            held = _ownership.Where(kv => kv.Value == owner).Select(kv => kv.Key).ToArray();
        }

        return Transfer(CommandOwner.Manual, held, reason, isSafeAbort: false);
    }

    public OwnershipTransfer ReturnToManual(string reason, bool isSafeAbort = false)
    {
        IReadOnlyList<ActuatorId> nonManual;
        lock (_gate)
        {
            nonManual = _ownership.Where(kv => kv.Value != CommandOwner.Manual).Select(kv => kv.Key).ToArray();
        }

        return Transfer(CommandOwner.Manual, nonManual, reason, isSafeAbort);
    }

    private OwnershipTransfer Transfer(
        CommandOwner to, IReadOnlyList<ActuatorId> actuators, string reason, bool isSafeAbort,
        CommandAuthorityLease? authority = null)
    {
        List<CommandLifecycleEntry> lastDesired = [];
        lock (_gate)
        {
            if (to != CommandOwner.Manual && actuators.Any(a => _reservedActuators.ContainsKey(a)) &&
                (authority is null || !IsCurrentUnderLock(authority) || actuators.Any(a => !authority.Resources.Contains(a))))
            {
                throw new InvalidOperationException("Atuadores reservados por outro bloco; transferência não autorizada.");
            }
            if (to == CommandOwner.Manual) RevokeReservationsUnderLock(actuators);
            foreach (var actuator in actuators)
            {
                _ownership[actuator] = to;
                if (_lifecycle.TryGetValue(actuator, out var entry))
                {
                    lastDesired.Add(entry);
                }
            }
        }

        var transfer = new OwnershipTransfer(to, actuators, reason, isSafeAbort, lastDesired);

        if (actuators.Count > 0)
        {
            _log.LogInformation("Ownership -> {Owner} for {Actuators} ({Reason})",
                to, string.Join(", ", actuators), reason);
        }

        OwnershipChanged?.Invoke(transfer);
        if (isSafeAbort)
        {
            OwnershipRevoked?.Invoke(transfer);
        }

        return transfer;
    }

    // ── Lifecycle advancement, from the inner device's events ────────────────

    private void OnInnerCommandSent(string json)
    {
        List<CommandLifecycleEntry> advanced = [];
        lock (_gate)
        {
            RecordAcceptedUnderLock(json);
            var now = _time.GetUtcNow();
            foreach (var actuator in ActuatorsInJson(json))
            {
                if (_lifecycle.TryGetValue(actuator, out var entry) && entry.Phase == CommandPhase.Issued && DesiredIsAcceptedUnderLock(actuator))
                {
                    var next = entry with { Phase = CommandPhase.TransportAccepted, UpdatedAt = now };
                    _lifecycle[actuator] = next;
                    advanced.Add(next);
                }
            }
        }

        foreach (var entry in advanced)
        {
            CommandTracked?.Invoke(entry);
        }

        CommandSent?.Invoke(json);
    }

    private void OnInnerTelemetry(SensorSnapshot snapshot)
    {
        List<CommandLifecycleEntry> advanced = [];
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            // Confirm aeration against the echoed setpoint.
            if (_flowConfirmTarget is { } target &&
                _lifecycle.TryGetValue(ActuatorId.Aeration, out var aeration) &&
                aeration.Phase == CommandPhase.TransportAccepted &&
                snapshot.FlowSetpoint >= 0 &&
                Math.Abs(snapshot.FlowSetpoint - target) <= FlowConfirmToleranceLpm)
            {
                var confirmed = aeration with { Phase = CommandPhase.TelemetryConfirmed, UpdatedAt = now };
                _lifecycle[ActuatorId.Aeration] = confirmed;
                _flowConfirmTarget = null;
                advanced.Add(confirmed);
            }

            // A frame is flowing, so anything still merely Issued should have been
            // accepted by now: if the budget has passed, the command never left.
            foreach (var actuator in CommandActuators.All)
            {
                if (_lifecycle.TryGetValue(actuator, out var entry) &&
                    entry.Phase == CommandPhase.Issued &&
                    now - entry.IssuedAt > _acceptTimeout)
                {
                    var timedOut = entry with { Phase = CommandPhase.TimedOut, UpdatedAt = now };
                    _lifecycle[actuator] = timedOut;
                    advanced.Add(timedOut);
                }
            }
        }

        foreach (var entry in advanced)
        {
            CommandTracked?.Invoke(entry);
        }

        TelemetryReceived?.Invoke(snapshot);
    }

    private void OnInnerStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            // Safe abort: revoke every non-Manual owner, and time out any command that
            // was still only Issued — the link that would have carried it is gone.
            IReadOnlyList<ActuatorId> nonManual;
            List<CommandLifecycleEntry> timedOut = [];
            lock (_gate)
            {
                nonManual = _ownership.Where(kv => kv.Value != CommandOwner.Manual).Select(kv => kv.Key).ToArray();

                var now = _time.GetUtcNow();
                foreach (var actuator in CommandActuators.All)
                {
                    if (_lifecycle.TryGetValue(actuator, out var entry) && entry.Phase == CommandPhase.Issued)
                    {
                        var next = entry with { Phase = CommandPhase.TimedOut, UpdatedAt = now };
                        _lifecycle[actuator] = next;
                        timedOut.Add(next);
                    }
                }
            }

            foreach (var entry in timedOut)
            {
                CommandTracked?.Invoke(entry);
            }

            if (nonManual.Count > 0)
            {
                Transfer(CommandOwner.Manual, nonManual,
                    $"aborto seguro: {DescribeLoss(change)}", isSafeAbort: true);
            }
        }

        StateChanged?.Invoke(change);
    }

    private void OnInnerRawTelemetry(string json) => RawTelemetryReceived?.Invoke(json);

    private void OnInnerDeviceLog(string text) => DeviceLogReceived?.Invoke(text);

    private void OnInnerNodeDiag(string json) => NodeDiagReceived?.Invoke(json);

    private void OnInnerSessionTimeZeroed(double offsetMinutes) => SessionTimeZeroed?.Invoke(offsetMinutes);

    // ── IDeviceService (forwarded to the inner transport) ────────────────────

    public ConnectionState State => _inner.State;

    public TransportMedium? Medium => _inner.Medium;

    public string Endpoint => _inner.Endpoint;

    public SensorSnapshot? Latest => _inner.Latest;

    public LinkDiagnostics Diagnostics => _inner.Diagnostics;

    public event Action<ConnectionStateChange>? StateChanged;
    public event Action<SensorSnapshot>? TelemetryReceived;
    public event Action<string>? RawTelemetryReceived;
    public event Action<string>? DeviceLogReceived;
    public event Action<string>? NodeDiagReceived;
    public event Action<string>? CommandSent;
    public event Action<double>? SessionTimeZeroed;

    public void Connect() => _inner.Connect();

    public void ConnectUsb(string portName) => _inner.ConnectUsb(portName);

    public void ConnectWiFi(string ipAddress) => _inner.ConnectWiFi(ipAddress);

    public void Disconnect() => _inner.Disconnect();

    /// <summary>A plain send is a Manual dispatch — the operator writing a setpoint.</summary>
    public void Send(OpenTECCommand command) => Dispatch(CommandOwner.Manual, command);

    public void SendAfterCurrentFrame(OpenTECCommand command)
        => DispatchSeparateFrame(CommandOwner.Manual, command);
    public void SendSafetyFrame(OpenTECCommand command)
        => DispatchSafety(command, "parada pelo serviço de dispositivo");

    /// <summary>Read-only system request: deliberately bypasses actuator ownership.</summary>
    public void RequestNodeDiag(string device) => _inner.RequestNodeDiag(device);

    public void ZeroSessionTime() => _inner.ZeroSessionTime();

    public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default)
        => _inner.DiscoverUsbPortAsync(cancellationToken);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<ActuatorId> ActuatorsInJson(string json)
    {
        // The exact frame the transport wrote back, which may merge several dispatches.
        // Parsing the keys is enough to find the actuators; values are not needed here.
        var actuators = new HashSet<ActuatorId>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (CommandActuators.ForKey(property.Name) is { } actuator)
                {
                    actuators.Add(actuator);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A frame we cannot parse cannot advance any lifecycle; leave them as-is.
        }

        return actuators;
    }

    private static string Describe(ActuatorId actuator, OpenTECCommand command)
    {
        var fragments = command.Keys
            .Where(key => CommandActuators.ForKey(key) == actuator)
            .Select(key => $"{key}={command.GetRawValue(key)}");
        return $"{CommandActuators.Label(actuator)}: {string.Join(", ", fragments)}";
    }

    private static bool TryParseValue(string? rawJson, out double value)
        => double.TryParse(
            (rawJson ?? "").Trim('"'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static string DescribeLoss(ConnectionStateChange change) => change.State switch
    {
        ConnectionState.Reconnecting => "reconectando",
        ConnectionState.Faulted => "falha de enlace",
        ConnectionState.Disconnected => "desconectado",
        _ => change.State.ToString(),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;
            RevokeReservationsUnderLock(CommandActuators.All);
        }
        _inner.StateChanged -= OnInnerStateChanged;
        _inner.TelemetryReceived -= OnInnerTelemetry;
        _inner.DeviceLogReceived -= OnInnerDeviceLog;
        _inner.NodeDiagReceived -= OnInnerNodeDiag;
        _inner.CommandSent -= OnInnerCommandSent;
        _inner.SessionTimeZeroed -= OnInnerSessionTimeZeroed;
    }
}
