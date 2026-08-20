using System.Text.Json;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Telemetry;

/// <summary>The eight audit sources defined by the operator-interface contract.</summary>
public enum AuditSource
{
    Equipment,
    Command,
    Connection,
    Setpoint,
    Alarm,
    Recipe,
    Calibration,
    Application,
}

public enum AuditSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>One immutable, filterable event shown on the Eventos page.</summary>
public sealed record AuditEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    AuditSource Source,
    AuditSeverity Severity,
    string Message,
    string Detail);

public interface IEventJournal : IDisposable
{
    event Action<AuditEvent>? EntryAdded;

    IReadOnlyList<AuditEvent> Snapshot();

    void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null);
}

/// <summary>
/// Bounded in-memory audit journal. It records app-known facts, including exact command
/// frames, so Eventos remains useful over Wi-Fi where serial-only ESP32 log lines vanish.
/// </summary>
public sealed class EventJournal : IEventJournal
{
    private const int Capacity = 5000;

    private static readonly IReadOnlyDictionary<string, string> SetpointNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CommandKeys.TempSetpoint] = "temperatura",
            [CommandKeys.MotorSetpoint] = "agitação",
            [CommandKeys.OxygenMonitor] = "oxigênio",
            [CommandKeys.FlowSetpoint] = "vazão",
            [CommandKeys.PressureReference] = "pressão",
            [CommandKeys.PHSetpoint] = "pH",
        };

    private readonly Lock _gate = new();
    private readonly List<AuditEvent> _entries = [];
    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly ISettingsService _settings;

    private AppSettings _previousSettings;
    private long _sequence;

    public EventJournal(IDeviceService device, ICommandArbiter arbiter, ISettingsService settings)
    {
        _device = device;
        _arbiter = arbiter;
        _settings = settings;
        _previousSettings = settings.Current;

        device.DeviceLogReceived += OnDeviceLogReceived;
        device.CommandSent += OnCommandSent;
        device.StateChanged += OnStateChanged;
        device.SessionTimeZeroed += OnSessionTimeZeroed;
        arbiter.OwnershipChanged += OnOwnershipChanged;
        arbiter.OwnershipRevoked += OnOwnershipRevoked;
        arbiter.CommandRejected += OnCommandRejected;
        arbiter.CommandTracked += OnCommandTracked;
        settings.Changed += OnSettingsChanged;

        Add(AuditSource.Application, AuditSeverity.Information, "Aplicação iniciada.");
    }

    public event Action<AuditEvent>? EntryAdded;

    public IReadOnlyList<AuditEvent> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    public void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new AuditEvent(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.Now,
            source,
            severity,
            message,
            detail ?? "");

        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }

        EntryAdded?.Invoke(entry);
    }

    private void OnDeviceLogReceived(string line)
    {
        var severity = line.Contains("ERRO", StringComparison.OrdinalIgnoreCase)
            ? AuditSeverity.Error
            : line.Contains("AVISO", StringComparison.OrdinalIgnoreCase)
                ? AuditSeverity.Warning
                : AuditSeverity.Information;

        Add(AuditSource.Equipment, severity, line, line);
    }

    private void OnCommandSent(string json)
    {
        Add(
            AuditSource.Command,
            AuditSeverity.Information,
            "Comando transmitido ao equipamento.",
            json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var changed = document.RootElement
                .EnumerateObject()
                .Where(property => SetpointNames.ContainsKey(property.Name))
                .Select(property => $"{SetpointNames[property.Name]} = {property.Value.GetRawText()}")
                .ToArray();

            if (changed.Length > 0)
            {
                Add(
                    AuditSource.Setpoint,
                    AuditSeverity.Information,
                    $"Setpoint aplicado: {string.Join("; ", changed)}.",
                    json);
            }
        }
        catch (JsonException)
        {
            Add(
                AuditSource.Application,
                AuditSeverity.Warning,
                "O comando transmitido não pôde ser detalhado no histórico.",
                json);
        }
    }

    private void OnSessionTimeZeroed(double offsetMinutes)
        => Add(
            AuditSource.Application,
            AuditSeverity.Information,
            "Tempo da sessão zerado pelo operador.",
            $"Deslocamento local aplicado: {offsetMinutes.ToString("F2", System.Globalization.CultureInfo.CurrentCulture)} min. " +
            "O relógio do equipamento e as amostras já registradas não foram alterados.");

    private void OnOwnershipChanged(OwnershipTransfer transfer)
    {
        if (transfer.IsSafeAbort || transfer.Actuators.Count == 0)
        {
            // Safe aborts are journalled with alarm severity in OnOwnershipRevoked; a
            // no-op transfer is not worth a line.
            return;
        }

        var actuators = string.Join(", ", transfer.Actuators.Select(CommandActuators.Label));
        Add(
            AuditSource.Command,
            AuditSeverity.Information,
            $"Posse transferida para {OwnerLabel(transfer.To)}: {actuators}.",
            transfer.Reason);
    }

    private void OnOwnershipRevoked(OwnershipTransfer transfer)
    {
        var actuators = string.Join(", ", transfer.Actuators.Select(CommandActuators.Label));
        Add(
            AuditSource.Alarm,
            AuditSeverity.Warning,
            $"Aborto seguro: posse devolvida ao operador ({actuators}).",
            transfer.Reason);
    }

    private void OnCommandRejected(CommandRejection rejection)
    {
        var conflicts = string.Join("; ",
            rejection.Conflicts.Select(c => $"{CommandActuators.Label(c.Actuator)} pertence a {OwnerLabel(c.Owner)}"));
        Add(
            AuditSource.Command,
            AuditSeverity.Warning,
            $"Comando de {OwnerLabel(rejection.Requester)} recusado: {conflicts}.",
            rejection.CommandJson);
    }

    private void OnCommandTracked(CommandLifecycleEntry entry)
    {
        // Accepted/confirmed transitions are normal traffic and would drown the journal.
        // A timeout is the one that means something did not reach the reactor.
        if (entry.Phase != CommandPhase.TimedOut)
        {
            return;
        }

        Add(
            AuditSource.Command,
            AuditSeverity.Error,
            $"Comando de {CommandActuators.Label(entry.Actuator)} expirou sem aceitação do transporte.",
            entry.Summary);
    }

    private static string OwnerLabel(CommandOwner owner) => owner switch
    {
        CommandOwner.Automatic => "Automático",
        CommandOwner.Recipe => "Receita",
        _ => "Manual",
    };

    private void OnStateChanged(ConnectionStateChange change)
    {
        var severity = change.State is ConnectionState.Faulted
            ? AuditSeverity.Error
            : change.State is ConnectionState.Reconnecting
                ? AuditSeverity.Warning
                : AuditSeverity.Information;
        var medium = change.Medium?.ToString() ?? "sem meio";

        Add(
            AuditSource.Connection,
            severity,
            $"Conexão: {change.State} · {medium} · {change.Endpoint}.",
            change.Reason ?? "");
    }

    private void OnSettingsChanged(AppSettings current)
    {
        if (current.Calibration != _previousSettings.Calibration)
        {
            Add(
                AuditSource.Calibration,
                AuditSeverity.Information,
                "Coeficientes de calibração aplicados.",
                $"Antes: {_previousSettings.Calibration}{Environment.NewLine}Depois: {current.Calibration}");
        }

        if (current.Theme != _previousSettings.Theme)
        {
            Add(
                AuditSource.Application,
                AuditSeverity.Information,
                $"Tema alterado para {current.Theme}.");
        }

        _previousSettings = current;
    }

    public void Dispose()
    {
        _device.DeviceLogReceived -= OnDeviceLogReceived;
        _device.CommandSent -= OnCommandSent;
        _device.StateChanged -= OnStateChanged;
        _device.SessionTimeZeroed -= OnSessionTimeZeroed;
        _arbiter.OwnershipChanged -= OnOwnershipChanged;
        _arbiter.OwnershipRevoked -= OnOwnershipRevoked;
        _arbiter.CommandRejected -= OnCommandRejected;
        _arbiter.CommandTracked -= OnCommandTracked;
        _settings.Changed -= OnSettingsChanged;
    }
}
