namespace OpenTECHub.Services.Control;

/// <summary>
/// A single evaluation of the cascade PID, decomposed so every contribution is visible.
/// </summary>
public readonly record struct CascadeTerms(
    double Error,
    double Proportional,
    double Integral,
    double Derivative,
    double DeltaOutput,
    double Output,
    double PredictedMeasurement,
    double MeasurementRate,
    bool Saturated,
    double RateSetpoint = 0.0,
    double GainFactor = 1.0)
{
    /// <summary>The neutral reading before the loop has run, or after a reset.</summary>
    public static CascadeTerms Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, false, 0, 1.0);
}
