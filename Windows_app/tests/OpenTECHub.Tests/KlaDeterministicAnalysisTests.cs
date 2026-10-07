using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaDeterministicAnalysisTests
{
    private static KlaDeterministicRequest Recovery(double ceq = 100, double origin = 0) => new()
    {
        Protocol = KlaAssayProtocol.Abiotic, RemovalMode = KlaGasRemovalMode.NitrogenStripping,
        Samples = Enumerable.Range(0, 101).Select(i => new KlaObservation(origin + i * 2,
            ceq - (ceq - 10) * Math.Exp(-.02 * i * 2), Adc: 20000 + i)).ToImmutableArray(),
        Events = [new(origin, KlaGasEventKind.GasOnConfirmed, "analytic_confirmed_event")],
        ProbeResponseNegligibleIndependentlyVerified = true,
        ProcessConditionsIndependentlyVerified = true,
    };

    private static KlaDeterministicRequest Biotic() => new()
    {
        Protocol = KlaAssayProtocol.Biotic, RemovalMode = KlaGasRemovalMode.Respiration,
        Samples = Enumerable.Range(0, 151).Select(i =>
        {
            var t = i * 2;
            var oxygen = t < 100 ? 60 - .5 * t : 75 - 65 * Math.Exp(-.02 * (t - 100));
            return new KlaObservation(t, oxygen, Adc: 20000 + i);
        }).ToImmutableArray(),
        Events = [new(0, KlaGasEventKind.GasOffConfirmed, "air_to_vent"), new(100, KlaGasEventKind.GasOnConfirmed, "air_to_reactor")],
        PhysicalSaturationPercent = 100, PhysicalSaturationSource = "independent analytic truth",
        ReferenceConcentrationMmolPerL = .25, ReferenceConcentrationSource = "independent analytic truth",
        ResidualTransferNegligible = true, ConsumptionRepresentativeOfRecovery = true,
        ProbeResponseNegligibleIndependentlyVerified = true, ProcessConditionsIndependentlyVerified = true,
    };

    [Theory]
    [InlineData(100, 0)]
    [InlineData(75, 0)]
    [InlineData(100, 100000)]
    public void IdealRecoveryRecoversRateAndFreeEquilibrium(double ceq, double origin)
    {
        var result = new KlaAnalysisEngine().AnalyzeDeterministic(Recovery(ceq, origin));
        Assert.Equal(KlaScientificQuality.Valid, result.KlaQuality);
        Assert.InRange(result.KlaPerHour!.Value, 71.999, 72.001);
        Assert.InRange(result.Equilibrium.Percent!.Value, ceq - .001, ceq + .001);
        Assert.Equal(KlaScientificQuality.NotApplicable, result.Our.Quality);
        Assert.NotEmpty(result.CandidateWindows);
        Assert.All(result.Phases, p => Assert.NotEmpty(p.Origin));
    }

    [Fact]
    public void BioticUsesRespiratoryEquilibriumWithoutCountingOurTwice()
    {
        var result = KlaDeterministicAnalysis.Analyze(Biotic());
        Assert.Equal(KlaScientificQuality.Valid, result.KlaQuality);
        Assert.InRange(result.KlaPerHour!.Value, 71.999, 72.001);
        Assert.InRange(result.Equilibrium.Percent!.Value, 74.999, 75.001);
        Assert.Equal(KlaScientificQuality.Valid, result.Our.Quality);
        Assert.Equal(1800, result.Our.PercentPointsPerHour!.Value, 8);
        Assert.Equal(4.5, result.Our.MmolPerLPerHour!.Value, 8);
        Assert.InRange(result.BalancePredictedOurPercentPointsPerHour!.Value, 1799.9, 1800.1);
        Assert.All(result.RateDiagnostics.Where(p => p.PhysicalBalancePerHour.HasValue),
            p => Assert.InRange(p.PhysicalBalancePerHour!.Value, 71.9, 72.1));
    }

    [Fact]
    public void PythonFrozenComponentsMatchOriginalOlsOurAndRawEquilibrium()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kla-e3-python-v1.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var t = item.GetProperty("time").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var y = item.GetProperty("oxygen").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var request = Recovery() with
            {
                Samples = t.Select((x, i) => new KlaObservation(x, y[i])).ToImmutableArray(),
                Events = [new(t[0], KlaGasEventKind.GasOnConfirmed, "fixture")],
            };
            var window = new KlaIndexWindow(item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32(), "frozen_python_window");
            var fitted = KlaWindowSelector.Assess(request.Samples, item.GetProperty("ceq").GetDouble(), window, request.Config);
            var expected = item.GetProperty("kernel");
            Assert.Equal(expected.GetProperty("value_h_inv").GetDouble(), fitted.RatePerHour!.Value, 9);
            Assert.Equal(expected.GetProperty("slope_standard_error_s_inv").GetDouble(), fitted.Regression!.SlopeStandardError, 10);
            Assert.Equal(expected.GetProperty("r2").GetDouble(), fitted.Regression.R2, 10);
            var eq = KlaEquilibriumEstimator.Fit(request, new(0, t.Length - 1, "frozen_python_input"));
            var expectedEq = item.GetProperty("equilibrium");
            Assert.True(eq.Converged, eq.Reason);
            Assert.InRange(eq.Percent!.Value, expectedEq.GetProperty("value_percent").GetDouble() - 1e-4,
                expectedEq.GetProperty("value_percent").GetDouble() + 1e-4);
            Assert.InRange(eq.AuxiliaryRatePerSecond!.Value, expectedEq.GetProperty("auxiliary_rate_s_inv").GetDouble() - 1e-6,
                expectedEq.GetProperty("auxiliary_rate_s_inv").GetDouble() + 1e-6);
        }
        foreach (var item in fixture.RootElement.GetProperty("ourCases").EnumerateArray())
        {
            var t = item.GetProperty("time").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var y = item.GetProperty("oxygen").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var request = Biotic() with
            {
                Samples = t.Select((x, i) => new KlaObservation(x, y[i])).ToImmutableArray(),
                RemovalMode = item.GetProperty("mode").GetString() == "respiration" ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping,
            };
            var our = KlaOurEstimator.Estimate(request, new(item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32()));
            var expected = item.GetProperty("output");
            if (expected.GetProperty("valid").GetBoolean())
            {
                Assert.Equal(expected.GetProperty("value_percent_h").GetDouble(), our.PercentPointsPerHour!.Value, 9);
                Assert.Equal(expected.GetProperty("r2").GetDouble(), our.Regression!.R2, 10);
            }
            else
            {
                Assert.Equal(KlaScientificQuality.NotApplicable, our.Quality);
                Assert.Null(our.PercentPointsPerHour);
            }
        }
    }

    [Fact]
    public void FlatRecoveryHasNoFallbackToOneHundredPercent()
    {
        var request = Recovery() with { Samples = Enumerable.Range(0, 40).Select(i => new KlaObservation(i * 2, 40)).ToImmutableArray() };
        var result = KlaDeterministicAnalysis.Analyze(request);
        Assert.Equal(KlaScientificQuality.Inconclusive, result.KlaQuality);
        Assert.Null(result.Equilibrium.Percent);
        Assert.Null(result.KlaPerHour);
    }

    [Fact]
    public void InvalidDeficitRefusesWholeDeclaredWindowWithoutDroppingPoint()
    {
        var samples = Recovery().Samples.SetItem(20, new(40, 100));
        var result = KlaWindowSelector.Assess(samples, 100, new(5, 50), new());
        Assert.False(result.Selectable);
        Assert.Contains("driving_force_at_or_below_floor_in_window", result.Reasons);
        Assert.Null(result.RatePerHour);
    }

    [Fact]
    public void EquilibriumIsNotForcedAboveEveryNoisyPlateauSample()
    {
        var request = Recovery() with
        {
            Samples = Enumerable.Range(0, 501).Select(i => new KlaObservation(i * 2,
                100 - 90 * Math.Exp(-.02 * i * 2) + .05 * Math.Sin(i * 2.37))).ToImmutableArray(),
        };
        var result = KlaDeterministicAnalysis.Analyze(request);
        Assert.True(result.Equilibrium.Converged);
        Assert.InRange(result.Equilibrium.Percent!.Value, 99.99, 100.01);
        Assert.True(request.Samples.Max(x => x.CalibratedDoPercent) > result.Equilibrium.Percent);
        Assert.InRange(result.KlaPerHour!.Value, 71, 73);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DuplicatesNaNAndGapsAreNotInterpolatedOrSilentlyRemoved(int defect)
    {
        var request = Recovery();
        var observation = request.Samples[20];
        observation = defect switch
        {
            0 => observation with { IsNewOxygenSample = false },
            1 => observation with { CalibratedDoPercent = double.NaN },
            2 => observation with { Seconds = request.Samples[19].Seconds },
            _ => observation with { Seconds = observation.Seconds + 30 },
        };
        var result = KlaDeterministicAnalysis.Analyze(request with { Samples = request.Samples.SetItem(20, observation) });
        Assert.Equal(KlaScientificQuality.Inconclusive, result.KlaQuality);
        Assert.Null(result.KlaPerHour);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void UnknownProbeIsGenericAndConditionalWhileKnownSlowProbeIsUnidentifiable()
    {
        var unknown = KlaDeterministicAnalysis.Analyze(Recovery() with
        { Probe = new() { Technology = "any future sensor" }, ProbeResponseNegligibleIndependentlyVerified = false });
        Assert.Equal(KlaScientificQuality.Conditional, unknown.KlaQuality);
        Assert.InRange(unknown.KlaPerHour!.Value, 71.99, 72.01);
        var slow = KlaDeterministicAnalysis.Analyze(Recovery() with { Probe = new() { ResponseTimeSeconds = 20 } });
        Assert.Equal(KlaScientificQuality.Inconclusive, slow.KlaQuality);
        Assert.Contains("transfer_and_probe_response_not_separately_identifiable", slow.Reasons);
    }

    [Fact]
    public void ResidualTransferAndMissingConcentrationAreNotReportedAsVerifiedOur()
    {
        var result = KlaDeterministicAnalysis.Analyze(Biotic() with
        { ResidualTransferNegligible = false, ReferenceConcentrationMmolPerL = null, ReferenceConcentrationSource = null });
        Assert.Equal(KlaScientificQuality.Conditional, result.Our.Quality);
        Assert.Equal(KlaScientificQuality.Conditional, result.KlaQuality);
        Assert.Null(result.Our.MmolPerLPerHour);
        Assert.Contains("apparent_consumption_residual_transfer_not_verified", result.Our.Reasons);
    }

    [Fact]
    public void VariableRespirationIsRefusedEvenWithHighOverallR2()
    {
        var request = Biotic();
        var samples = request.Samples.Select(s => s.Seconds < 100 ? s with
        { CalibratedDoPercent = 60 - .1 * s.Seconds - .003 * s.Seconds * s.Seconds } : s).ToImmutableArray();
        var result = KlaDeterministicAnalysis.Analyze(request with { Samples = samples });
        Assert.Equal(KlaScientificQuality.Inconclusive, result.Our.Quality);
        Assert.Contains("respiration_curved_or_oxygen_limited", result.Our.Reasons);
    }

    [Fact]
    public void IndependentBalanceDisagreementInvalidatesKla()
    {
        var result = KlaDeterministicAnalysis.Analyze(Biotic() with { PhysicalSaturationPercent = 90 });
        Assert.Equal(KlaScientificQuality.Inconclusive, result.KlaQuality);
        Assert.Contains("independent_our_and_physical_balance_disagree", result.Reasons);
        Assert.Equal(KlaScientificQuality.Valid, result.Our.Quality);
    }

    [Fact]
    public void CausalPhaseLabelsAreUnaffectedByFutureObservations()
    {
        var request = Biotic();
        var first = KlaPhaseDetector.Detect(request);
        var prefix = KlaPhaseDetector.Detect(request with { Samples = request.Samples.Take(80).ToImmutableArray() });
        Assert.Equal(first.Take(80), prefix);
        Assert.Contains(first, p => p.Phase == KlaScientificPhase.GasOffTransient);
        Assert.Contains(first, p => p.Phase == KlaScientificPhase.GasOffConsumption);
        Assert.Contains(first, p => p.Phase == KlaScientificPhase.GasOnTransient);
        Assert.Contains(first, p => p.Phase == KlaScientificPhase.GasOnRecovery);
    }

    [Fact]
    public void MultipleRecoveryCyclesMustBeSlicedBeforeAnalysis()
    {
        var result = KlaDeterministicAnalysis.Analyze(Biotic() with
        { Events = [new(0, KlaGasEventKind.GasOffConfirmed, "off"), new(100, KlaGasEventKind.GasOnConfirmed, "on"),
            new(200, KlaGasEventKind.GasOffConfirmed, "off2"), new(240, KlaGasEventKind.GasOnConfirmed, "on2")] });
        Assert.Contains("one_recovery_episode_required", result.Reasons);
    }

    [Fact]
    public void AnalysisRevisionPersistsAuditAndDoesNotInventOperatorAcceptance()
    {
        var result = KlaDeterministicAnalysis.Analyze(Biotic());
        var revision = result.ToRevision(3, DateTimeOffset.UnixEpoch);
        var loaded = KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(revision))!;
        Assert.Equal("OpenTecDeterministicKlaV1", loaded.AnalysisMethod);
        Assert.Equal(KlaOperatorDecision.Pending, loaded.Outcome!.OperatorDecision);
        Assert.Equal(KlaRestorationState.NotRecorded, loaded.Outcome.Restoration);
        Assert.Equal(result.Input!.Samples.Length, loaded.DeterministicResult!.Input!.Samples.Length);
        Assert.Equal(result.CandidateWindows.Length, loaded.DeterministicResult.CandidateWindows.Length);
        Assert.Equal(result.KlaPerHour, loaded.DeterministicResult.KlaPerHour);
    }

    [Fact]
    public void FactoryReadsCalibratedZeroAndNeverUsesAdcAsPercent()
    {
        var document = new KlaTestDocument { Conditions = [new() { AgitationRpm = 400, AirflowLpm = 3 }] };
        var definition = KlaRunDefinition.Create(document, document.Conditions[0], 1);
        var points = Enumerable.Range(0, 101).Select(i => new KlaRawDataPoint(DateTimeOffset.UnixEpoch,
            i * 2, RunPhase.Reoxygenating, 20000 + i, 100 - 100 * Math.Exp(-.02 * i * 2),
            3, 3, 400, false, true, false, 30, 400)).ToArray();
        var request = KlaDeterministicRequestFactory.FromRun(definition, points,
            [new(0, KlaGasEventKind.GasOnConfirmed, "recorded_echo")]);
        Assert.Equal(0, request.Samples[0].CalibratedDoPercent);
        Assert.Equal(20000, request.Samples[0].Adc);
        var result = KlaDeterministicAnalysis.Analyze(request);
        Assert.InRange(result.KlaPerHour!.Value, 71.99, 72.01);
        Assert.Equal(KlaScientificQuality.Conditional, result.KlaQuality); // Probe response unknown.
    }

    [Fact]
    public void ManualWindowAndPhaseProvenanceCannotOverrideConfirmedGasDirection()
    {
        var request = Biotic();
        var wrongWindow = KlaDeterministicAnalysis.Analyze(request with { RecoveryWindow = new(5, 20, "operator") });
        Assert.Null(wrongWindow.KlaPerHour);
        Assert.Contains("window_outside_recovery_phase", wrongWindow.Reasons);
        var phases = KlaPhaseDetector.Detect(request).SetItem(20, new(20, KlaScientificPhase.GasOnRecovery, "operator_correction", 1));
        var wrongPhase = KlaDeterministicAnalysis.Analyze(request with { PhaseOverride = phases });
        Assert.Contains("manual_phase_conflicts_with_confirmed_gas_route", wrongPhase.Reasons);
    }

    [Fact]
    public void InvalidNaNObservationCanBePersistedWithExplicitRefusal()
    {
        var request = Recovery();
        request = request with { Samples = request.Samples.SetItem(20, request.Samples[20] with { CalibratedDoPercent = double.NaN }) };
        var result = KlaDeterministicAnalysis.Analyze(request);
        var read = KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(result.ToRevision(1, DateTimeOffset.UnixEpoch)))!;
        Assert.Equal(KlaScientificQuality.Inconclusive, read.Outcome!.KlaQuality);
        Assert.True(double.IsNaN(read.DeterministicResult!.Input!.Samples[20].CalibratedDoPercent));
    }
}
