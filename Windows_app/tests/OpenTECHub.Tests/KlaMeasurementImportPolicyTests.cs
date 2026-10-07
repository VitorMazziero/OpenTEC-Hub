using System;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.KlaMapping;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaMeasurementImportPolicyTests
{
    private static readonly KlaMeasurementContext Context = new() { Medium = "Meio A", CultivationId = "Cultivo 7", TimeWindow = "Fase 1", Source = KlaMeasurementSource.Physical };
    private static KlaTestDocument Document => new() { Protocol = KlaAssayProtocol.Biotic, Context = Context, SequenceLimits = new() };
    private static KlaTestRunSummary Run => new() { Phase = RunPhase.Accepted, Decision = DecisionQuality.Acceptable, Context = Context,
        Outcome = new() { OperatorDecision = KlaOperatorDecision.Accepted, KlaQuality = KlaScientificQuality.Valid, Restoration = KlaRestorationState.Confirmed } };
    private static KlaAnalysisRevision Analysis => new() { KlaPerHour = 40, Quality = DecisionQuality.Acceptable,
        Outcome = new() { OperatorDecision = KlaOperatorDecision.Accepted, KlaQuality = KlaScientificQuality.Valid } };

    [Fact]
    public void Compatible_biotic_context_is_eligible_without_a_map_or_with_the_same_context()
    {
        Assert.Null(KlaMeasurementImportPolicy.Refusal(Document, Run, Analysis, null, null, false));
        Assert.Null(KlaMeasurementImportPolicy.Refusal(Document, Run, Analysis, KlaAssayProtocol.Biotic, Context, true));
    }
    [Theory]
    [InlineData("medium")] [InlineData("cultivation")] [InlineData("window")] [InlineData("simulation")]
    public void Different_context_cannot_be_averaged_into_the_existing_map(string difference)
    {
        var other = difference switch {
            "medium" => Context with { Medium = "Meio B" },
            "cultivation" => Context with { CultivationId = "Cultivo 8" },
            "window" => Context with { TimeWindow = "Fase 2" },
            _ => Context with { Source = KlaMeasurementSource.Simulation },
        };
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(Document, Run, Analysis, KlaAssayProtocol.Biotic, other, true));
    }
    [Fact]
    public void Protocols_and_unknown_manual_points_cannot_be_mixed_silently()
    {
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(Document, Run, Analysis, KlaAssayProtocol.Abiotic, Context, true));
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(Document, Run, Analysis, null, null, true));
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(withContext(), Run with { Context = null }, Analysis, null, null, false));
        static KlaTestDocument withContext() => new() { Protocol = KlaAssayProtocol.Biotic, SequenceLimits = new() };
    }
    [Fact]
    public void A_new_review_or_unconfirmed_return_invalidates_import_eligibility()
    {
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(Document, Run, withOutcome(), null, null, false));
        Assert.NotNull(KlaMeasurementImportPolicy.Refusal(Document, Run with { Outcome = Run.Outcome! with { Restoration = KlaRestorationState.Pending } }, Analysis, null, null, false));
        static KlaAnalysisRevision withOutcome() => new() { KlaPerHour = 40, Quality = DecisionQuality.Acceptable, Outcome = new() { OperatorDecision = KlaOperatorDecision.Pending } };
    }
    [Fact]
    public void Context_is_part_of_scientific_fingerprint_and_one_point_stays_unidentifiable()
    {
        var snapshot = new KlaExperimentSnapshot { MeasurementProtocol = KlaAssayProtocol.Biotic, MeasurementContext = Context, Anchors = [new(3, 400, 40)] };
        Assert.NotEqual(snapshot.ScientificFingerprint(), (snapshot with { MeasurementContext = Context with { TimeWindow = "Fase 2" } }).ScientificFingerprint());
        Assert.NotEmpty(new KlaMappingEngine().Validate(snapshot));
    }
}
