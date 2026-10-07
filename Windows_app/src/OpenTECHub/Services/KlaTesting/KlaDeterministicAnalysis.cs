using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Pure shared pipeline: confirmed events → phases → equilibrium → qualified window → raw OLS.</summary>
public static class KlaDeterministicAnalysis
{
    public const string Version = "OpenTecDeterministicKlaV1";

    public static KlaDeterministicResult Analyze(KlaDeterministicRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Config);
        ArgumentNullException.ThrowIfNull(request.Probe);
        request.Config.Validate();
        var empty = new KlaDeterministicResult { Input = request, Config = request.Config };
        var inputReason = ValidateInput(request);
        if (inputReason is not null)
        {
            return empty with { Reasons = [inputReason] };
        }
        var phases = request.PhaseOverride.IsEmpty ? KlaPhaseDetector.Detect(request) : request.PhaseOverride;
        var recovery = phases.Where(x => x.Phase == KlaScientificPhase.GasOnRecovery).ToArray();
        var ourWindow = request.OurWindow;
        if (ourWindow is null)
        {
            var off = phases.Where(x => x.Phase == KlaScientificPhase.GasOffConsumption).ToArray();
            if (off.Length > 0)
            {
                ourWindow = new(off[0].Index, off[^1].Index, "confirmed_respiratory_phase_v1");
            }
        }
        var our = KlaOurEstimator.Estimate(request, ourWindow);
        if (request.RemovalMode == KlaGasRemovalMode.Respiration && ourWindow is not null &&
            (ourWindow.Start < 0 || ourWindow.End >= phases.Length || ourWindow.End < ourWindow.Start ||
             phases.Skip(ourWindow.Start).Take(ourWindow.Count).Any(x => x.Phase != KlaScientificPhase.GasOffConsumption)))
        {
            our = new(null, null, KlaScientificQuality.Inconclusive, ourWindow, null, null, null, ["our_window_outside_respiration_phase"]);
        }
        empty = empty with { Phases = phases, Our = our };
        if (recovery.Length < request.Config.MinimumPoints)
        {
            return empty with { Reasons = ["insufficient_confirmed_recovery"] };
        }
        var start = recovery[0].Index;
        var end = recovery[^1].Index;
        if (phases.Skip(start).Take(end - start + 1).Any(x => x.Phase is not (KlaScientificPhase.GasOnRecovery or KlaScientificPhase.Steady)))
        {
            return empty with { Reasons = ["noncontinuous_recovery_episode"] };
        }
        // Only the immediate post-recovery steady hold belongs to this equilibrium.
        while (end + 1 < phases.Length && phases[end + 1].Phase == KlaScientificPhase.Steady)
        {
            end++;
        }
        var ceq = KlaEquilibriumEstimator.Fit(request, new(start, end, "recovery_and_immediate_hold_v1"));
        empty = empty with { Equilibrium = ceq };
        if (!ceq.Converged || ceq.Percent is null)
        {
            return empty with { Reasons = [ceq.Reason] };
        }
        var candidates = KlaWindowSelector.Search(request, phases, ceq.Percent.Value, ceq.StandardError ?? 0);
        // Rank by duration, then information (never by the most convenient reported kLa).
        var selected = candidates.Where(x => x.Selectable)
            .OrderByDescending(x => request.Samples[x.Window.End].Seconds - request.Samples[x.Window.Start].Seconds)
            .ThenByDescending(x => x.SignalToNoise).ThenBy(x => x.Window.Start).FirstOrDefault();
        empty = empty with { CandidateWindows = candidates, SelectedWindow = selected };
        if (selected?.Regression is not { } fit || selected.RatePerHour is not { } rate)
        {
            return empty with { Reasons = candidates.Length == 1 ? candidates[0].Reasons : ["no_qualified_contiguous_window"] };
        }
        var reasons = ImmutableArray.CreateBuilder<string>();
        var quality = KlaScientificQuality.Valid;
        if (!request.ProcessConditionsIndependentlyVerified)
        {
            quality = KlaScientificQuality.Conditional;
            reasons.Add("constant_process_conditions_not_independently_verified");
        }
        if (request.Probe.ResponseTimeSeconds is { } tau && rate / 3600 * tau > request.Config.MaximumProbeRateRatio)
        {
            quality = KlaScientificQuality.Inconclusive;
            reasons.Add("transfer_and_probe_response_not_separately_identifiable");
        }
        else if (request.Probe.ResponseTimeSeconds is null && !request.ProbeResponseNegligibleIndependentlyVerified)
        {
            quality = KlaScientificQuality.Conditional;
            reasons.Add("unknown_probe_response_rate_is_conditional");
        }
        if (request.Protocol == KlaAssayProtocol.Biotic &&
            (our.Quality != KlaScientificQuality.Valid || !request.ConsumptionRepresentativeOfRecovery))
        {
            if (quality != KlaScientificQuality.Inconclusive) { quality = KlaScientificQuality.Conditional; }
            reasons.Add("constant_representative_our_not_independently_verified");
        }
        double? predictedOur = null;
        if (request.PhysicalSaturationPercent is { } saturation && !string.IsNullOrWhiteSpace(request.PhysicalSaturationSource))
        {
            predictedOur = rate * (saturation - ceq.Percent.Value);
            var referenceOur = request.Protocol == KlaAssayProtocol.Abiotic ? 0 : our.PercentPointsPerHour;
            if (referenceOur.HasValue && (request.Protocol == KlaAssayProtocol.Abiotic ||
                (our.Quality == KlaScientificQuality.Valid && request.ConsumptionRepresentativeOfRecovery)))
            {
                var scale = Math.Max(1, Math.Max(Math.Abs(referenceOur.Value), rate * Math.Abs(saturation)));
                // Compare biotic rate against measured OUR; for zero OUR use the physical transfer scale.
                if (request.Protocol == KlaAssayProtocol.Biotic) { scale = Math.Max(1, Math.Abs(referenceOur.Value)); }
                var uncertainty = rate * (ceq.StandardError ?? 0) +
                    (our.ConditionalCi95High - our.ConditionalCi95Low ?? 0) / 2;
                if (predictedOur < 0 || Math.Abs(predictedOur.Value - referenceOur.Value) >
                    request.Config.BalanceRelativeTolerance * scale + uncertainty)
                {
                    quality = KlaScientificQuality.Inconclusive;
                    reasons.Add("independent_our_and_physical_balance_disagree");
                }
            }
        }
        else if (request.Protocol == KlaAssayProtocol.Biotic)
        {
            reasons.Add("physical_saturation_unavailable_balance_not_checked");
        }
        var half = 3600 * fit.SlopeStandardError * KlaNumerics.Student95(selected.Window.Count - 2);
        if (Math.Abs(fit.Lag1Autocorrelation) > .2 || request.ManualEquilibriumPercent.HasValue)
        {
            if (quality == KlaScientificQuality.Valid) { quality = KlaScientificQuality.Conditional; }
            reasons.Add("ols_interval_is_conditional_on_equilibrium_and_correlated_errors");
        }
        return empty with
        {
            KlaPerHour = quality == KlaScientificQuality.Inconclusive ? null : rate,
            KlaQuality = quality, ConditionalCi95Low = rate - half, ConditionalCi95High = rate + half,
            BalancePredictedOurPercentPointsPerHour = predictedOur,
            RateDiagnostics = Diagnostics(request, selected.Window, ceq.Percent.Value, our),
            Reasons = reasons.ToImmutable(),
        };
    }

    private static string? ValidateInput(KlaDeterministicRequest r)
    {
        if (!Enum.IsDefined(r.Protocol) || !Enum.IsDefined(r.RemovalMode) || r.Samples.IsDefault ||
            r.Events.IsDefault || r.PhaseOverride.IsDefault || r.Samples.Length < r.Config.MinimumPoints)
        {
            return "invalid_protocol_or_insufficient_samples";
        }
        if ((r.Protocol == KlaAssayProtocol.Biotic) != (r.RemovalMode == KlaGasRemovalMode.Respiration))
        {
            return "protocol_removal_mode_mismatch";
        }
        for (var i = 0; i < r.Samples.Length; i++)
        {
            var x = r.Samples[i];
            if (!double.IsFinite(x.Seconds) || !double.IsFinite(x.CalibratedDoPercent) || !x.IsValid || !x.IsNewOxygenSample || x.CalibratedDoPercent < 0)
            {
                return "invalid_or_repeated_oxygen_observation";
            }
            if (i > 0 && x.Seconds <= r.Samples[i - 1].Seconds) { return "time_not_strictly_monotonic"; }
            if (i > 0 && x.Seconds - r.Samples[i - 1].Seconds > r.Config.MaximumSampleGapSeconds) { return "oxygen_sample_gap_exceeds_configured_span"; }
        }
        for (var i = 0; i < r.Events.Length; i++)
        {
            var x = r.Events[i];
            if (!Enum.IsDefined(x.Kind) || !double.IsFinite(x.Seconds) || string.IsNullOrWhiteSpace(x.Evidence) ||
                (i > 0 && (x.Seconds <= r.Events[i - 1].Seconds || x.Kind == r.Events[i - 1].Kind)))
            {
                return "invalid_or_unconfirmed_gas_event";
            }
        }
        if (r.Events.Count(x => x.Kind == KlaGasEventKind.GasOnConfirmed) != 1 ||
            r.Events.Count(x => x.Kind == KlaGasEventKind.GasOffConfirmed) > 1)
        {
            return "one_recovery_episode_required";
        }
        if (r.Events[^1].Kind != KlaGasEventKind.GasOnConfirmed || r.Events[^1].Seconds > r.Samples[^1].Seconds)
        {
            return "recovery_event_outside_episode";
        }
        if (!r.PhaseOverride.IsEmpty && (r.PhaseOverride.Length != r.Samples.Length ||
            r.PhaseOverride.Where((x, i) => x.Index != i || !Enum.IsDefined(x.Phase) || string.IsNullOrWhiteSpace(x.Origin)).Any()))
        {
            return "invalid_manual_phase_provenance";
        }
        if (!r.PhaseOverride.IsEmpty)
        {
            var gasOn = r.Events.Single(x => x.Kind == KlaGasEventKind.GasOnConfirmed).Seconds;
            var gasOff = r.Events.FirstOrDefault(x => x.Kind == KlaGasEventKind.GasOffConfirmed)?.Seconds;
            if (r.PhaseOverride.Any(x =>
                (x.Phase is KlaScientificPhase.GasOnRecovery or KlaScientificPhase.GasOnTransient && r.Samples[x.Index].Seconds < gasOn) ||
                (x.Phase is KlaScientificPhase.GasOffConsumption or KlaScientificPhase.GasOffTransient &&
                    (gasOff is null || r.Samples[x.Index].Seconds < gasOff || r.Samples[x.Index].Seconds >= gasOn))))
            {
                return "manual_phase_conflicts_with_confirmed_gas_route";
            }
        }
        if (r.Probe.ResponseTimeSeconds is { } tau && (!double.IsFinite(tau) || tau <= 0)) { return "invalid_probe_response_time"; }
        if (r.PhysicalSaturationPercent is { } sat && (!double.IsFinite(sat) || sat <= 0)) { return "invalid_physical_saturation"; }
        if (r.ReferenceConcentrationMmolPerL is { } concentration && (!double.IsFinite(concentration) || concentration <= 0)) { return "invalid_concentration_reference"; }
        return null;
    }

    private static ImmutableArray<KlaRateDiagnostic> Diagnostics(KlaDeterministicRequest request,
        KlaIndexWindow window, double ceq, KlaOurEstimate our)
    {
        var result = ImmutableArray.CreateBuilder<KlaRateDiagnostic>();
        for (var i = window.Start; i <= window.End; i++)
        {
            if (i == window.Start || i == window.End)
            {
                result.Add(new(i, null, null, "no_centered_derivative_at_window_boundary"));
                continue;
            }
            var x = request.Samples[i];
            var derivative = (request.Samples[i + 1].CalibratedDoPercent - request.Samples[i - 1].CalibratedDoPercent) /
                (request.Samples[i + 1].Seconds - request.Samples[i - 1].Seconds);
            var ratio = 3600 * derivative / (ceq - x.CalibratedDoPercent);
            double? balance = null;
            if (request.PhysicalSaturationPercent is { } sat && !string.IsNullOrWhiteSpace(request.PhysicalSaturationSource) &&
                sat - x.CalibratedDoPercent > request.Config.MinimumDrivingForcePercent &&
                (request.Protocol == KlaAssayProtocol.Abiotic ||
                    (our.Quality == KlaScientificQuality.Valid && request.ConsumptionRepresentativeOfRecovery)))
            {
                balance = (3600 * derivative + (our.PercentPointsPerHour ?? 0)) / (sat - x.CalibratedDoPercent);
            }
            result.Add(new(i, ratio, balance, "raw_centered_secant_diagnostic_v1"));
        }
        return result.ToImmutable();
    }
}
