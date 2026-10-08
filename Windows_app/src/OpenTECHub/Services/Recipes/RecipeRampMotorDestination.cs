using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampMotorConfirmationPolicy : RecipeRampMeasuredConfirmationPolicy
{
    public double ToleranceRpm { get => Tolerance; init => Tolerance = value; }
    public RecipeRampMotorConfirmationPolicy(double ToleranceRpm, TimeSpan Stability, TimeSpan MaximumSampleGap, TimeSpan Timeout)
        : base(ToleranceRpm, Stability, MaximumSampleGap, Timeout) { }
}

/// <summary>Measured motor-speed component. Wrap in the ramp producer's guarded destination.</summary>
public sealed class RecipeRampMotorDestination(RecipeEngine engine, RecipeRampMotorConfirmationPolicy policy)
    : RecipeRampMeasuredDestination(engine, policy)
{
    protected override SetpointVariable Variable => SetpointVariable.Agitation;

    protected override void ValidateReference(LinearRampSample target)
    {
        if (target.Reference != Math.Truncate(target.Reference))
            throw new ArgumentException("Destino do motor exige uma referência de agitação quantizada.", nameof(target));
    }

    protected override RecipeRampMeasuredProof? Evaluate(SensorSnapshot snapshot, LinearRampSample target, RecipeRampMeasuredRoute route)
        => snapshot.HasServoTelemetry && snapshot.HasServoSample && snapshot.ServoOnline &&
            snapshot.ServoCommEnabled == true && snapshot.ServoCommandPending == false && snapshot.ServoAlarm == 0 &&
            snapshot.MotorControlViaModbus == !route.MotorViaUart && snapshot.ServoMotorRouteAck == (route.MotorViaUart ? 0 : 1)
            ? new(snapshot.ServoRpm, Policy.Tolerance, RecipeRampConfirmationEvidence.ProcessFeedback) : null;
}
