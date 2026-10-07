using System;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaMeasurementImportPolicy
{
    public static string? Refusal(KlaTestDocument doc, KlaTestRunSummary run, KlaAnalysisRevision analysis,
        KlaAssayProtocol? targetProtocol, KlaMeasurementContext? targetContext, bool targetHasMeasurements)
    {
        if (!KlaSequence.IsAccepted(run) || analysis.Outcome is { OperatorDecision: not KlaOperatorDecision.Accepted })
            return "A corrida não está aceita na revisão atual.";
        var protocol = run.Definition?.Protocol ?? doc.EffectiveProtocol;
        if (!double.IsFinite(analysis.KlaPerHour) || analysis.KlaPerHour <= 0 || analysis.Quality == DecisionQuality.Inconclusive ||
            analysis.DeterministicResult is { } result && (result.KlaPerHour is null || result.KlaQuality is not (KlaScientificQuality.Valid or KlaScientificQuality.Conditional)))
            return "A revisão atual não tem kLa utilizável.";
        if (protocol == KlaAssayProtocol.Biotic && run.EffectiveOutcome.Restoration != KlaRestorationState.Confirmed)
            return "A retomada biótica não está confirmada.";
        var context = run.Context ?? run.Definition?.Context ?? doc.Context;
        if ((protocol == KlaAssayProtocol.Biotic || doc.SequenceLimits is not null) && (context is null || !context.IsDefinedFor(protocol)))
            return "Contexto incompleto nesta corrida. Defina meio, origem e, no biótico, cultivo e janela antes de adquirir pontos para o mapa.";
        if (targetHasMeasurements && targetProtocol is null && context is not null)
            return "O mapa contém pontos sem contexto. Crie outro mapa para estas medições.";
        if (targetProtocol is { } expected && expected != protocol)
            return "O mapa usa outro protocolo de medição.";
        if (targetHasMeasurements && ((context is null) != (targetContext is null) ||
            context is not null && targetContext is not null && !context.CompatibleWith(targetContext)))
            return "Meio, cultivo, janela ou origem de simulação não correspondem ao mapa.";
        return null;
    }
}
