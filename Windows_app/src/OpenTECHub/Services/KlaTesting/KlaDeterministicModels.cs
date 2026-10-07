using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaScientificPhase { Steady, GasOffTransient, GasOffConsumption, GasOnTransient, GasOnRecovery }

/// <summary>One new, unsmoothed calibrated oxygen observation. ADC is never substituted.</summary>
public sealed record KlaObservation(
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double Seconds,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double CalibratedDoPercent,
    bool IsNewOxygenSample = true, bool IsValid = true, bool ConditionStable = true,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double? Adc = null);
public sealed record KlaPhasePoint(int Index, KlaScientificPhase Phase, string Origin, int Cycle);
public sealed record KlaIndexWindow(int Start, int End, string Origin = "operator")
{
    public int Count => End - Start + 1;
}

/// <summary>Development defaults, persisted with each analysis; no equipment/cultivation ranges.</summary>
public sealed record KlaDeterministicConfig
{
    public int MinimumPoints { get; init; } = 8;
    public int MinimumOurPoints { get; init; } = 10;
    public double MinimumSpanSeconds { get; init; } = 10;
    public double MaximumSampleGapSeconds { get; init; } = 10;
    public double TransientSeconds { get; init; } = 6;
    public double StabilitySpanSeconds { get; init; } = 10;
    public double StableSlopePercentPerSecond { get; init; } = 0.02;
    public double StableSlopeHysteresis { get; init; } = 1.5;
    public double MinimumDrivingForcePercent { get; init; } = 0.05;
    public double NoiseFloorPercent { get; init; } = 0.01;
    public double MinimumSignalToNoise { get; init; } = 3;
    public double MinimumR2 { get; init; } = 0.95;
    public double MaximumRelativeSlopeError { get; init; } = 0.20;
    public double MaximumSlopeChange { get; init; } = 0.20;
    public double MaximumResidualAutocorrelation { get; init; } = 0.80;
    public double CeqSensitivityStepPercent { get; init; } = 0.10;
    public double MaximumCeqSensitivity { get; init; } = 0.20;
    public double MaximumCeqPercent { get; init; } = 110;
    public double MinimumRatePerSecond { get; init; } = 0.00001;
    public double MaximumRatePerSecond { get; init; } = 5;
    public double EquilibriumWeightRatio { get; init; } = 5;
    public int MaximumBoundaryCandidates { get; init; } = 48;
    public double MaximumProbeRateRatio { get; init; } = 0.20;
    public double BalanceRelativeTolerance { get; init; } = 0.25;
    public double MaximumTemperatureChangeC { get; init; } = 0.5;

    public void Validate()
    {
        if (MinimumPoints < 6 || MinimumOurPoints < 6 || MaximumBoundaryCandidates < 8)
        {
            throw new ArgumentException("Configuração de amostras/candidatos inválida.");
        }
        foreach (var x in new[] { MinimumSpanSeconds, MaximumSampleGapSeconds, StabilitySpanSeconds,
            StableSlopePercentPerSecond, StableSlopeHysteresis, MinimumDrivingForcePercent, NoiseFloorPercent,
            MinimumSignalToNoise, MaximumRelativeSlopeError, MaximumSlopeChange, MaximumResidualAutocorrelation,
            CeqSensitivityStepPercent, MaximumCeqSensitivity, MaximumCeqPercent, MinimumRatePerSecond,
            MaximumRatePerSecond, EquilibriumWeightRatio, MaximumProbeRateRatio, BalanceRelativeTolerance, MaximumTemperatureChangeC })
        {
            if (!double.IsFinite(x) || x <= 0)
            {
                throw new ArgumentException("Configuração científica deve conter valores finitos positivos.");
            }
        }
        if (!double.IsFinite(TransientSeconds) || TransientSeconds < 0 || !double.IsFinite(MinimumR2) ||
            MinimumR2 < 0 || MinimumR2 > 1 || MinimumRatePerSecond >= MaximumRatePerSecond ||
            EquilibriumWeightRatio < 1 || StableSlopeHysteresis < 1 || MaximumResidualAutocorrelation >= 1)
        {
            throw new ArgumentException("Configuração científica inválida.");
        }
    }
}

[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
public sealed record KlaDeterministicRequest
{
    public KlaAssayProtocol Protocol { get; init; }
    public KlaGasRemovalMode RemovalMode { get; init; }
    public ImmutableArray<KlaObservation> Samples { get; init; } = [];
    public ImmutableArray<KlaGasEvent> Events { get; init; } = [];
    public ImmutableArray<KlaPhasePoint> PhaseOverride { get; init; } = [];
    public KlaDeterministicConfig Config { get; init; } = new();
    public KlaProbeDescription Probe { get; init; } = new();
    public KlaIndexWindow? RecoveryWindow { get; init; }
    public KlaIndexWindow? OurWindow { get; init; }
    public double? ManualEquilibriumPercent { get; init; }
    public double? PhysicalSaturationPercent { get; init; }
    public string? PhysicalSaturationSource { get; init; }
    public double? ReferenceConcentrationMmolPerL { get; init; }
    public string? ReferenceConcentrationSource { get; init; }
    public bool ResidualTransferNegligible { get; init; }
    public bool ConsumptionRepresentativeOfRecovery { get; init; }
    public bool ProbeResponseNegligibleIndependentlyVerified { get; init; }
    public bool ProcessConditionsIndependentlyVerified { get; init; }
    public string RawDataSha256 { get; init; } = "";
}

public sealed record KlaRegression(double Slope, double Intercept, double R2,
    double SlopeStandardError, double ResidualSd, double Lag1Autocorrelation);
public sealed record KlaEquilibriumEstimate(double? Percent, double? StandardError,
    double? AuxiliaryRatePerSecond, double? Amplitude, double? R2, bool Converged,
    string Method, string Reason, KlaIndexWindow? Window);
public sealed record KlaWindowAssessment(KlaIndexWindow Window, bool Selectable,
    KlaRegression? Regression, double? RatePerHour, double? SignalToNoise,
    double? SlopeChange, double? CeqSensitivity, double? EndpointSensitivity,
    ImmutableArray<string> Reasons);
public sealed record KlaOurEstimate(double? PercentPointsPerHour, double? MmolPerLPerHour,
    KlaScientificQuality Quality, KlaIndexWindow? Window, KlaRegression? Regression,
    double? ConditionalCi95Low, double? ConditionalCi95High, ImmutableArray<string> Reasons);
public sealed record KlaRateDiagnostic(int Index, double? EquilibriumRatioPerHour,
    double? PhysicalBalancePerHour, string Reason);
public sealed record KlaDeterministicResult
{
    public string AlgorithmVersion { get; init; } = "OpenTecDeterministicKlaV1";
    public KlaDeterministicConfig Config { get; init; } = new();
    // Preserve assumptions, sensor/reference provenance, samples, events and manual selections.
    public KlaDeterministicRequest? Input { get; init; }
    public ImmutableArray<KlaPhasePoint> Phases { get; init; } = [];
    public KlaEquilibriumEstimate Equilibrium { get; init; } = new(null, null, null, null, null, false, "none", "not_evaluated", null);
    public KlaWindowAssessment? SelectedWindow { get; init; }
    public ImmutableArray<KlaWindowAssessment> CandidateWindows { get; init; } = [];
    public double? KlaPerHour { get; init; }
    public double? ConditionalCi95Low { get; init; }
    public double? ConditionalCi95High { get; init; }
    public KlaScientificQuality KlaQuality { get; init; } = KlaScientificQuality.Inconclusive;
    public KlaOurEstimate Our { get; init; } = new(null, null, KlaScientificQuality.NotEvaluated, null, null, null, null, []);
    public double? BalancePredictedOurPercentPointsPerHour { get; init; }
    public ImmutableArray<KlaRateDiagnostic> RateDiagnostics { get; init; } = [];
    public ImmutableArray<string> Reasons { get; init; } = [];

    public KlaAnalysisRevision ToRevision(int revisionNumber, DateTimeOffset analyzedUtc)
    {
        if (revisionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revisionNumber));
        }
        var fit = SelectedWindow?.Regression;
        var window = SelectedWindow?.Window;
        return new()
        {
            RevisionNumber = revisionNumber, AnalyzedUtc = analyzedUtc,
            DeterministicResult = this, AnalysisMethod = AlgorithmVersion,
            CeqMethod = Equilibrium.Method, CeqPercent = Equilibrium.Percent ?? 0,
            IsCeqManual = Input?.ManualEquilibriumPercent.HasValue == true,
            FittedKappa = Equilibrium.AuxiliaryRatePerSecond, FittedA = Equilibrium.Amplitude,
            CeqFitR2 = Equilibrium.R2, KlaPerHour = KlaPerHour ?? 0,
            TStartSeconds = window is null ? 0 : Input!.Samples[window.Start].Seconds,
            TEndSeconds = window is null ? 0 : Input!.Samples[window.End].Seconds,
            TotalPoints = window?.Count ?? 0, UsedPoints = fit is null ? 0 : window!.Count,
            SlopeBeta1 = fit?.Slope ?? 0, InterceptBeta0 = fit?.Intercept ?? 0,
            SlopeStandardError = fit?.SlopeStandardError ?? 0,
            AnalysisR2 = fit?.R2 ?? 0, AnalysisRmse = fit?.ResidualSd ?? 0,
            ConfidenceInterval95Low = ConditionalCi95Low ?? 0, ConfidenceInterval95High = ConditionalCi95High ?? 0,
            Quality = KlaQuality == KlaScientificQuality.Valid ? DecisionQuality.Acceptable
                : KlaQuality == KlaScientificQuality.Conditional ? DecisionQuality.AcceptableWithWarning : DecisionQuality.Inconclusive,
            RejectionReason = KlaQuality == KlaScientificQuality.Inconclusive ? string.Join("; ", Reasons) : null,
            WarningJustification = KlaQuality == KlaScientificQuality.Conditional ? string.Join("; ", Reasons) : null,
            RawDataSha256 = Input?.RawDataSha256 ?? "",
            Outcome = new()
            {
                KlaQuality = KlaQuality, OurQuality = Our.Quality,
                OurPercentPointsPerHour = Our.PercentPointsPerHour, OurMmolPerLPerHour = Our.MmolPerLPerHour,
                PhysicalSaturationPercent = Input?.PhysicalSaturationPercent,
                ScientificReason = string.Join("; ", Reasons),
            },
        };
    }
}

public interface IKlaDeterministicAnalysisEngine
{
    KlaDeterministicResult AnalyzeDeterministic(KlaDeterministicRequest request);
}
