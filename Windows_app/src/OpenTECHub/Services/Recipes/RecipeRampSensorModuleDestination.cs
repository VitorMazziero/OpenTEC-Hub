using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

/// <summary>pH/pressure process confirmation with current Hub reference and dispatch-state echoes.</summary>
public sealed class RecipeRampSensorModuleDestination : RecipeRampMeasuredDestination
{
    private readonly SetpointVariable _variable;
    private readonly double _phBand;
    protected override SetpointVariable Variable => _variable;
    protected override double PhInactiveBand => _phBand;

    public RecipeRampSensorModuleDestination(RecipeEngine engine, SetpointVariable variable,
        RecipeRampMeasuredConfirmationPolicy policy, double? phInactiveBand = null)
        : base(engine, CheckedPolicy(variable, policy, phInactiveBand))
    {
        _variable = variable;
        _phBand = phInactiveBand is { } band ? Math.Round(band, 2, MidpointRounding.AwayFromZero) : 0;
    }

    private static RecipeRampMeasuredConfirmationPolicy CheckedPolicy(SetpointVariable variable,
        RecipeRampMeasuredConfirmationPolicy policy, double? band)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (variable is not (SetpointVariable.Ph or SetpointVariable.Pressure) ||
            variable == SetpointVariable.Ph && (band is not { } value || !double.IsFinite(value) || value < 0))
            throw new ArgumentException("Destino requer pH/pressão e banda de pH preservada quando aplicável.");
        return policy;
    }

    protected override void ValidateReference(LinearRampSample target)
    {
        if (RecipeRampReferenceQuantization.Quantize(target.Variable, target.Reference) != target.Reference)
            throw new ArgumentException("Destino exige referência quantizada.");
    }

    protected override RecipeRampMeasuredProof? Evaluate(SensorSnapshot snapshot, LinearRampSample target, RecipeRampMeasuredRoute route)
    {
        var ph = _variable == SetpointVariable.Ph;
        var echoed = ph ? snapshot.PHSetpoint : snapshot.PressureReference;
        var active = ph ? snapshot.PHControlActive : snapshot.PressureControlActive;
        var pending = ph ? snapshot.PHCommandPending : snapshot.PressureCommandPending;
        if (!snapshot.SensorCommUpdated || !snapshot.SensorCommOk || pending != false || echoed is not { } reference || !double.IsFinite(reference) ||
            Math.Abs(reference - target.Reference) > (ph ? .005 : 0) ||
            ph && (snapshot.PHError is not { } error || !double.IsFinite(error) || Math.Abs(error - _phBand) > .005)) return null;
        if (target.Reference == 0)
            return reference == 0 && active == false ? new(0, 0, RecipeRampConfirmationEvidence.DeviceReferenceReadback) : null;
        var measured = ph ? snapshot.PHCalibrated : snapshot.Pressure;
        if (active != true || !(ph ? snapshot.PHUpdated : snapshot.PressureUpdated) ||
            !double.IsFinite(measured) || measured < 0 || ph && measured > 14) return null;
        return new(measured, Policy.Tolerance, RecipeRampConfirmationEvidence.ProcessFeedback);
    }
}
