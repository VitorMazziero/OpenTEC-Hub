using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Bench of 2026-09-11: the operator set the stop criteria in the dialog, closed the application
/// normally and reopened it — the assay came back with the factory defaults, and three vent
/// stabilisations expired at 120 s. The dialog now persists on <c>Concluir</c>, with the assay idle
/// or running, and the exit path offers to save what is still unsaved.
/// </summary>
[Collection("AppPaths")]
public sealed class PowerCaptureSettingsPersistenceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerCaptureSettingsPersistenceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-capture-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
        _store = new PowerTestStore(AppPaths.PowerTestsDirectory);
    }

    public void Dispose()
    {
        _overrideScope.Dispose();
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Default_vent_stabilisation_timeout_is_500_s()
    {
        // τ ≈ 45 s and a 2.3× overshoot on opening: 120 s expired three times on the bench.
        Assert.Equal(500.0, new PowerTestSettings().MaxPrestageSeconds);
    }

    [Fact]
    public void Concluir_persists_the_criteria_and_bumps_the_revision()
    {
        var (vm, doc, dialogs) = Build();
        var revisionBefore = doc.SettingsRevision;
        dialogs.OnShow = v => { v.MaxPrestageSeconds = 321.0; v.PrestageFlowToleranceLpm = 0.3; return true; };

        vm.OpenCaptureSettingsCommand.Execute(null);

        var reloaded = _store.LoadTest(doc.FolderName);
        Assert.NotNull(reloaded);
        Assert.Equal(321.0, reloaded.Settings.MaxPrestageSeconds);
        Assert.Equal(0.3, reloaded.Settings.PrestageFlowToleranceLpm);
        Assert.Equal(revisionBefore + 1, reloaded.SettingsRevision);
        Assert.Contains($"revisão {revisionBefore + 1}", vm.StatusMessage);
        Assert.False(vm.HasUnsavedCaptureSettings);

        // The journal escapes non-ASCII, so the arrow appears as → in the file.
        var events = File.ReadAllText(Path.Combine(_store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName));
        Assert.Contains("\"SettingsChanged\"", events, StringComparison.Ordinal);
        Assert.Contains("MaxPrestageSeconds: 500 \\u2192 321", events, StringComparison.Ordinal);
        Assert.Contains("PrestageFlowToleranceLpm: 0.2 \\u2192 0.3", events, StringComparison.Ordinal);
    }

    [Fact]
    public void Concluir_with_no_change_writes_nothing()
    {
        var (vm, doc, dialogs) = Build();
        var revisionBefore = doc.SettingsRevision;
        dialogs.OnShow = _ => true;

        vm.OpenCaptureSettingsCommand.Execute(null);

        Assert.Equal(revisionBefore, _store.LoadTest(doc.FolderName)!.SettingsRevision);
        var eventsPath = Path.Combine(_store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName);
        Assert.True(!File.Exists(eventsPath) || !File.ReadAllText(eventsPath).Contains("SettingsChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Discarding_the_dialog_reverts_the_fields_to_the_assay()
    {
        var (vm, doc, dialogs) = Build();
        dialogs.OnShow = v => { v.MaxPrestageSeconds = 77.0; return false; };

        vm.OpenCaptureSettingsCommand.Execute(null);

        Assert.Equal(doc.Settings.MaxPrestageSeconds, vm.MaxPrestageSeconds);
        Assert.False(vm.HasUnsavedCaptureSettings);
        Assert.Equal(doc.Settings.MaxPrestageSeconds, _store.LoadTest(doc.FolderName)!.Settings.MaxPrestageSeconds);
    }

    [Fact]
    public void Invalid_criteria_are_refused_and_not_persisted()
    {
        var (vm, doc, dialogs) = Build();
        var before = doc.Settings;
        dialogs.OnShow = v => { v.MaxPrestageSeconds = -5; return true; };

        vm.OpenCaptureSettingsCommand.Execute(null);

        Assert.Equal(before, _store.LoadTest(doc.FolderName)!.Settings);
        Assert.Contains("Tempo limite da pré-estabilização", vm.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Persisting_while_running_reaches_the_document_the_runner_reads()
    {
        var runner = new StubRunner();
        var (vm, doc, dialogs) = Build(runner);
        runner.PrepareTest(doc);
        runner.IsRunning = true;
        runner.RaiseStateChanged();
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEditPlan);

        dialogs.OnShow = v => { v.MaxPrestageSeconds = 450.0; return true; };
        vm.OpenCaptureSettingsCommand.Execute(null);

        // Same document instance: the next VentStabilizing phase reads the new limit.
        Assert.Same(doc, runner.CurrentTest);
        Assert.Equal(450.0, runner.CurrentTest!.Settings.MaxPrestageSeconds);
        Assert.Equal(450.0, _store.LoadTest(doc.FolderName)!.Settings.MaxPrestageSeconds);
    }

    [Fact]
    public void HasUnsavedSetup_tracks_the_page_fields_and_exit_save_persists_them()
    {
        var (vm, doc, _) = Build();
        Assert.False(vm.HasUnsavedSetup);

        vm.MaxCaptureSeconds = 222.0;
        Assert.True(vm.HasUnsavedSetup);

        Assert.True(vm.TrySaveSetupForExit(out var error));
        Assert.Equal("", error);
        Assert.False(vm.HasUnsavedSetup);
        Assert.Equal(222.0, _store.LoadTest(doc.FolderName)!.Settings.MaxCaptureSeconds);
    }

    [Fact]
    public void Settings_diff_names_each_changed_field_with_invariant_values()
    {
        var before = new PowerTestSettings();
        var after = before with { MaxPrestageSeconds = 321.5, AutoAcceptRuns = true };

        var changes = PowerTestSettingsDiff.Compute(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Field == "MaxPrestageSeconds" && c.From == "500" && c.To == "321.5");
        Assert.Contains(changes, c => c.Field == "AutoAcceptRuns" && c.From == "false" && c.To == "true");
        Assert.Empty(PowerTestSettingsDiff.Compute(before, before));
    }

    private (PowerTestViewModel Vm, PowerTestDocument Doc, RecordingDialogs Dialogs) Build(IPowerTestRunner? runner = null)
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        var geometry = new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010, Impellers = [impeller] };
        var doc = _store.CreateTest("Ensaio Criterios", new FluidProperties(), geometry, new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var dialogs = new RecordingDialogs();
        var vm = new PowerTestViewModel(_store, device, arbiter, runner, dialogs);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);
        return (vm, vm.CurrentTest!, dialogs);
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public Func<PowerTestViewModel, bool> OnShow { get; set; } = _ => false;
        public bool ShowCaptureSettings(PowerTestViewModel viewModel) => OnShow(viewModel);
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => true;
        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => true;
        public bool PromptInput(string title, string message, out string response, string initialValue = "") { response = initialValue; return false; }
        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.Cancel;
    }

    private sealed class StubRunner : IPowerTestRunner
    {
        public PowerTestDocument? CurrentTest { get; private set; }
        public PowerRun? CurrentRun => null;
        public PowerCondition? CurrentCondition => null;
        public PowerRunPhase Phase => IsRunning ? PowerRunPhase.PrestagingFlow : PowerRunPhase.Idle;
        public bool IsRunning { get; set; }
        public bool IsInReview => false;
        public bool IsPausedByOperator => false;
        public bool IsPausedForMeasurement => false;
        public double PhaseElapsedSeconds => 0;
        public double TotalElapsedSeconds => 0;
        public double CurrentRpm => 0;
        public double CurrentTorquePercent => 0;
        public double CurrentTorqueCi95Percent => 0;
        public double CurrentTorqueCiTargetPercent => 0;
        public int CurrentAttempt => 0;
        public string StatusMessage => "";
        public IReadOnlyList<PowerDataPoint> CurrentRunPoints => [];
        public IReadOnlyList<PowerGlobalSeriesSample> GlobalSeriesSamples => [];

        public event Action? StateChanged;
        public event Action<PowerDataPoint>? DataPointAdded;
        public event Action<PowerRun>? RunStarted;
        public event Action<string>? Logged;

        public void RaiseStateChanged() => StateChanged?.Invoke();
        public bool CanStart(PowerTestDocument doc, out string? reason) { reason = null; return true; }
        public void PrepareTest(PowerTestDocument doc) => CurrentTest = doc;
        public void ClearTest() => CurrentTest = null;
        public Task StartTestAsync(PowerTestDocument doc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartRunAsync(PowerCondition condition, int replicateNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipCurrentConditionAsync(string reason = "") => Task.CompletedTask;
        public Task ResumeAfterMeasurementAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubmitManualEnergyAsync(double electricalPowerW, string? instrument = null, string? note = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRunAndReviewAsync(string reason = "") => Task.CompletedTask;
        public Task AcceptRunAsync() => Task.CompletedTask;
        public Task RejectRunAsync(string reason) => Task.CompletedTask;
        public Task RepeatRunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteTestAsync() => Task.CompletedTask;
        public Task AbortTestAsync(string reason) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TestDeviceService : IDeviceService
    {
        public ConnectionState State => ConnectionState.Connected;
        public TransportMedium? Medium => TransportMedium.Usb;
        public string Endpoint => "COM3";
        public SensorSnapshot? Latest { get; set; }
        public LinkDiagnostics Diagnostics => new();

        public event Action<ConnectionStateChange>? StateChanged;
        public event Action<SensorSnapshot>? TelemetryReceived;
        public event Action<string>? RawTelemetryReceived;
        public event Action<string>? DeviceLogReceived;
        public event Action<string>? CommandSent;
        public event Action<double>? SessionTimeZeroed;

        public void Connect() { }
        public void ConnectUsb(string portName) { }
        public void ConnectWiFi(string ipAddress) { }
        public void Disconnect() { }
        public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Send(OpenTECCommand command) { CommandSent?.Invoke(command.ToJson()); }
        public void ZeroSessionTime() { }
    }
}
