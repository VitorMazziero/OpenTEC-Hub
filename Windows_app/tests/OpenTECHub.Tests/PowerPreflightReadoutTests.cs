using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The acquisition page's live readiness read-out. The runner has always been able to say why a
/// test cannot start, but that answer only reached the operator as an error dialog after they
/// pressed the button; now it is on screen while they are still setting the rig up.
/// </summary>
public sealed class PowerPreflightReadoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerPreflight_" + Guid.NewGuid().ToString("N"));
    private readonly PowerTestStore _store;
    private readonly RecordingDeviceService _device = new();
    private readonly CommandArbiter _arbiter;

    public PowerPreflightReadoutTests()
    {
        _store = new PowerTestStore(_root);
        _arbiter = new CommandArbiter(_device, TimeProvider.System);
    }

    public void Dispose()
    {
        _arbiter.Dispose();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void Tare_library_is_loaded_on_open_and_no_tare_choice_persists()
    {
        _store.SaveTareProfile("eixo", new TareCurve { Points = { new TarePoint(300, 0.5, 0.05) } });
        using var vm = OpenAssay(new StubRunner());
        vm.SelectedTareProfile = Assert.Single(vm.TareProfiles);
        vm.ApplyTareProfileCommand.Execute(null);
        Assert.NotNull(_store.LoadTare("preflight"));
        vm.UseNoTareCommand.Execute(null);
        Assert.Null(vm.CurrentTest!.Tare);
        Assert.Null(_store.LoadTare("preflight"));
        Assert.Null(_store.LoadTest("preflight")!.Tare);
        Assert.Equal("RELATIVO", vm.ResultModeLabel);
        Assert.Single(_store.ListTareProfiles());
    }

    [Fact]
    public void Without_an_open_assay_the_readout_says_so_and_start_is_not_offered_as_ready()
    {
        var runner = new StubRunner();
        var viewModel = new PowerTestViewModel(_store, _arbiter, _arbiter, runner, null);

        Assert.False(viewModel.IsReadyToStart);
        Assert.Contains("Abra ou crie um ensaio", viewModel.PreflightMessage, StringComparison.Ordinal);

        viewModel.Dispose();
    }

    [Fact]
    public void The_runner_s_own_refusal_reaches_the_operator_before_they_press_start()
    {
        var runner = new StubRunner { Reason = "Conecte o Hub antes de iniciar o ensaio de potência." };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 0 });

        Assert.False(viewModel.IsReadyToStart);
        Assert.Equal("Conecte o Hub antes de iniciar o ensaio de potência.", viewModel.PreflightMessage);

        viewModel.Dispose();
    }

    [Fact]
    public void A_rig_that_passes_preflight_reads_as_ready()
    {
        var runner = new StubRunner { Reason = null };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 300, ServoTorquePct = 4 });

        Assert.True(viewModel.IsReadyToStart);
        Assert.Equal("Pronto para iniciar.", viewModel.PreflightMessage);

        viewModel.Dispose();
    }

    [Fact]
    public void A_throwing_preflight_is_reported_rather_than_crashing_the_page()
    {
        var runner = new StubRunner { ThrowMessage = "O ensaio precisa estar salvo antes de iniciar." };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 100 });

        Assert.False(viewModel.IsReadyToStart);
        Assert.Equal("O ensaio precisa estar salvo antes de iniciar.", viewModel.PreflightMessage);

        viewModel.Dispose();
    }

    [Fact]
    public void While_a_run_is_going_the_readout_stops_advertising_readiness()
    {
        var runner = new StubRunner { Reason = null };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 300 });
        Assert.True(viewModel.IsReadyToStart);

        runner.IsRunning = true;
        runner.RaiseStateChanged();

        Assert.False(viewModel.IsReadyToStart);
        Assert.Contains("andamento", viewModel.PreflightMessage, StringComparison.OrdinalIgnoreCase);

        runner.IsRunning = false;
        runner.IsInReview = true;
        runner.RaiseStateChanged();

        Assert.False(viewModel.IsReadyToStart);
        Assert.Contains("decisão", viewModel.PreflightMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.Dispose();
    }

    [Fact]
    public void HasPreflightWarning_is_true_when_blocked()
    {
        var runner = new StubRunner { Reason = "O fluxômetro precisa estar online para ensaios com aeração." };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 0 });

        Assert.False(viewModel.IsReadyToStart);
        Assert.True(viewModel.HasPreflightWarning);
        Assert.Equal("O fluxômetro precisa estar online para ensaios com aeração.", viewModel.PreflightMessage);

        viewModel.Dispose();
    }

    [Fact]
    public void HasPreflightWarning_is_false_when_ready()
    {
        var runner = new StubRunner { Reason = null };
        var viewModel = OpenAssay(runner);

        _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 300, ServoTorquePct = 4 });

        Assert.True(viewModel.IsReadyToStart);
        Assert.False(viewModel.HasPreflightWarning);
        Assert.Equal("Pronto para iniciar.", viewModel.PreflightMessage);

        viewModel.Dispose();
    }

    [Fact]
    public void The_check_is_throttled_so_telemetry_does_not_re_walk_the_plan_every_sample()
    {
        var runner = new StubRunner { Reason = null };
        var viewModel = OpenAssay(runner);

        var afterOpen = runner.CanStartCalls;

        for (var i = 0; i < 50; i++)
        {
            _device.PushTelemetry(new SensorSnapshot { HasServoSample = true, ServoRpm = 300 });
        }

        // A burst of samples inside one throttle window must not become a burst of full preflights.
        Assert.True(
            runner.CanStartCalls - afterOpen <= 5,
            $"preflight ran {runner.CanStartCalls - afterOpen} times for 50 telemetry samples");

        viewModel.Dispose();
    }

    [Fact]
    public void Idle_runner_does_not_undo_a_draft_table_edit_and_the_cell_is_saved_immediately()
    {
        var runner = new StubRunner();
        var viewModel = OpenAssay(runner);

        Assert.True(viewModel.CanEditPlan);
        viewModel.Conditions[0].AgitationRpm = 425;
        runner.RaiseStateChanged();

        Assert.Equal(425, Assert.Single(viewModel.Conditions).AgitationRpm);
        Assert.Equal(425, Assert.Single(_store.LoadTest("preflight")!.Conditions).AgitationRpm);

        viewModel.Dispose();
    }

    [Fact]
    public void Interrupted_assay_is_explicitly_editable_and_add_condition_is_persisted()
    {
        var runner = new StubRunner();
        var viewModel = OpenAssay(runner);
        viewModel.CurrentTest!.Status = PowerTestStatus.Interrupted;
        _store.SaveTestManifest(viewModel.CurrentTest);
        runner.RaiseStateChanged();

        Assert.True(viewModel.CanEditPlan);
        Assert.Contains("edição liberada", viewModel.TestStatusLabel, StringComparison.OrdinalIgnoreCase);

        viewModel.AddConditionCommand.Execute(null);

        Assert.Equal(2, viewModel.Conditions.Count);
        Assert.Equal(2, _store.LoadTest("preflight")!.Conditions.Count);
        viewModel.Dispose();
    }

    private PowerTestViewModel OpenAssay(StubRunner runner)
    {
        _store.CreateTest(
            "preflight",
            new FluidProperties(),
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings(),
            [new PowerCondition { AgitationRpm = 300, OrderIndex = 0 }]);

        var viewModel = new PowerTestViewModel(_store, _arbiter, _arbiter, runner, null);
        viewModel.RefreshTestsCommand.Execute(null);
        viewModel.SelectedTest = viewModel.Tests.First(t => t.Name == "preflight");
        viewModel.LoadSelectedTestCommand.Execute(null);
        return viewModel;
    }

    private sealed class StubRunner : IPowerTestRunner
    {
        public string? Reason { get; set; } = "não pronto";
        public string? ThrowMessage { get; set; }
        public int CanStartCalls { get; private set; }

        public bool CanStart(PowerTestDocument doc, out string? reason)
        {
            CanStartCalls++;
            if (ThrowMessage is not null)
            {
                throw new InvalidOperationException(ThrowMessage);
            }

            reason = Reason;
            return reason is null;
        }

        public void RaiseStateChanged() => StateChanged?.Invoke();

        public PowerTestDocument? CurrentTest { get; private set; }
        public PowerRun? CurrentRun => null;
        public PowerCondition? CurrentCondition => null;
        public PowerRunPhase Phase => PowerRunPhase.Idle;
        public bool IsRunning { get; set; }
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
        public string StatusMessage => "";
        public IReadOnlyList<PowerDataPoint> CurrentRunPoints => [];
        public IReadOnlyList<PowerGlobalSeriesSample> GlobalSeriesSamples => [];

        public event Action? StateChanged;
        public event Action<PowerDataPoint>? DataPointAdded;
        public event Action<PowerRun>? RunStarted;
        public event Action<string>? Logged;

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
}
