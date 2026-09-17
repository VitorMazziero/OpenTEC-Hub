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
/// D-050: a run that stopped for review before capturing anything (vent, valve or speed time-out)
/// is not a result. Bench of 2026-09-11, IsojetB-Combijet: two such runs were accepted with n = 0
/// and P = 0 W, entered <c>resumo-resultados.csv</c> and marked their conditions complete, so the
/// sequence would have skipped them.
/// </summary>
[Collection("AppPaths")]
public sealed class PowerRunWithoutCaptureTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerRunWithoutCaptureTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-no-capture-{Guid.NewGuid():N}");
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
    public void HasCapture_requires_samples_and_a_finite_net_power()
    {
        Assert.False(PowerTestRunner.HasCapture(new PowerRun { SampleCount = 0, NetPowerW = 0 }));
        Assert.False(PowerTestRunner.HasCapture(new PowerRun { SampleCount = 12, NetPowerW = double.NaN }));
        Assert.True(PowerTestRunner.HasCapture(new PowerRun { SampleCount = 12, NetPowerW = 0.35 }));

        // Summary rows: a ghost has no samples AND no usable power (the bench case: n = 0, P = 0 W).
        Assert.True(PowerTestFileContracts.IsRunWithoutCapture(new PowerRunSummary { SampleCount = 0, NetPowerW = 0 }));
        Assert.True(PowerTestFileContracts.IsRunWithoutCapture(new PowerRunSummary { SampleCount = 0, NetPowerW = null }));
        Assert.True(PowerTestFileContracts.IsRunWithoutCapture(new PowerRunSummary { SampleCount = 0, NetPowerW = double.NaN }));
        Assert.False(PowerTestFileContracts.IsRunWithoutCapture(new PowerRunSummary { SampleCount = 0, NetPowerW = 1.2 }));
        Assert.False(PowerTestFileContracts.IsRunWithoutCapture(new PowerRunSummary { SampleCount = 5, NetPowerW = 1.2 }));
    }

    [Fact]
    public void ViewModel_offers_no_accept_for_a_run_that_did_not_capture()
    {
        var runner = new ReviewStubRunner();
        var (vm, doc) = Build(runner);
        runner.PrepareTest(doc);

        runner.CurrentRun = new PowerRun { RunId = Guid.NewGuid(), ConditionId = doc.Conditions[0].ConditionId, SampleCount = 0, NetPowerW = 0 };
        runner.StatusMessage = "Tempo limite de estabilização da vazão no alívio excedido.";
        runner.IsInReview = true;
        runner.RaiseStateChanged();

        Assert.True(vm.IsInReview);
        Assert.False(vm.ReviewHasCapture);
        Assert.False(vm.CanAcceptRun);
        Assert.True(vm.IsReviewingUnperformedRun);
        Assert.Equal("Sem captura — Tempo limite de estabilização da vazão no alívio excedido.", vm.ReviewNoCaptureText);

        runner.CurrentRun = new PowerRun { RunId = Guid.NewGuid(), ConditionId = doc.Conditions[0].ConditionId, SampleCount = 40, NetPowerW = 0.8 };
        runner.RaiseStateChanged();
        Assert.True(vm.CanAcceptRun);
        Assert.False(vm.IsReviewingUnperformedRun);
        Assert.Equal("", vm.ReviewNoCaptureText);
    }

    [Fact]
    public void Per_row_status_override_allows_reviewing_a_row_without_capture()
    {
        var (vm, doc) = Build(null);
        var condition = doc.Conditions[0];
        var run = new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = condition.ConditionId, ReplicateNumber = 1, FolderName = "N0200_Q02p00_Rep01",
            AgitationRpm = 200, GasFlowLpm = 2.0, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Rejected,
            StopReason = PowerStopReason.Tmax, SampleCount = 0, NetPowerW = 0,
        };
        doc.Runs.Add(run);
        _store.SaveTestManifest(doc);
        vm.LoadSelectedTestCommand.Execute(null);
        vm.SelectedResultRow = vm.Results.First(r => r.RunId == run.RunId);

        vm.CycleResultStatusCommand.Execute(vm.SelectedResultRow);

        Assert.Equal(PowerRunPhase.Accepted, vm.CurrentTest!.Runs.Single(r => r.RunId == run.RunId).Phase);
        Assert.Contains("Status do ponto alterado para Aceito", vm.StatusMessage);
    }

    [Fact]
    public void Loading_demotes_accepted_runs_without_capture_and_reopens_their_conditions()
    {
        var (_, doc) = Build(null);
        var condition = doc.Conditions[0];
        var ghost = new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = condition.ConditionId, ReplicateNumber = 1, FolderName = "N0200_Q02p00_Rep01",
            AgitationRpm = 200, GasFlowLpm = 2.0, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Tmax, SampleCount = 0, NetPowerW = 0,
        };
        var real = new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = condition.ConditionId, ReplicateNumber = 2, FolderName = "N0200_Q02p00_Rep02",
            AgitationRpm = 200, GasFlowLpm = 2.0, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Target, SampleCount = 60, NetPowerW = 0.42,
        };
        doc.Runs.Add(ghost);
        doc.Runs.Add(real);
        condition.RequestedReplicates = 2;
        condition.AcceptedReplicates = 2;
        condition.CompletedReplicates = 2;
        condition.Status = PowerConditionStatus.Completed;
        doc.Status = PowerTestStatus.Completed;
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);
        _store.SaveTestManifest(doc);

        var loaded = _store.LoadTest(doc.FolderName);

        Assert.NotNull(loaded);
        Assert.Equal(PowerRunPhase.Rejected, loaded.Runs.Single(r => r.RunId == ghost.RunId).Phase);
        Assert.Equal(PowerRunPhase.Accepted, loaded.Runs.Single(r => r.RunId == real.RunId).Phase);
        var reopened = loaded.Conditions.Single(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(1, reopened.AcceptedReplicates);
        Assert.Equal(1, reopened.RejectedReplicates);
        Assert.Equal(PowerConditionStatus.Pending, reopened.Status);
        Assert.Equal(PowerTestStatus.Interrupted, loaded.Status);

        // Persisted once: a second load finds nothing to migrate and the journal has one entry.
        var folder = Path.Combine(_store.RootDirectory, doc.FolderName);
        var events = File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.EventLogFileName));
        // The journal escapes non-ASCII, so match on the run folder that only the migration names.
        Assert.Single(events.Split("N0200_Q02p00_Rep01 estava aceita com n = 0", StringSplitOptions.None).Skip(1));
        var again = _store.LoadTest(doc.FolderName);
        Assert.Equal(PowerRunPhase.Rejected, again!.Runs.Single(r => r.RunId == ghost.RunId).Phase);
        Assert.Equal(events, File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.EventLogFileName)));

        // The summary was regenerated from the surviving capture only.
        var summary = File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.ResultsSummaryFileName));
        Assert.Contains("0.42", summary, StringComparison.Ordinal);
    }

    /// <summary>§G: a finished assay is not a dead end — Duplicar copies the setup without the points, Reabrir lifts Completed.</summary>
    [Fact]
    public void A_completed_assay_can_be_duplicated_without_runs_and_reopened()
    {
        var dialogs = new PromptingDialogs { NextInput = "Ensaio Sem Captura (2)" };
        var (vm, doc) = Build(null, dialogs);
        var condition = doc.Conditions[0];
        doc.Runs.Add(new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = condition.ConditionId, ReplicateNumber = 1, FolderName = "N0200_Q02p00_Rep01",
            AgitationRpm = 200, GasFlowLpm = 2.0, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Target, SampleCount = 60, NetPowerW = 0.42,
        });
        condition.AcceptedReplicates = 1;
        condition.CompletedReplicates = 1;
        condition.Status = PowerConditionStatus.Completed;
        doc.Tare = new TareCurve { Points = [new TarePoint(200, 0.1, 0.5)], MeasuredUtc = DateTimeOffset.UnixEpoch };
        doc.Status = PowerTestStatus.Completed;
        doc.CompletedUtc = DateTimeOffset.UtcNow;
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);
        _store.SaveTare(doc.FolderName, doc.Tare);
        _store.SaveTestManifest(doc);
        vm.LoadSelectedTestCommand.Execute(null);
        Assert.False(vm.CanEditPlan);
        Assert.False(vm.CanStartOrContinue);
        Assert.True(vm.CanReopenTest);
        Assert.Contains("Duplicar", vm.TestStatusLabel, StringComparison.Ordinal);

        vm.DuplicateTestCommand.Execute(null);

        var copy = vm.CurrentTest!;
        Assert.NotEqual(doc.TestId, copy.TestId);
        Assert.Equal(doc.TestId, copy.DuplicatedFrom);
        Assert.Equal("Ensaio Sem Captura (2)", copy.Name);
        Assert.Empty(copy.Runs);
        Assert.Null(copy.Flooding);
        Assert.Equal(PowerTestStatus.Draft, copy.Status);
        Assert.Single(copy.Conditions);
        Assert.NotEqual(condition.ConditionId, copy.Conditions[0].ConditionId);
        Assert.Equal(200, copy.Conditions[0].AgitationRpm);
        Assert.Equal(0, copy.Conditions[0].AcceptedReplicates);
        Assert.Equal(PowerConditionStatus.Pending, copy.Conditions[0].Status);
        Assert.Equal(doc.Settings, copy.Settings);
        Assert.NotNull(copy.Tare);
        Assert.Single(copy.Tare!.Points);
        Assert.True(vm.CanEditPlan);
        var reloadedCopy = _store.LoadTest(copy.FolderName)!;
        Assert.Equal(doc.TestId, reloadedCopy.DuplicatedFrom);
        Assert.Contains("TestDuplicated", File.ReadAllText(Path.Combine(_store.RootDirectory, copy.FolderName, PowerTestFileContracts.EventLogFileName)), StringComparison.Ordinal);

        // Back to the original: Reabrir lifts Completed and keeps the accepted run.
        vm.SelectedTest = vm.Tests.First(t => t.FolderName == doc.FolderName);
        vm.LoadSelectedTestCommand.Execute(null);
        Assert.True(vm.CanReopenTest);
        vm.ReopenTestCommand.Execute(null);

        Assert.Equal(PowerTestStatus.Interrupted, vm.CurrentTest!.Status);
        Assert.Null(vm.CurrentTest.CompletedUtc);
        Assert.True(vm.CanEditPlan);
        Assert.True(vm.CanStartOrContinue);
        Assert.False(vm.CanReopenTest);
        var reloaded = _store.LoadTest(doc.FolderName)!;
        Assert.Equal(PowerTestStatus.Interrupted, reloaded.Status);
        Assert.Equal("Reaberto pelo operador", reloaded.InterruptionReason);
        Assert.Single(reloaded.Runs, r => r.Phase == PowerRunPhase.Accepted);
        Assert.Contains("TestReopened", File.ReadAllText(Path.Combine(_store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_leaves_a_clean_manifest_untouched()
    {
        var (_, doc) = Build(null);
        doc.Runs.Add(new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = doc.Conditions[0].ConditionId, ReplicateNumber = 1, FolderName = "N0300_Rep01",
            AgitationRpm = 300, GasMode = PowerGasMode.Ungassed, Phase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Target, SampleCount = 60, NetPowerW = 0.42,
        });
        _store.SaveTestManifest(doc);
        var folder = Path.Combine(_store.RootDirectory, doc.FolderName);
        var eventsPath = Path.Combine(folder, PowerTestFileContracts.EventLogFileName);
        var before = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";

        var loaded = _store.LoadTest(doc.FolderName);

        Assert.Equal(PowerRunPhase.Accepted, loaded!.Runs.Single().Phase);
        Assert.Equal(before, File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "");
    }

    private (PowerTestViewModel Vm, PowerTestDocument Doc) Build(IPowerTestRunner? runner, IDialogService? dialogs = null)
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        var geometry = new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010, Impellers = [impeller] };
        var doc = _store.CreateTest("Ensaio Sem Captura", new FluidProperties(), geometry, new PowerTestSettings(),
            [new PowerCondition { AgitationRpm = 200, GasFlowLpm = 2.0, GasMode = PowerGasMode.Gassed }]);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter, runner, dialogs);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);
        return (vm, vm.CurrentTest!);
    }

    private sealed class PromptingDialogs : IDialogService
    {
        public string NextInput { get; set; } = "";
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => true;
        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => true;
        public bool PromptInput(string title, string message, out string response, string initialValue = "") { response = NextInput; return true; }
        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.Cancel;
    }

    private sealed class ReviewStubRunner : IPowerTestRunner
    {
        public PowerTestDocument? CurrentTest { get; private set; }
        public PowerRun? CurrentRun { get; set; }
        public PowerCondition? CurrentCondition => null;
        public PowerRunPhase Phase => IsInReview ? PowerRunPhase.Reviewing : PowerRunPhase.Idle;
        public bool IsRunning => false;
        public bool IsInReview { get; set; }
        public bool IsPausedByOperator => false;
        public bool IsPausedForMeasurement => false;
        public double PhaseElapsedSeconds => 0;
        public double TotalElapsedSeconds => 0;
        public double CurrentRpm => 0;
        public double CurrentTorquePercent => 0;
        public double CurrentTorqueCi95Percent => 0;
        public double CurrentTorqueCiTargetPercent => 0;
        public int CurrentAttempt => 0;
        public string StatusMessage { get; set; } = "";
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
        public Task ResumeAfterLinkRecoveryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
