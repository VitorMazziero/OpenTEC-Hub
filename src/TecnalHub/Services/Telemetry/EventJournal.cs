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
    private readonly ISettingsService _settings;

    private AppSettings _previousSettings;
    private long _sequence;

    public EventJournal(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _previousSettings = settings.Current;

        device.DeviceLogReceived += OnDeviceLogReceived;
        device.CommandSent += OnCommandSent;
        device.StateChanged += OnStateChanged;
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
        _settings.Changed -= OnSettingsChanged;
    }
}
