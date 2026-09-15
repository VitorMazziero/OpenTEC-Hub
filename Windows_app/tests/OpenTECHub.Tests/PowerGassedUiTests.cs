using System;
using System.Collections.Generic;
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

[Collection("AppPaths")]
public sealed class PowerGassedUiTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerGassedUiTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-gassed-ui-test-{Guid.NewGuid():N}");
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
    public void PowerCondition_converts_Lpm_and_vvm_using_liquid_volume_and_notifies_changes()
    {
        var volume = 10.0;
        var condition = new PowerCondition { GasMode = PowerGasMode.Ungassed };
        condition.ConfigureFlowConversion(() => volume);

        var notifiedProps = new List<string>();
        condition.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                notifiedProps.Add(e.PropertyName);
            }
        };

        // Setting L/min -> computes vvm and switches GasMode to Gassed
        condition.GasFlowLpm = 5.0;

        Assert.Equal(5.0, condition.GasFlowLpm);
        Assert.Equal(0.5, condition.GasFlowVvm);
        Assert.Equal(PowerGasMode.Gassed, condition.GasMode);
        Assert.Contains(nameof(condition.GasFlowLpm), notifiedProps);
        Assert.Contains(nameof(condition.GasFlowVvm), notifiedProps);
        Assert.Contains(nameof(condition.GasMode), notifiedProps);

        // Setting vvm directly
        notifiedProps.Clear();
        condition.GasFlowVvm = 1.0;

        Assert.Equal(1.0, condition.GasFlowVvm);
        Assert.Equal(10.0, condition.GasFlowLpm);
        Assert.Contains(nameof(condition.GasFlowLpm), notifiedProps);
        Assert.Contains(nameof(condition.GasFlowVvm), notifiedProps);
    }

    [Fact]
    public void PowerCondition_warns_vvm_when_liquid_volume_is_zero_or_negative()
    {
        string? error = null;
        var condition = new PowerCondition();
        condition.ConfigureFlowConversion(() => 0.0, msg => error = msg);
        condition.GasFlowVvm = 0.5;

        Assert.Equal(0.5, condition.GasFlowVvm);
        Assert.Null(condition.GasFlowLpm);
        Assert.NotNull(error);
        Assert.Contains("Volume útil", error);
    }

    [Fact]
    public void PowerCondition_resets_gas_flows_when_gas_mode_set_to_ungassed()
    {
        var condition = new PowerCondition
        {
            GasMode = PowerGasMode.Gassed,
        };
        condition.ConfigureFlowConversion(() => 10.0);
        condition.GasFlowLpm = 8.0;

        Assert.Equal(8.0, condition.GasFlowLpm);
        Assert.Equal(0.8, condition.GasFlowVvm);

        condition.GasMode = PowerGasMode.Ungassed;

        Assert.Null(condition.GasFlowLpm);
        Assert.Null(condition.GasFlowVvm);
    }

    [Fact]
    public void PowerCondition_flow_conversion_context_is_isolated_per_condition()
    {
        var fiveLiterCondition = new PowerCondition();
        var twentyLiterCondition = new PowerCondition();
        fiveLiterCondition.ConfigureFlowConversion(() => 5.0);
        twentyLiterCondition.ConfigureFlowConversion(() => 20.0);

        fiveLiterCondition.GasFlowVvm = 0.5;
        twentyLiterCondition.GasFlowVvm = 0.5;

        Assert.Equal(2.5, fiveLiterCondition.GasFlowLpm);
        Assert.Equal(10.0, twentyLiterCondition.GasFlowLpm);
    }

    [Fact]
    public void ViewModel_round_trips_auto_accept_and_drops_the_manual_energy_hold()
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Impellers = [impeller],
        };
        var doc = _store.CreateTest("Ensaio Automatico", new FluidProperties(), geometry, new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.False(vm.AutoAcceptRuns);

        vm.ManualEnergyCaptureEnabled = true;
        vm.AutoAcceptRuns = true;

        // The wattmeter hold parks every point, so it cannot coexist with an unattended run.
        Assert.False(vm.ManualEnergyCaptureEnabled);

        vm.SaveSetupCommand.Execute(null);
        var reloaded = _store.LoadTest(doc.FolderName);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.Settings.AutoAcceptRuns);
        Assert.False(reloaded.Settings.ManualEnergyCaptureEnabled);
    }

    [Fact]
    public void ViewModel_generate_sweep_supports_variable_N_constant_Qg()
    {
        var doc = _store.CreateTest("Ensaio N Const Qg", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        vm.SelectedSweepType = PowerSweepType.VariableNConstantQg;
        vm.MinRpm = 100;
        vm.MaxRpm = 300;
        vm.StepRpm = 100;
        vm.SweepConstantQgLpm = 4.0;
        vm.SweepGasMode = PowerGasMode.Gassed;
        vm.GenerateSweepCommand.Execute(null);

        Assert.Equal(3, vm.Conditions.Count);
        Assert.All(vm.Conditions, c =>
        {
            Assert.Equal(4.0, c.GasFlowLpm);
            Assert.Equal(PowerGasMode.Gassed, c.GasMode);
        });
        Assert.Equal([100.0, 200.0, 300.0], vm.Conditions.Select(c => c.AgitationRpm).ToArray());

        var reloaded = _store.LoadTest(doc.FolderName);
        Assert.NotNull(reloaded);
        Assert.Equal([100.0, 200.0, 300.0], reloaded.Conditions.Select(c => c.AgitationRpm).ToArray());
    }

    [Fact]
    public void ViewModel_generate_sweep_supports_variable_Qg_constant_N_flooding_mapping()
    {
        var doc = _store.CreateTest("Ensaio Flooding", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        vm.SelectedSweepType = PowerSweepType.VariableQgConstantN;
        vm.SweepConstantRpm = 400;
        vm.SweepStartQgLpm = 2.0;
        vm.SweepEndQgLpm = 6.0;
        vm.SweepStepQgLpm = 2.0;
        vm.SweepGasMode = PowerGasMode.Both;
        vm.GenerateSweepCommand.Execute(null);

        Assert.Equal(3, vm.Conditions.Count);
        Assert.All(vm.Conditions, c =>
        {
            Assert.Equal(400.0, c.AgitationRpm);
            Assert.Equal(PowerGasMode.Both, c.GasMode);
        });
        Assert.Equal([2.0, 4.0, 6.0], vm.Conditions.Select(c => c.GasFlowLpm ?? 0).ToArray());
    }

    [Fact]
    public void ViewModel_generate_sweep_supports_matrix_2D_N_by_Qg()
    {
        var doc = _store.CreateTest("Ensaio Matrix 2D", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        vm.SelectedSweepType = PowerSweepType.MatrixNByQg;
        vm.MinRpm = 200;
        vm.MaxRpm = 300;
        vm.StepRpm = 100; // 200, 300 -> 2 speeds
        vm.SweepStartQgLpm = 5.0;
        vm.SweepEndQgLpm = 10.0;
        vm.SweepStepQgLpm = 5.0; // 5, 10 -> 2 flows
        vm.SweepGasMode = PowerGasMode.Gassed;
        vm.GenerateSweepCommand.Execute(null);

        Assert.Equal(4, vm.Conditions.Count);
        Assert.Equal(200, vm.Conditions[0].AgitationRpm);
        Assert.Equal(5.0, vm.Conditions[0].GasFlowLpm);
        Assert.Equal(200, vm.Conditions[1].AgitationRpm);
        Assert.Equal(10.0, vm.Conditions[1].GasFlowLpm);
        Assert.Equal(300, vm.Conditions[2].AgitationRpm);
        Assert.Equal(5.0, vm.Conditions[2].GasFlowLpm);
        Assert.Equal(300, vm.Conditions[3].AgitationRpm);
        Assert.Equal(10.0, vm.Conditions[3].GasFlowLpm);
    }

    [Fact]
    public void ViewModel_vent_stabilization_controls_and_validates_settings()
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Impellers = [impeller],
        };
        var doc = _store.CreateTest("Ensaio Alivio", new FluidProperties(), geometry, new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        vm.PrestageFlowToleranceLpm = 0.35;
        vm.PrestageFlowStableSamples = 8;
        vm.PrestageAgitationRpm = 25.0;
        vm.MaxPrestageSeconds = 90.0;

        vm.SaveSetupCommand.Execute(null);

        var savedDoc = _store.LoadTest(doc.FolderName);
        Assert.NotNull(savedDoc);
        Assert.Equal(0.35, savedDoc.Settings.PrestageFlowToleranceLpm);
        Assert.Equal(8, savedDoc.Settings.PrestageFlowStableSamples);
        Assert.Equal(25.0, savedDoc.Settings.PrestageAgitationRpm);
        Assert.Equal(90.0, savedDoc.Settings.MaxPrestageSeconds);
    }

    [Fact]
    public void ViewModel_live_metrics_computes_vvm_power_ratio_and_gas_loop_badge()
    {
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010, // 10 L
            Impellers = [impeller],
        };
        var doc = _store.CreateTest("Ensaio Live Gas", new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 }, geometry, new PowerTestSettings());

        // Add an ungassed run to act as P0 reference at 300 rpm
        var p0Run = new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            MeanRpmMeasured = 300.0,
            NetPowerW = 10.0,
            GasMode = PowerGasMode.Ungassed,
            Phase = PowerRunPhase.Accepted,
            Analysis = new PowerPointResult
            {
                AssemblyReynoldsNumber = 20000.0,
                AssemblyPowerNumber = 5.0,
            },
        };
        doc.Runs.Add(p0Run);
        _store.SaveTestManifest(doc);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        // Telemetry with flow and A open — valve_2 on the default A/B/C wiring (plan §1.3.1)
        var snapshot = new SensorSnapshot
        {
            HasServoSample = true,
            ServoRpm = 300.0,
            ServoTorquePct = 5.0,
            FlowRate = 5.0, // 5 L/min
            FlowSetpoint = 5.0,
            FlowValve2 = 1,
            FlowValveMain = 0,
        };
        device.Emit(snapshot);

        Assert.Equal(5.0, vm.CurrentFlowLpm);
        Assert.Equal(0.5, vm.CurrentFlowVvm); // 5 L/min / 10 L = 0.5 vvm
        Assert.Equal("Reator (A)", vm.GasLoopStatusBadge);
        Assert.NotNull(vm.CurrentPowerRatio);
        Assert.True(vm.CurrentPowerRatio > 0);
    }

    /// <summary>
    /// The chip reads the wire the way the rig does (plan A/B/C §3.1, Etapa 5): the observed pair
    /// and the echoed setpoint, with the hardware's names. With the runner idle, both outputs
    /// closed and no setpoint is simply "Fechado" — the shutoff FlowSafeStop leaves on purpose,
    /// not a vent stabilising (§F.4, Rushton-Smith 2026-09-11: chip stuck on the old text).
    /// A setpoint with nothing open is a dead-ended line; both open is the anomaly.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 0.0, 0.0, "Fechado")]
    [InlineData(0, 0, 0, 0.0, 0.0, "Fechado")]
    [InlineData(1, 0, 0, 2.0, 2.1, "Gás sem destino")]
    [InlineData(0, 1, 0, 2.0, 2.0, "Descarga + N₂ (B/C)")]
    [InlineData(0, 0, 1, 2.0, 2.0, "Reator (A)")]
    [InlineData(0, 1, 1, 2.0, 2.0, "A e B/C abertas")]
    public void Gas_loop_chip_names_the_observed_route(int vFlow, int valve1, int valve2, double setpoint, double flow, string expected)
    {
        var snapshot = new SensorSnapshot { FlowValveMain = vFlow, FlowValve1 = valve1, FlowValve2 = valve2, FlowSetpoint = setpoint, FlowRate = flow };

        Assert.Equal(expected, PowerTestViewModel.GasLoopStatusFor(runner: null, snapshot, GasRigConfiguration.Default));
    }

    /// <summary>The same pair reads the other way round on the other wiring.</summary>
    [Fact]
    public void Gas_loop_chip_follows_the_configured_wiring()
    {
        var snapshot = new SensorSnapshot { FlowValve1 = 1, FlowValve2 = 0, FlowSetpoint = 2.0, FlowRate = 2.0 };
        Assert.Equal("Reator (A)", PowerTestViewModel.GasLoopStatusFor(runner: null, snapshot, new GasRigConfiguration(GasInput.Input1)));
    }

    [Fact]
    public void PowerTestViewModel_rebuild_results_populates_gas_columns_without_flooding_summary()
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Impellers = [new Impeller { DiameterM = 0.06, BladeCount = 6 }]
        };
        var doc = _store.CreateTest("Flooding-Step6-Test", new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 }, geometry, new PowerTestSettings());

        // Ungassed reference P0
        var p0Run = new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            AgitationRpm = 400.0,
            MeanRpmMeasured = 400.0,
            NetPowerW = 20.0,
            GasMode = PowerGasMode.Ungassed,
            Phase = PowerRunPhase.Accepted,
            Analysis = new PowerPointResult { AssemblyReynoldsNumber = 24000.0, AssemblyPowerNumber = 5.0 }
        };
        doc.Runs.Add(p0Run);

        // Gassed runs at increasing gas flows
        var flowRates = new[] { 1.0, 2.0, 4.0, 8.0, 12.0, 16.0 };
        var ratios = new[] { 0.95, 0.90, 0.82, 0.72, 0.55, 0.50 };
        for (var i = 0; i < flowRates.Length; i++)
        {
            var gassedRun = new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-15 + i),
                AgitationRpm = 400.0,
                MeanRpmMeasured = 400.0,
                NetPowerW = 20.0 * ratios[i],
                GassedPowerW = 20.0 * ratios[i],
                ReferenceP0W = 20.0,
                GasMode = PowerGasMode.Gassed,
                GasFlowLpm = flowRates[i],
                GasFlowNumber = 0.005 * (i + 1),
                FroudeNumber = 0.18,
                PowerRatio = ratios[i],
                PowerRatioCi95 = 0.02,
                Phase = PowerRunPhase.Accepted,
                Analysis = new PowerPointResult
                {
                    AssemblyReynoldsNumber = 24000.0,
                    AssemblyPowerNumber = 5.0 * ratios[i],
                }
            };
            doc.Runs.Add(gassedRun);
        }

        _store.SaveTestManifest(doc);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.Equal(7, vm.Results.Count); // 1 P0 + 6 Gassed
        var gassedRow = vm.Results.FirstOrDefault(r => r.IsGassed && Math.Abs(r.Ratio - 0.82) < 1e-3);
        Assert.NotNull(gassedRow);
        Assert.Equal(0.015, gassedRow.AerationNumber, 4);
        Assert.Equal(0.18, gassedRow.FroudeNumber, 3);
        Assert.NotEqual("—", gassedRow.PgLiquid);
        Assert.NotEqual("—", gassedRow.P0Ref);
        Assert.NotEqual("—", gassedRow.PowerRatio);

        // Flooding/Nienow are no longer evaluated or exposed by the power assay VM.
        Assert.Null(vm.CurrentTest!.Flooding);
    }

    [Fact]
    public void PowerTestViewModel_allows_per_row_status_cycle_without_flooding_workflow()
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Impellers = [new Impeller { DiameterM = 0.06, BladeCount = 6 }]
        };
        var doc = _store.CreateTest("Step7-Flooding-Review", new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 }, geometry, new PowerTestSettings());

        var flowRates = new[] { 2.0, 4.0, 8.0, 12.0 };
        var ratios = new[] { 0.90, 0.80, 0.70, 0.60 };
        for (var i = 0; i < flowRates.Length; i++)
        {
            doc.Runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10 + i),
                AgitationRpm = 400.0,
                MeanRpmMeasured = 400.0,
                NetPowerW = 20.0 * ratios[i],
                GassedPowerW = 20.0 * ratios[i],
                ReferenceP0W = 20.0,
                GasMode = PowerGasMode.Gassed,
                GasFlowLpm = flowRates[i],
                GasFlowNumber = 0.005 * (i + 1),
                FroudeNumber = 0.18,
                PowerRatio = ratios[i],
                PowerRatioCi95 = 0.02,
                Phase = PowerRunPhase.Accepted,
            });
        }
        _store.SaveTestManifest(doc);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.Null(vm.CurrentTest!.Flooding);
        var targetRow = vm.Results.First(r => r.IsGassed && Math.Abs(r.Ratio - 0.80) < 1e-3);
        Assert.True(vm.CanChangeResultStatus);

        vm.CycleResultStatusCommand.Execute(targetRow);
        Assert.Equal("Capturado", vm.Results.First(r => r.RunId == targetRow.RunId).Status);
        Assert.Equal("🟡", vm.Results.First(r => r.RunId == targetRow.RunId).StatusGlyph);

        vm.CycleResultStatusCommand.Execute(vm.Results.First(r => r.RunId == targetRow.RunId));
        Assert.Equal("Em revisão", vm.Results.First(r => r.RunId == targetRow.RunId).Status);
        Assert.Equal("🔵", vm.Results.First(r => r.RunId == targetRow.RunId).StatusGlyph);

        vm.CycleResultStatusCommand.Execute(vm.Results.First(r => r.RunId == targetRow.RunId));
        Assert.Equal("Rejeitado", vm.Results.First(r => r.RunId == targetRow.RunId).Status);
        Assert.Equal("🔴", vm.Results.First(r => r.RunId == targetRow.RunId).StatusGlyph);

        vm.CycleResultStatusCommand.Execute(vm.Results.First(r => r.RunId == targetRow.RunId));
        Assert.Equal("Aceito", vm.Results.First(r => r.RunId == targetRow.RunId).Status);
        Assert.Equal("🟢", vm.Results.First(r => r.RunId == targetRow.RunId).StatusGlyph);
    }

    [Fact]
    public void PowerTestViewModel_step7_reprocessing_recalculates_adimensionals_without_flooding_or_altering_raw_measurements()
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Impellers = [new Impeller { DiameterM = 0.06, BladeCount = 6 }]
        };
        var doc = _store.CreateTest("Step7-Reprocess-Test", new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 }, geometry, new PowerTestSettings());

        // Add 1 ungassed run + 3 gassed runs
        var p0Run = new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            AgitationRpm = 300.0,
            MeanRpmMeasured = 300.0,
            NetPowerW = 10.0,
            MeanShaftPowerW = 10.0,
            GasMode = PowerGasMode.Ungassed,
            Phase = PowerRunPhase.Accepted,
            Analysis = new PowerPointResult { AssemblyReynoldsNumber = 18000.0, AssemblyPowerNumber = 5.0 },
        };
        doc.Runs.Add(p0Run);

        for (var i = 1; i <= 3; i++)
        {
            doc.Runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10 + i),
                AgitationRpm = 300.0,
                MeanRpmMeasured = 300.0,
                NetPowerW = 10.0 * (1.0 - i * 0.1),
                MeanShaftPowerW = 10.0 * (1.0 - i * 0.1),
                GasMode = PowerGasMode.Gassed,
                GasFlowLpm = i * 2.0,
                Phase = PowerRunPhase.Accepted,
            });
        }
        _store.SaveTestManifest(doc);

        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        var initialP0Row = vm.Results.First(r => !r.IsGassed);
        var initialRe = initialP0Row.ReynoldsNumber;
        Assert.Null(vm.CurrentTest!.Flooding);

        // Change fluid density to 1200 kg/m3 -> Re and Np should change reactively
        vm.DensityKgM3 = 1200.0;

        var reprocessedP0Row = vm.Results.First(r => !r.IsGassed);
        Assert.True(reprocessedP0Row.ReynoldsNumber > initialRe); // Re is proportional to density

        // Geometry changes still trigger scientific reprocessing, without restoring flooding data.
        vm.VesselDiameterMm = 250.0;
        Assert.Null(vm.CurrentTest!.Flooding);

        // Verify raw measurements (NetPowerW, MeanRpmMeasured) remained unaltered in document
        var reloaded = _store.LoadTest(doc.FolderName);
        Assert.NotNull(reloaded);
        Assert.Equal(10.0, reloaded.Runs[0].NetPowerW);
        Assert.Equal(300.0, reloaded.Runs[0].MeanRpmMeasured);
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

        public void Emit(SensorSnapshot s)
        {
            Latest = s;
            TelemetryReceived?.Invoke(s);
        }

        public void Connect() { }
        public void ConnectUsb(string portName) { }
        public void ConnectWiFi(string ipAddress) { }
        public void Disconnect() { }
        public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Send(OpenTECCommand command) { CommandSent?.Invoke(command.ToJson()); }
        public void ZeroSessionTime() { }
    }
}
