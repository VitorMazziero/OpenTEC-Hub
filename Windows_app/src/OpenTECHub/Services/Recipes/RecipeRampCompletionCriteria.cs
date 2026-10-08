namespace OpenTECHub.Services.Recipes;

/// <summary>Frozen operational criteria for final device feedback; oxygen uses the exact controller reference.</summary>
public sealed record RecipeRampCompletionCriteria(double TemperatureToleranceCelsius, double AgitationToleranceRpm,
    double FlowToleranceLpm, double PhTolerance, double PressureToleranceKilopascals, double StabilitySeconds,
    double MaximumSampleGapSeconds, double TimeoutSeconds)
{
    public void Validate()
    {
        foreach (var tolerance in new[] { TemperatureToleranceCelsius, AgitationToleranceRpm, FlowToleranceLpm, PhTolerance, PressureToleranceKilopascals })
            if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("Tolerância de confirmação inválida.");
        if (!double.IsFinite(StabilitySeconds) || !double.IsFinite(MaximumSampleGapSeconds) || !double.IsFinite(TimeoutSeconds) ||
            StabilitySeconds < 0 || MaximumSampleGapSeconds <= 0 || TimeoutSeconds <= StabilitySeconds ||
            MaximumSampleGapSeconds * 1000 > uint.MaxValue - 1 ||
            TimeoutSeconds * 1000 > uint.MaxValue - 1)
            throw new ArgumentException("Tempos de confirmação inválidos.");
    }

    public RecipeRampFrameDestination CreateDestination(RecipeEngine engine, RecipeRampBlockConfiguration configuration, double? phInactiveBand)
    {
        Validate();
        var stability = TimeSpan.FromSeconds(StabilitySeconds);
        var gap = TimeSpan.FromSeconds(MaximumSampleGapSeconds);
        var timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        return new(engine, configuration, new(AgitationToleranceRpm, stability, gap, timeout),
            new(TemperatureToleranceCelsius, stability, gap, timeout), new(FlowToleranceLpm, stability, gap, timeout),
            new(PhTolerance, stability, gap, timeout), new(PressureToleranceKilopascals, stability, gap, timeout), phInactiveBand);
    }
}
