using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Explicit bridge from persisted calibrated CSV + confirmed events, independent of UI/maps.</summary>
public static class KlaDeterministicRequestFactory
{
    public static KlaDeterministicRequest FromRun(KlaRunDefinition definition,
        IReadOnlyList<KlaRawDataPoint> points, IReadOnlyList<KlaGasEvent> confirmedEvents,
        KlaDeterministicConfig? config = null, string rawDataSha256 = "")
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(confirmedEvents);
        // Post-restoration observations belong to the cultivation rather than the assay model.
        var episode = points.TakeWhile(p => p.Phase != RunPhase.RestoringCultivation).ToArray();
        var cfg = config ?? new();
        var initialTemperature = episode.FirstOrDefault()?.TemperatureC;
        var hasMeasuredConditions = episode.Length > 0 && episode.All(p => p.RpmMeasured.HasValue &&
            double.IsFinite(p.RpmMeasured.Value) && double.IsFinite(p.FlowMeasured) && p.FlowMeasured >= 0 &&
            p.TemperatureC.HasValue && double.IsFinite(p.TemperatureC.Value));
        return new()
        {
            Protocol = definition.Protocol,
            RemovalMode = definition.Protocol == KlaAssayProtocol.Biotic ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping,
            Probe = definition.ProtocolSettings.Probe, Config = cfg,
            Events = confirmedEvents.ToImmutableArray(), RawDataSha256 = rawDataSha256,
            ProcessConditionsIndependentlyVerified = hasMeasuredConditions,
            Samples = episode.Select(p => new KlaObservation(p.RelativeSeconds, p.DOFiltered,
                IsValid: double.IsFinite(p.DOFiltered) && p.DOFiltered >= 0,
                ConditionStable: (p.RpmMeasured is null || Math.Abs(p.RpmMeasured.Value - p.AgitationSetpoint) <= definition.ProtocolSettings.ReturnAgitationToleranceRpm) &&
                    (p.Phase is not RunPhase.Reoxygenating || Math.Abs(p.FlowMeasured - definition.Condition.AirflowLpm) <= definition.ProtocolSettings.ReturnFlowToleranceLpm) &&
                    (initialTemperature is null || p.TemperatureC is null || Math.Abs(p.TemperatureC.Value - initialTemperature.Value) <= cfg.MaximumTemperatureChangeC),
                Adc: p.DORaw)).ToImmutableArray(),
        };
    }
}
