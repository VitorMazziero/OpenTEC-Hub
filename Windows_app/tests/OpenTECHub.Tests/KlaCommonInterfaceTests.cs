using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed partial class KlaDeterminationViewModelTests
{
    private void CreateCommonSession(bool biotic, bool single)
    {
        _vm.IsBiotic = biotic; _vm.IsSingleCapture = single;
        _vm.NewTestName = $"E4-{biotic}-{single}";
        _vm.NewConditionRpm = 500; _vm.NewConditionFlow = 3;
        _vm.CreateNewTest();
        Assert.NotNull(_vm.CurrentTest);
        if (!single) _vm.AddManualCondition();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task E4_FourModesPersistAndStartTheirOwnProtocol(bool biotic, bool single)
    {
        CreateCommonSession(biotic, single);
        var loaded = _store.LoadTest(_vm.CurrentTest!.FolderName)!;
        Assert.Equal(biotic ? KlaAssayProtocol.Biotic : KlaAssayProtocol.Abiotic, loaded.EffectiveProtocol);
        Assert.Equal(single ? KlaCaptureMode.Single : KlaCaptureMode.Multiple, loaded.EffectiveCaptureMode);
        Assert.Null(loaded.LinkedMap);
        Assert.Equal(100, loaded.ProtocolSettings!.OxygenRemovalAgitationRpm);
        Assert.Equal(30, loaded.ProtocolSettings.OperatingRange!.MinimumOperatingDoPercent);
        _vm.NitrogenIsolationConfirmed = biotic; _vm.NitrogenSourceConfirmed = !biotic;
        if (single) await _vm.StartSingleAsync(); else await _vm.StartSequenceAsync();
        Assert.NotNull(_runner.CurrentCondition);
        Assert.Equal(500, _runner.CurrentCondition.AgitationRpm);
        Assert.Equal(biotic, _vm.CurrentTest.NitrogenIsolationConfirmedUtc.HasValue);
        Assert.Equal(!biotic, _vm.CurrentTest.NitrogenSourceConfirmedUtc.HasValue);
    }

    [Fact]
    public async Task E4_BioticNeverAcceptsOpenNitrogenAsIsolation()
    {
        CreateCommonSession(true, true);
        _vm.NitrogenSourceConfirmed = true;
        await _vm.StartSingleAsync();
        Assert.Null(_runner.CurrentRun);
        Assert.Contains("isolado", _vm.StatusMessage);
    }

    [Fact]
    public void E4_DraftChangesDoNotReinterpretOrMutateAnExistingSession()
    {
        CreateCommonSession(false, false);
        var old = _vm.CurrentTest!;
        _vm.IsBiotic = true;
        Assert.True(_vm.IsCreateDialogOpen);
        _vm.SettingDOMin = 10;
        Assert.Equal(KlaAssayProtocol.Abiotic, old.EffectiveProtocol);
        Assert.Equal(15, old.Settings.DOMinPercent);
        _vm.CloseCreateDialog();
        Assert.False(_vm.IsBiotic);
        Assert.Equal(15, _vm.SettingDOMin);
    }

    private async Task SimulateCommonRecovery(bool biotic, bool single = true)
    {
        CreateCommonSession(biotic, single);
        _vm.NitrogenIsolationConfirmed = biotic; _vm.NitrogenSourceConfirmed = !biotic;
        if (single) await _vm.StartSingleAsync(); else await _vm.StartSequenceAsync();
        var run = _runner.CurrentRun!;
        run.Definition = KlaRunDefinition.Create(_vm.CurrentTest!, _runner.CurrentCondition!, 1);
        run.FolderName = _store.InitializeRunFolder(_vm.CurrentTest!.FolderName, run);
        run.Outcome = new() { Restoration = biotic ? KlaRestorationState.Pending : KlaRestorationState.NotRequired };
        if (biotic) run.GasEvents.Add(new(0, KlaGasEventKind.GasOffConfirmed, "simulated_air_vent"));
        run.GasEvents.Add(new(biotic ? 100 : 0, KlaGasEventKind.GasOnConfirmed, "simulated_air_reactor"));
        _store.SaveRunGasEvents(_vm.CurrentTest.FolderName, run.FolderName, run.GasEvents);
        _vm.LivePoints.Clear();
        for (var t = 0; t <= (biotic ? 300 : 200); t += 2)
        {
            var off = biotic && t < 100;
            var oxygen = off ? 60 - .5 * t : (biotic ? 75 - 65 * Math.Exp(-.02 * (t - 100)) : 100 - 90 * Math.Exp(-.02 * t));
            var rpm = off ? 100 : 500;
            _vm.LivePoints.Add(new(DateTimeOffset.UtcNow.AddSeconds(t), t,
                off ? RunPhase.MeasuringConsumption : RunPhase.Reoxygenating,
                20000 + t, oxygen, 3, 3, rpm, false, false, false, TemperatureC: 30, RpmMeasured: rpm));
        }
        run.RawDataSha256 = _store.SaveRunRawData(_vm.CurrentTest.FolderName, run.FolderName, _vm.LivePoints);
        _runner.Phase = RunPhase.Reviewing; _runner.RaiseStateChanged();
        Assert.NotNull(_vm.CurrentAnalysis?.DeterministicResult);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task E4_FourModesUseCalibratedOxygenAndSharedAnalysis(bool biotic, bool single)
    {
        await SimulateCommonRecovery(biotic, single);
        var result = _vm.CurrentAnalysis!.DeterministicResult!;
        Assert.InRange(result.KlaPerHour!.Value, 71.99, 72.01);
        Assert.InRange(result.Equilibrium.Percent!.Value, biotic ? 74.999 : 99.999, biotic ? 75.001 : 100.001);
        Assert.All(result.Input!.Samples, p => Assert.True(p.CalibratedDoPercent < 110 && p.Adc > 19000));
        Assert.Equal(biotic, _vm.ShowOur);
        if (biotic)
        {
            Assert.InRange(result.Our.PercentPointsPerHour!.Value, 1799.99, 1800.01);
            Assert.Equal(KlaScientificQuality.Conditional, result.Our.Quality);
            Assert.False(_vm.CanDecideRun);
            _runner.CurrentRun!.Outcome = new() { Restoration = KlaRestorationState.Confirmed };
            _runner.RaiseStateChanged();
            Assert.True(_vm.CanDecideRun);
        }
        Assert.NotEmpty(_vm.ReviewPhases);
        Assert.NotEmpty(_vm.LogLinearSeries);
    }

    [Fact]
    public async Task E4_ManualReanalysisCreatesRevisionsAndPreservesPhysicalOutcome()
    {
        await SimulateCommonRecovery(true);
        _runner.CurrentRun!.Outcome = new() { Restoration = KlaRestorationState.Confirmed };
        _vm.ResidualTransferVerified = true; _vm.ConsumptionRepresentative = true; _vm.ProbeResponseVerified = true;
        _vm.PhysicalSaturationSource = "analytic reference"; _vm.PhysicalSaturation = 100;
        _vm.ReferenceConcentrationSource = "analytic reference"; _vm.ReferenceConcentration = .25;
        _vm.RecomputeReviewAnalysis();
        Assert.Equal(KlaScientificQuality.Valid, _vm.CurrentAnalysis!.DeterministicResult!.Our.Quality);
        Assert.Equal(KlaRestorationState.Confirmed, _vm.CurrentAnalysis.Outcome!.Restoration);
        Assert.InRange(_vm.CurrentAnalysis.Outcome.OurMmolPerLPerHour!.Value, 4.499, 4.501);
        await _vm.SaveReviewRevisionAsync();
        var folder = _runner.CurrentRun.FolderName;
        var first = _store.LoadRunAnalysis(_vm.CurrentTest!.FolderName, folder)!;
        Assert.Equal(1, first.RevisionNumber);
        var revisionPath = Path.Combine(_store.RootDirectory, _vm.CurrentTest.FolderName, "Corridas", folder, "analise-rev-001.json");
        var before = File.ReadAllBytes(revisionPath);
        _vm.ReviewManualWindow = true;
        _vm.ReviewTStart = 120; _vm.ReviewTEnd = 200;
        _vm.RecomputeReviewAnalysis();
        await _vm.SaveReviewRevisionAsync();
        Assert.Equal(before, File.ReadAllBytes(revisionPath));
        Assert.Equal(2, _store.LoadRunAnalysis(_vm.CurrentTest.FolderName, folder)!.RevisionNumber);
        Assert.Equal(KlaOperatorDecision.Pending, _vm.CurrentAnalysis.Outcome.OperatorDecision);
        Assert.Equal("operator_window_v1", _vm.CurrentAnalysis.DeterministicResult.Input!.RecoveryWindow!.Origin);
    }

    [Fact]
    public async Task E4_InconclusiveRateDisplaysUnavailableRatherThanCompatibilityZero()
    {
        await SimulateCommonRecovery(false);
        _vm.ReviewManualWindow = true; _vm.ReviewTStart = 1000; _vm.ReviewTEnd = 900;
        _vm.RecomputeReviewAnalysis();
        Assert.Null(_vm.CurrentAnalysis!.DeterministicResult!.KlaPerHour);
        Assert.Equal("—", _vm.DisplayReviewKla);
        Assert.False(_vm.CanAcceptAnalysis);
    }

    [Fact]
    public async Task E7_RejectedPhysicalBalanceDoesNotDisplayTheCandidateRateIntervalAsAnAvailableResult()
    {
        await SimulateCommonRecovery(true);
        _runner.CurrentRun!.Outcome = new() { Restoration = KlaRestorationState.Confirmed };
        _vm.ResidualTransferVerified = true;
        _vm.ConsumptionRepresentative = true;
        _vm.ProbeResponseVerified = true;
        _vm.PhysicalSaturation = 90;
        _vm.PhysicalSaturationSource = "synthetic independent saturation deliberately inconsistent with OUR";
        _vm.RecomputeReviewAnalysis();
        var result = _vm.CurrentAnalysis!.DeterministicResult!;
        Assert.Equal(KlaScientificQuality.Inconclusive, result.KlaQuality);
        Assert.Contains("independent_our_and_physical_balance_disagree", result.Reasons);
        Assert.Null(result.KlaPerHour);
        Assert.NotNull(result.ConditionalCi95Low); // candidate diagnostic stays available in the audit
        Assert.Equal("—", _vm.DisplayReviewKla);
        Assert.Equal("IC 95%: [—; —]", _vm.DisplayReviewCi95);
        Assert.False(_vm.CanAcceptAnalysis);
    }
}
