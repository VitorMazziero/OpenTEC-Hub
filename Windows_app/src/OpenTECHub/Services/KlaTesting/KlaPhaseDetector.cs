using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Causal labels: only confirmed events and observations at or before each index.</summary>
public static class KlaPhaseDetector
{
    public static ImmutableArray<KlaPhasePoint> Detect(KlaDeterministicRequest request)
    {
        var result = ImmutableArray.CreateBuilder<KlaPhasePoint>();
        var eventIndex = 0;
        KlaGasEvent? current = null;
        var cycle = 0;
        var steadySince = double.NaN;
        var steady = false;
        for (var i = 0; i < request.Samples.Length; i++)
        {
            var sample = request.Samples[i];
            while (eventIndex < request.Events.Length && request.Events[eventIndex].Seconds <= sample.Seconds)
            {
                current = request.Events[eventIndex++];
                if (current.Kind == KlaGasEventKind.GasOffConfirmed)
                {
                    cycle++;
                }
                steadySince = double.NaN; steady = false;
            }
            var phase = KlaScientificPhase.Steady;
            var origin = "before_confirmed_event";
            if (current is not null)
            {
                var transientSpan = Math.Max(request.Config.TransientSeconds, 3 * (request.Probe.ResponseTimeSeconds ?? 0));
                var transient = sample.Seconds - current.Seconds < transientSpan || !sample.ConditionStable;
                if (current.Kind == KlaGasEventKind.GasOffConfirmed)
                {
                    phase = transient ? KlaScientificPhase.GasOffTransient : KlaScientificPhase.GasOffConsumption;
                }
                else
                {
                    phase = transient ? KlaScientificPhase.GasOnTransient : KlaScientificPhase.GasOnRecovery;
                    if (!transient)
                    {
                        var start = i;
                        while (start > 0 && request.Samples[start - 1].Seconds >=
                            Math.Max(current.Seconds + transientSpan,
                                sample.Seconds - request.Config.StabilitySpanSeconds))
                        {
                            start--;
                        }
                        var past = request.Samples.Skip(start).Take(i - start + 1).ToArray();
                        var span = sample.Seconds - past[0].Seconds;
                        var valid = past.All(x => x.IsValid && x.IsNewOxygenSample && x.ConditionStable);
                        var fit = valid ? KlaNumerics.Ols(past.Select(x => x.Seconds).ToArray(),
                            past.Select(x => x.CalibratedDoPercent).ToArray()) : null;
                        var threshold = request.Config.StableSlopePercentPerSecond *
                            (steady ? request.Config.StableSlopeHysteresis : 1);
                        if (span >= request.Config.StabilitySpanSeconds &&
                            ((fit is not null && Math.Abs(fit.Slope) <= threshold) ||
                             (valid && past.All(x => x.CalibratedDoPercent == past[0].CalibratedDoPercent))))
                        {
                            if (double.IsNaN(steadySince))
                            {
                                steadySince = sample.Seconds;
                            }
                            steady = sample.Seconds - steadySince >= request.Config.StabilitySpanSeconds;
                        }
                        else
                        {
                            steadySince = double.NaN; steady = false;
                        }
                        if (steady)
                        {
                            phase = KlaScientificPhase.Steady;
                        }
                    }
                }
                origin = $"confirmed_event:{current.Evidence};causal_rule_v1";
            }
            result.Add(new(i, phase, origin, cycle));
        }
        return result.ToImmutable();
    }
}
