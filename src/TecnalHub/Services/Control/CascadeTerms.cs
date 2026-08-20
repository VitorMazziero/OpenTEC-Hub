namespace TecnalHub.Services.Control;

/// <summary>
/// A single evaluation of the cascade PID, decomposed so every contribution is visible.
/// </summary>
/// <remarks>
/// <para>
/// These are exactly the read-only figures the tuning workspace shows at 1 Hz -
/// <c>P</c>, <c>I</c>, <c>D</c>, <c>dSaída</c>, <c>Saída</c>, <c>DOT_pred</c>
/// (<c>docs/UI_DESIGN.md</c> section 5.2, "Termos ao vivo"). Making the terms
/// observable is the point: the operator can see the integral holding the actuator at
/// setpoint, rather than trusting that it does.
/// </para>
/// <para>
/// <see cref="Output"/> is the velocity-form running effort: <c>Saída[i-1] + dSaída[i]</c>,
/// clamped to the actuator window. It is <b>not</b> the sum of
/// <see cref="Proportional"/>, <see cref="Integral"/> and <see cref="Derivative"/> - those
/// are the positional-equivalent contributions, shown for insight, while the commanded
/// value is integrated and saturated separately.
/// </para>
/// </remarks>
/// <param name="Error">Setpoint minus the predicted measurement, in percent DO.</param>
/// <param name="Proportional">Proportional contribution, <c>Kp · error</c>.</param>
/// <param name="Integral">Integral contribution, bounded by the tuning's integral limits.</param>
/// <param name="Derivative">Derivative contribution, acting on the measurement rate.</param>
/// <param name="DeltaOutput">The increment actually applied this step, after saturation.</param>
/// <param name="Output">The commanded control effort in percent, after saturation.</param>
/// <param name="PredictedMeasurement">
/// Dead-time-compensated measurement <c>DOT_pred = DOT + rate · horizon</c>.
/// </param>
/// <param name="MeasurementRate">Estimated measurement rate, in percent DO per second.</param>
/// <param name="Saturated">True when the output hit an actuator window limit this step.</param>
public readonly record struct CascadeTerms(
    double Error,
    double Proportional,
    double Integral,
    double Derivative,
    double DeltaOutput,
    double Output,
    double PredictedMeasurement,
    double MeasurementRate,
    bool Saturated)
{
    /// <summary>The neutral reading before the loop has run, or after a reset.</summary>
    public static CascadeTerms Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, false);
}
