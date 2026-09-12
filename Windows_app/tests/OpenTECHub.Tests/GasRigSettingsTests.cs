using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Etapa 8 of the A/B/C plan: Configurações › Gás e válvulas persists which flowmeter input
/// drives A, refuses to change it while automation runs, journals the change, and the wiring
/// travels as provenance into files.
/// </summary>
public class GasRigSettingsTests
{
    private sealed class Journal : IEventJournal
    {
        public List<(AuditSource Source, AuditSeverity Severity, string Message)> Entries { get; } = [];
        public event Action<AuditEvent>? EntryAdded;
        public IReadOnlyList<AuditEvent> Snapshot() => [];
        public void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null)
        {
            Entries.Add((source, severity, message));
            EntryAdded?.Invoke(new AuditEvent(Entries.Count, DateTimeOffset.UnixEpoch, source, severity, message, detail ?? ""));
        }
        public void Dispose() { }
    }

    private sealed class Dialogs : IDialogService
    {
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => true;
        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => true;
        public bool PromptInput(string title, string message, out string response, string initialValue = "") { response = initialValue; return true; }
        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.StartPreserving;
    }

    /// <summary>Only the run state matters here.</summary>
    private sealed class Recipes(RecipeRunState state) : IRecipeEngine
    {
        public RecipeDeviceWait? Waiting => null;
        public RecipeRunState State => state;
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
        public CascadeTerms? CascadeTermsFor(string nodeId) => null;
#pragma warning disable CS0067
        public event Action<string>? NodeStateChanged;
        public event Action? StateChanged;
        public event Action? WaitingChanged;
        public event Action<RecipeLogEntry>? Logged;
#pragma warning restore CS0067
        public void Dispose() { }
    }

    private static (SettingsViewModel Vm, MemorySettingsService Settings, Journal Journal) Build(RecipeRunState recipes = RecipeRunState.Idle)
    {
        var settings = new MemorySettingsService(new AppSettings());
        var journal = new Journal();
        var vm = new SettingsViewModel(
            settings,
            new ThemeService(NullLogger<ThemeService>.Instance),
            new RecordingDeviceService(),
            new Dialogs(),
            recipes: new Recipes(recipes),
            journal: journal);
        return (vm, settings, journal);
    }

    [Fact]
    public void Default_is_A_on_input_2_and_the_section_exists()
    {
        var (vm, settings, _) = Build();
        Assert.Equal(GasInput.Input2, vm.GasAirInletInput);
        Assert.True(vm.IsGasRigDefault);
        Assert.Equal("B e C ligadas na entrada 1", vm.GasVentInputText);
        Assert.Equal("Arranjo: A na entrada 2 · B/C na entrada 1", vm.GasRigSummary);
        Assert.Contains(vm.Sections, s => s.Id == SettingsViewModel.GasRigSectionId && s.Label == "Gás e válvulas");
        Assert.Equal(GasInput.Input2, settings.Current.GasRig.AirInletInput);
    }

    [Fact]
    public void Changing_the_input_persists_journals_and_restore_brings_the_default_back()
    {
        var (vm, settings, journal) = Build();

        vm.GasAirInletInput = GasInput.Input1;

        Assert.Equal(GasInput.Input1, settings.Current.GasRig.AirInletInput);
        Assert.Equal("B e C ligadas na entrada 2", vm.GasVentInputText);
        Assert.False(vm.IsGasRigDefault);
        var line = Assert.Single(journal.Entries, e => e.Message.StartsWith("Arranjo de válvulas", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, line.Severity);
        Assert.Contains("A → entrada 1; B e C → entrada 2", line.Message, StringComparison.Ordinal);
        Assert.Contains("A na entrada 1", vm.StatusMessage, StringComparison.Ordinal);

        vm.RestoreGasRigDefaultCommand.Execute(null);
        Assert.Equal(GasInput.Input2, settings.Current.GasRig.AirInletInput);
        Assert.True(vm.IsGasRigDefault);
        Assert.Equal(2, journal.Entries.Count(e => e.Message.StartsWith("Arranjo de válvulas", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_wiring_cannot_change_while_automation_runs()
    {
        var (vm, settings, journal) = Build(RecipeRunState.Running);

        vm.GasAirInletInput = GasInput.Input1;

        Assert.Equal(GasInput.Input2, settings.Current.GasRig.AirInletInput);
        Assert.Equal(GasInput.Input2, vm.GasAirInletInput);
        Assert.Contains("receita em execução", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public void Provenance_reads_the_same_everywhere()
    {
        Assert.Equal("A=2 B/C=1", GasRigProvenance.Describe(new GasRigSettings()));
        Assert.Equal("A=1 B/C=2", GasRigProvenance.Describe(new GasRigConfiguration(GasInput.Input1)));
        Assert.Equal(GasRigProvenance.Unknown, GasRigProvenance.Describe((GasRigSettings?)null));

        var preamble = ServoSessionLogFormat.BuildPreamble("10.2.0-dev", 10, "app", null, new GasRigSettings { AirInletInput = GasInput.Input1 });
        Assert.Contains("# gas_rig: A=1 B/C=2", preamble, StringComparison.Ordinal);
        Assert.Contains("# gas_rig: " + GasRigProvenance.Unknown, ServoSessionLogFormat.BuildPreamble(null, -1, "app"), StringComparison.Ordinal);
    }

    [Fact]
    public void Flow_calibration_records_the_rig_and_the_route_with_its_points()
    {
        var settings = new MemorySettingsService(new AppSettings());
        var device = new RecordingDeviceService();
        using var vm = new FlowCalibrationViewModel(device, settings);
        Assert.Null(settings.Current.Calibration.FlowCalibrationGasRig);

        vm.CalibrateThroughReactor = true;
        vm.SavePointsCommand.Execute(null);

        Assert.NotNull(settings.Current.Calibration.FlowCalibrationGasRig);
        Assert.Equal(GasInput.Input2, settings.Current.Calibration.FlowCalibrationGasRig!.AirInletInput);
        Assert.Equal(GasRoute.Reactor, settings.Current.Calibration.FlowCalibrationRoute);
    }

    [Fact]
    public void The_manual_has_the_topic()
    {
        var topic = DocumentationCatalog.Find(DocumentationCatalog.SettingsGasRigTopicId);
        Assert.NotNull(topic);
        Assert.Equal("Configurações · Gás e válvulas", topic!.Title);
    }
}
