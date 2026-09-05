using OpenTECHub.Protocol;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Events the servo card writes to the journal. None of these is an alarm.
/// </summary>
/// <remarks>
/// The servo command link carries no acknowledgement, so the journal is where the evidence
/// lives: a line saying a command was sent, and a separate line saying it was observed to
/// take effect. Months later those two entries are all a reader has.
/// <para>
/// The alarm conditions themselves live in <c>AlarmServiceTests</c>, beside the other
/// devices' - they share the service and its harness.
/// </para>
/// </remarks>
public sealed class ServoJournalTests
{
    /// <summary>Collects journal entries without the real journal's device dependencies.</summary>
    private sealed class RecordingJournal : IEventJournal
    {
        private readonly List<AuditEvent> _entries = [];

        public event Action<AuditEvent>? EntryAdded;

        public IReadOnlyList<AuditEvent> Snapshot() => _entries;

        public void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null)
        {
            var entry = new AuditEvent(
                _entries.Count, DateTimeOffset.UnixEpoch, source, severity, message, detail ?? "");
            _entries.Add(entry);
            EntryAdded?.Invoke(entry);
        }

        public void Dispose() { }
    }

    private static (ServoDriveViewModel Vm, RecordingDeviceService Device, RecordingJournal Journal) Build()
    {
        var device = new RecordingDeviceService();
        var journal = new RecordingJournal();
        return (new ServoDriveViewModel(device, journal: journal), device, journal);
    }

    private static SensorSnapshot Frame(bool online = true, double energyWh = 1.0) => new()
    {
        HasServoTelemetry = true,
        HasServoSample = online,
        ServoOnline = online,
        ServoCommEnabled = true,
        ServoRpm = 600.5,
        ServoEnergyWh = energyWh,
    };

    /// <summary>
    /// The first frame establishes a baseline rather than announcing an arrival.
    /// </summary>
    /// <remarks>
    /// The node did not just appear; the app did. Logging it would put a spurious arrival at
    /// the head of every session, and an operator reading the journal back would see a
    /// device event that never happened.
    /// </remarks>
    [Fact]
    public void The_first_frame_does_not_announce_the_node_arriving()
    {
        var (_, device, journal) = Build();

        device.PushTelemetry(Frame());

        Assert.DoesNotContain(
            journal.Snapshot(),
            e => e.Message.Contains("servo drive presente", StringComparison.Ordinal));
    }

    [Fact]
    public void Losing_and_regaining_the_node_is_journalled_once_each()
    {
        var (_, device, journal) = Build();

        device.PushTelemetry(Frame());
        device.PushTelemetry(Frame(online: false));
        device.PushTelemetry(Frame(online: false));
        device.PushTelemetry(Frame());

        var messages = journal.Snapshot().Select(e => e.Message).ToList();
        Assert.Single(messages, m => m.Contains("deixou de responder", StringComparison.Ordinal));
        Assert.Single(messages, m => m.Contains("presente", StringComparison.Ordinal));
    }

    [Fact]
    public void Requesting_the_energy_reset_is_journalled_with_what_confirms_it()
    {
        var (vm, device, journal) = Build();
        device.PushTelemetry(Frame());

        vm.ResetEnergyCommand.Execute(null);

        var entry = Assert.Single(
            journal.Snapshot(),
            e => e.Message.Contains("solicitada", StringComparison.Ordinal));
        Assert.Contains("não confirma comandos", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drop that confirms the reset is journalled separately from the request.
    /// </summary>
    /// <remarks>
    /// With no acknowledgement on this link, the two entries together are the only evidence
    /// a later reader has that a command was both sent and effective.
    /// </remarks>
    [Fact]
    public void The_observed_drop_is_journalled_as_a_confirmation()
    {
        var (vm, device, journal) = Build();
        device.PushTelemetry(Frame(energyWh: 1.5));

        vm.ResetEnergyCommand.Execute(null);
        device.PushTelemetry(Frame(energyWh: 1.5));
        device.PushTelemetry(Frame(energyWh: 0.0001));

        Assert.Single(journal.Snapshot(), e => e.Message.Contains("confirmada", StringComparison.Ordinal));
    }

    /// <summary>
    /// Energy dropping on its own is a node reboot, not a confirmation.
    /// </summary>
    /// <remarks>
    /// The accumulator restarts whenever the node does. Recording that as "reset confirmed"
    /// would put a confirmation in the record for a command nobody sent.
    /// </remarks>
    [Fact]
    public void A_drop_nobody_asked_for_is_not_reported_as_a_confirmation()
    {
        var (_, device, journal) = Build();

        device.PushTelemetry(Frame(energyWh: 12.5));
        device.PushTelemetry(Frame(energyWh: 0.0));

        Assert.DoesNotContain(journal.Snapshot(), e => e.Message.Contains("confirmada", StringComparison.Ordinal));
    }

    [Fact]
    public void The_confirmation_is_reported_once_and_not_on_every_later_frame()
    {
        var (vm, device, journal) = Build();
        device.PushTelemetry(Frame(energyWh: 1.5));

        vm.ResetEnergyCommand.Execute(null);
        device.PushTelemetry(Frame(energyWh: 0.0));
        device.PushTelemetry(Frame(energyWh: 0.0));
        device.PushTelemetry(Frame(energyWh: 0.001));

        Assert.Single(journal.Snapshot(), e => e.Message.Contains("confirmada", StringComparison.Ordinal));
    }
}
