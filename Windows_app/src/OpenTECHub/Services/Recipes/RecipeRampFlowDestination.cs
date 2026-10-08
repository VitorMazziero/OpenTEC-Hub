using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampFlowConfirmationPolicy : RecipeRampMeasuredConfirmationPolicy
{
    public double ToleranceLpm { get => Tolerance; init => Tolerance = value; }
    public RecipeRampFlowConfirmationPolicy(double ToleranceLpm, TimeSpan Stability, TimeSpan MaximumSampleGap, TimeSpan Timeout)
        : base(ToleranceLpm, Stability, MaximumSampleGap, Timeout) { }
}

/// <summary>Confirms fresh measured flow and acknowledged routing through the frozen gas rig.</summary>
public sealed class RecipeRampFlowDestination(RecipeEngine engine, RecipeRampFlowConfirmationPolicy policy)
    : RecipeRampMeasuredDestination(engine, policy)
{
    protected override SetpointVariable Variable => SetpointVariable.Flow;

    protected override RecipeRampMeasuredProof? Evaluate(SensorSnapshot snapshot, LinearRampSample target, RecipeRampMeasuredRoute route)
    {
        if (!snapshot.FlowRateUpdated || !snapshot.FlowFeedbackUpdated || !snapshot.FlowmeterOnline || snapshot.FlowCommandPending ||
            !double.IsFinite(snapshot.FlowRate) || snapshot.FlowRate < 0 ||
            snapshot.FlowCommandId <= 0 || snapshot.FlowCommandAck != snapshot.FlowCommandId || route.GasRig is null ||
            !double.IsFinite(snapshot.FlowSetpoint) || Math.Abs(snapshot.FlowSetpoint - target.Reference) > 0.1 ||
            snapshot.FlowValve1 is not (0 or 1) || snapshot.FlowValve2 is not (0 or 1)) return null;
        var observedRoute = GasRouting.Interpret(snapshot.FlowValve1 == 1, snapshot.FlowValve2 == 1, snapshot.FlowSetpoint, route.GasRig);
        if (target.Reference == 0 ? snapshot.FlowSetpoint != 0 || observedRoute != ObservedGasRoute.Closed
            : observedRoute != ObservedGasRoute.Reactor) return null;
        return new(snapshot.FlowRate, Policy.Tolerance, RecipeRampConfirmationEvidence.ProcessFeedback);
    }
}
