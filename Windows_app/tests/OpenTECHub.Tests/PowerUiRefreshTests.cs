using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Bench of 2026-09-11: every telemetry frame (~1 Hz) rebuilt the plan and results grids twice
/// (<c>Clear()</c> + re-add), so no row container, hover state, scroll position or selection
/// survived a second. The page now refreshes read-outs per frame and touches the grids only when
/// something structural changed — and then in place, by id.
/// </summary>
[Collection("AppPaths")]
public sealed class PowerUiRefreshTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerUiRefreshTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-ui-refresh-{Guid.NewGuid():N}");
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
    public void Repeated_frames_never_reset_the_grids_and_keep_the_selection()
    {
        var runner = new FrameStubRunner();
        var (vm, doc) = Build(runner);
        runner.PrepareTest(doc);
        runner.Phase = PowerRunPhase.AccumulatingToTarget;
        runner.IsRunning = true;
        runner.CurrentRun = new PowerRun { RunId = Guid.NewGuid(), ConditionId = doc.Conditions[1].ConditionId, SampleCount = 3, NetPowerW = 0.2 };
        runner.CurrentCondition = doc.Conditions[1];
        doc.Conditions[1].Status = PowerConditionStatus.InProgress;
        runner.RaiseStateChanged();

        var selectedCondition = vm.Conditions[2];
        vm.SelectedCondition = selectedCondition;
        var selectedRow = vm.Results[0];
        vm.SelectedResultRow = selectedRow;
        var conditionRows = vm.Conditions.ToArray();
        var conditionEvents = new List<NotifyCollectionChangedAction>();
        var resultEvents = new List<NotifyCollectionChangedAction>();
        vm.Conditions.CollectionChanged += (_, e) => conditionEvents.Add(e.Action);
        vm.Results.CollectionChanged += (_, e) => resultEvents.Add(e.Action);

        for (var i = 0; i < 20; i++)
        {
            runner.PhaseElapsed += 1.0;
            runner.RaiseDataPoint(new PowerDataPoint(DateTimeOffset.UtcNow, i, PowerRunPhase.AccumulatingToTarget, 300, 2.0, 0.02, 0.63, null, true));
            runner.RaiseStateChanged();
        }

        Assert.Empty(conditionEvents);
        Assert.Empty(resultEvents);
        Assert.Same(selectedCondition, vm.SelectedCondition);
        Assert.Same(selectedRow, vm.SelectedResultRow);
        Assert.Equal(conditionRows, vm.Conditions.ToArray());
        Assert.Equal(20, vm.LivePoints.Count);
        Assert.Equal(doc.Conditions[1].ConditionId, vm.CurrentConditionId);
        Assert.Equal(PowerConditionStatus.InProgress, vm.Conditions[1].Status);
    }

    /// <summary>§N: during the assay the plan grid is read-only, not disabled — selecting a row to see
    /// where the sequence is must never write the plan.</summary>
    [Fact]
    public void Selecting_a_condition_while_running_does_not_persist_the_plan()
    {
        var runner = new FrameStubRunner();
        var (vm, doc) = Build(runner);
        runner.PrepareTest(doc);
        runner.Phase = PowerRunPhase.AccumulatingToTarget;
        runner.IsRunning = true;
        runner.CurrentCondition = doc.Conditions[0];
        runner.RaiseStateChanged();
        Assert.False(vm.CanEditPlan);
        var folder = Path.Combine(_store.RootDirectory, doc.FolderName);
        var manifestBefore = File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName));
        var tableBefore = File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.ConditionTableFileName));

        vm.SelectedCondition = vm.Conditions[2];
        vm.SelectedCondition = vm.Conditions[1];
        runner.RaiseStateChanged();

        Assert.Same(vm.Conditions[1], vm.SelectedCondition);
        Assert.Equal(doc.Conditions[0].ConditionId, vm.CurrentConditionId);
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName)));
        Assert.Equal(tableBefore, File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.ConditionTableFileName)));

        runner.IsRunning = false;
        runner.Phase = PowerRunPhase.Idle;
        runner.CurrentCondition = null;
        runner.RaiseStateChanged();
        Assert.Null(vm.CurrentConditionId);
    }

    [Fact]
    public void Runner_owned_fields_are_copied_onto_the_existing_rows_without_replacing_them()
    {
        var runner = new FrameStubRunner();
        var (vm, doc) = Build(runner);
        runner.PrepareTest(doc);
        runner.Phase = PowerRunPhase.SettingSpeed;
        runner.IsRunning = true;
        runner.RaiseStateChanged();
        var row = vm.Conditions[0];
        var revisionBefore = _store.LoadTest(doc.FolderName)!.SettingsRevision;
        var events = new List<NotifyCollectionChangedAction>();
        vm.Conditions.CollectionChanged += (_, e) => events.Add(e.Action);

        // The runner accepts a replicate: counters and status move on the document, then a frame arrives.
        doc.Conditions[0].CompletedReplicates = 1;
        doc.Conditions[0].AcceptedReplicates = 1;
        doc.Conditions[0].Status = PowerConditionStatus.Completed;
        runner.Phase = PowerRunPhase.Accepted;
        runner.RaiseStateChanged();

        Assert.Empty(events);
        Assert.Same(row, vm.Conditions[0]);
        Assert.Equal(1, row.AcceptedReplicates);
        Assert.Equal("1/1", row.ReplicatesDisplay);
        Assert.Equal(PowerConditionStatus.Completed, row.Status);
        // Copying runtime state is not an edit: the plan on disk was not rewritten by the copy.
        Assert.Equal(revisionBefore, _store.LoadTest(doc.FolderName)!.SettingsRevision);
    }

    [Fact]
    public void A_new_run_is_inserted_and_a_changed_run_replaced_without_reset()
    {
        var runner = new FrameStubRunner();
        var (vm, doc) = Build(runner);
        runner.PrepareTest(doc);
        runner.Phase = PowerRunPhase.SettingSpeed;
        runner.IsRunning = true;
        runner.RaiseStateChanged();
        vm.SelectedResultRow = vm.Results[0];
        var selectedId = vm.SelectedResultRow!.RunId;
        var events = new List<NotifyCollectionChangedAction>();
        vm.Results.CollectionChanged += (_, e) => events.Add(e.Action);

        doc.Runs.Add(new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = doc.Conditions[1].ConditionId, ReplicateNumber = 1, FolderName = "N0400_Rep01",
            AgitationRpm = 400, GasMode = PowerGasMode.Ungassed, Phase = PowerRunPhase.Captured, StopReason = PowerStopReason.Target,
            SampleCount = 60, NetPowerW = 0.9, StartedUtc = DateTimeOffset.UtcNow,
        });
        runner.Phase = PowerRunPhase.Captured;
        runner.RaiseStateChanged();

        Assert.Equal([NotifyCollectionChangedAction.Add], events);
        Assert.Equal(3, vm.Results.Count);
        Assert.Equal(selectedId, vm.SelectedResultRow?.RunId);

        events.Clear();
        doc.Runs[^1] = doc.Runs[^1] with { Phase = PowerRunPhase.Accepted };
        doc.Conditions[1].AcceptedReplicates = 1;
        runner.Phase = PowerRunPhase.Accepted;
        runner.RaiseStateChanged();

        Assert.Equal([NotifyCollectionChangedAction.Replace], events);
        var updatedRow = vm.Results.First(r => r.RunId == doc.Runs[^1].RunId);
        Assert.Equal("Aceito", updatedRow.Status);
        Assert.Equal(selectedId, vm.SelectedResultRow?.RunId);
    }

    private (PowerTestViewModel Vm, PowerTestDocument Doc) Build(IPowerTestRunner runner)
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        var geometry = new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010, Impellers = [impeller] };
        var doc = _store.CreateTest("Ensaio Grades", new FluidProperties(), geometry, new PowerTestSettings(),
        [
            new PowerCondition { OrderIndex = 0, AgitationRpm = 300 },
            new PowerCondition { OrderIndex = 1, AgitationRpm = 400 },
            new PowerCondition { OrderIndex = 2, AgitationRpm = 500 },
        ]);
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        doc.Runs.Add(new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = doc.Conditions[0].ConditionId, ReplicateNumber = 1, FolderName = "N0300_Rep01",
            AgitationRpm = 300, GasMode = PowerGasMode.Ungassed, Phase = PowerRunPhase.Accepted, StopReason = PowerStopReason.Target,
            SampleCount = 60, NetPowerW = 0.4, StartedUtc = started,
        });
        doc.Runs.Add(new PowerRunSummary
        {
            RunId = Guid.NewGuid(), ConditionId = doc.Conditions[0].ConditionId, ReplicateNumber = 2, FolderName = "N0300_Rep02",
            AgitationRpm = 300, GasMode = PowerGasMode.Ungassed, Phase = PowerRunPhase.Rejected, StopReason = PowerStopReason.Tmax,
            SampleCount = 30, NetPowerW = 0.5, StartedUtc = started.AddMinutes(2),
        });
        _store.SaveTestManifest(doc);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter, runner, null);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);
        return (vm, vm.CurrentTest!);
    }

    private sealed class FrameStubRunner : IPowerTestRunner
    {
        public PowerTestDocument? CurrentTest { get; private set; }
        public PowerRun? CurrentRun { get; set; }
        public PowerCondition? CurrentCondition { get; set; }
        public PowerRunPhase Phase { get; set; } = PowerRunPhase.Idle;
        public bool IsRunning { get; set; }
        public bool IsInReview => Phase == PowerRunPhase.Reviewing;
        public bool IsPausedByOperator => false;
        public bool IsPausedForMeasurement => false;
        public double PhaseElapsed { get; set; }
        public double PhaseElapsedSeconds => PhaseElapsed;
        public double TotalElapsedSeconds => PhaseElapsed;
        public double CurrentRpm => 300;
        public double CurrentTorquePercent => 2;
        public double CurrentTorqueCi95Percent => 0.5;
        public double CurrentTorqueCiTargetPercent => 0.1;
        public int CurrentAttempt => 1;
        public string StatusMessage => "";
        public IReadOnlyList<PowerDataPoint> CurrentRunPoints => [];
        public IReadOnlyList<PowerGlobalSeriesSample> GlobalSeriesSamples => [];

        public event Action? StateChanged;
        public event Action<PowerDataPoint>? DataPointAdded;
        public event Action<PowerRun>? RunStarted;
        public event Action<string>? Logged;

        public void RaiseStateChanged() => StateChanged?.Invoke();
        public void RaiseDataPoint(PowerDataPoint point) => DataPointAdded?.Invoke(point);
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
