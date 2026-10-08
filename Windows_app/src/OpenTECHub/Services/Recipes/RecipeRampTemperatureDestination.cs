using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampTemperatureConfirmationPolicy : RecipeRampMeasuredConfirmationPolicy
{
    public double ToleranceC { get => Tolerance; init => Tolerance = value; }
    public RecipeRampTemperatureConfirmationPolicy(double ToleranceC, TimeSpan Stability, TimeSpan MaximumSampleGap, TimeSpan Timeout)
        : base(ToleranceC, Stability, MaximumSampleGap, Timeout) { }
}

/// <summary>Reactor-temperature confirmation through the selected native or external-bath route.</summary>
public sealed class RecipeRampTemperatureDestination(RecipeEngine engine, RecipeRampTemperatureConfirmationPolicy policy,
    RampTemperatureRoute? temperatureRoute = null)
    : RecipeRampMeasuredDestination(engine, policy)
{
    protected override SetpointVariable Variable => SetpointVariable.Temperature;
    private readonly RampTemperatureRoute _temperatureRoute = temperatureRoute ?? engine.RampTemperatureRoute;
    protected override RampTemperatureRoute? ExpectedTemperatureRoute => _temperatureRoute;

    protected override void ValidateReference(LinearRampSample target)
    {
        // Preserve the parser's existing observable envelope; zero means OFF, not a measured zero °C.
        if (target.Reference != 0 && target.Reference is not (> 10 and < 100))
            throw new ArgumentException("Alvo fora da faixa de temperatura observável pelo aplicativo.", nameof(target));
        if (RecipeRampReferenceQuantization.Quantize(target.Variable, target.Reference, _temperatureRoute) != target.Reference)
            throw new ArgumentException("Temperatura exige alvo quantizado para a rota capturada.");
    }

    protected override RecipeRampMeasuredProof? Evaluate(SensorSnapshot snapshot, LinearRampSample target, RecipeRampMeasuredRoute route)
    {
        if (snapshot.TempControlViaBath != route.TemperatureViaBath || snapshot.TempSetpoint is not { } echoed ||
            !double.IsFinite(echoed) || Math.Abs(echoed - target.Reference) > .005) return null;
        if (route.TemperatureViaBath && (!snapshot.HasBathTelemetry || !snapshot.BathOnline || snapshot.BathCommEnabled != true ||
            snapshot.BathCommandPending != false || snapshot.BathCommandCompletionPending || snapshot.BathStopPending ||
            snapshot.BathCascadeState.Equals("fault", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(snapshot.BathCascadeFaultReason) || !string.IsNullOrWhiteSpace(snapshot.BathOperationError))) return null;
        if (target.Reference == 0)
        {
            if (echoed != 0) return null;
            var stopped = route.TemperatureViaBath
                ? snapshot.BathOwned == false && snapshot.BathCascadeActive == false && snapshot.BathCommandLastSentId > 0 &&
                  snapshot.BathCommandLastDoneId >= snapshot.BathCommandLastSentId
                : snapshot.TempModuleActuatorOn == false;
            return stopped ? new(0, 0, RecipeRampConfirmationEvidence.DeviceReferenceReadback) : null;
        }
        if (snapshot.TempSetpointCommanded != true || !snapshot.TemperatureUpdated || !snapshot.SensorCommOk ||
            snapshot.TemperatureValid != true || snapshot.TemperatureAgeMs is not { } age || age < 0 || age > Policy.MaximumSampleGap.TotalMilliseconds ||
            route.TemperatureViaBath && (snapshot.BathOwned != true || snapshot.BathCascadeActive != true)) return null;
        return new(snapshot.Temperature, Policy.Tolerance, RecipeRampConfirmationEvidence.ProcessFeedback);
    }
}
