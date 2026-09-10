using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

[Collection("AppPaths")]
public sealed class PowerGuidedProceduresTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerGuidedProceduresTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-guided-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
        _store = new PowerTestStore(AppPaths.PowerTestsDirectory);
    }

    public void Dispose()
    {
        _overrideScope.Dispose();
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void Static_torque_calibration_computes_reference_and_scale_with_zero_offset()
    {
        // m = 0.100 kg, r = 0.050 m, tau_pct = 3.86%, T_nom = 1.27 N*m
        var calib = PowerCalc.ComputeStaticTorqueCalibration(
            massKg: 0.100,
            leverArmM: 0.050,
            torquePercent: 3.86,
            motorRatedTorqueNm: 1.27);

        var expectedRef = 0.100 * PowerCalc.GravityMetersPerSecondSquared * 0.050;
        var expectedMeasured = (3.86 / 100.0) * 1.27;
        var expectedScale = expectedRef / expectedMeasured;

        Assert.Equal(expectedRef, calib.ReferenceNm, 6);
        Assert.Equal(expectedScale, calib.Scale, 5);
        Assert.Equal(0.0, calib.Offset);
        Assert.Equal(0.100, calib.ReferenceMassKg);
        Assert.Equal(0.050, calib.LeverArmM);
        Assert.Equal(1.27, calib.MotorRatedTorqueNm);
    }

    [Theory]
    [InlineData(-0.1, 0.05, 3.86, 1.27)]
    [InlineData(0.1, -0.05, 3.86, 1.27)]
    [InlineData(0.1, 0.05, 0.0, 1.27)]
    [InlineData(0.1, 0.05, 3.86, 0.0)]
    public void Static_torque_calibration_rejects_non_positive_inputs(
        double mass, double arm, double pct, double tNom)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PowerCalc.ComputeStaticTorqueCalibration(mass, arm, pct, tNom));
    }

    [Fact]
    public void Electrical_correlation_fits_affine_line_and_r_squared()
    {
        // P_elec = 1.5 * P_mech + 20
        var points = new List<(double Mech, double Elec)>
        {
            (10.0, 35.0),
            (20.0, 50.0),
            (30.0, 65.0),
            (40.0, 80.0),
        };

        var fit = PowerCalc.FitElectricalCorrelation(points);
        Assert.NotNull(fit);
        Assert.Equal(1.5, fit.Value.Slope, 6);
        Assert.Equal(20.0, fit.Value.Intercept, 6);
        Assert.Equal(1.0, fit.Value.R2, 5);
    }

    [Fact]
    public void Electrical_correlation_returns_null_when_less_than_two_points()
    {
        Assert.Null(PowerCalc.FitElectricalCorrelation([]));
        Assert.Null(PowerCalc.FitElectricalCorrelation([(10.0, 35.0)]));
    }

    [Fact]
    public void ViewModel_tare_flow_updates_status_and_result_mode()
    {
        var doc = _store.CreateTest("Ensaio Tare", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new RecordingDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);

        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.Equal("Sem tara aplicada · modo relativo", vm.TareStatus);
        Assert.Equal("RELATIVO", vm.ResultModeLabel);

        vm.TareMeasurementInfoCommand.Execute(null);
        Assert.True(vm.IsTareAssistantOpen);

        // Apply a tare curve to doc
        doc.Tare = new TareCurve
        {
            Points = [new TarePoint(300, 0.5, 0.05)],
            ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(doc.Geometry),
        };
        _store.SaveTare(doc.FolderName, doc.Tare);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.Contains("Tara compatível", vm.TareStatus, StringComparison.Ordinal);
        Assert.Equal("CALIBRADO", vm.ResultModeLabel);
    }

    [Fact]
    public void ViewModel_single_point_can_be_appended_to_conditions_table()
    {
        var doc = _store.CreateTest("Ensaio Ponto Unico", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new RecordingDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var vm = new PowerTestViewModel(_store, device, arbiter);

        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        vm.SinglePointRpm = 450;
        vm.SinglePointFlowLpm = 2.5;
        vm.AddSinglePointToTestCommand.Execute(null);

        Assert.Contains(vm.Conditions, c => c.AgitationRpm == 450 && c.GasFlowLpm == 2.5 && c.Origin == PowerConditionOrigin.Manual);
        var table = _store.LoadConditionsTable(doc.FolderName);
        Assert.Contains(table, c => c.AgitationRpm == 450 && c.GasFlowLpm == 2.5);
    }
}
