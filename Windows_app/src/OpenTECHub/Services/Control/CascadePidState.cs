namespace OpenTECHub.Services.Control;

public sealed record CascadePidState(CascadeTuning Tuning, double Setpoint, double Output, double Integral,
    double PreviousError, double PreviousDerivative, bool HasPrevious, double[] MeasurementHistory,
    double[] ErrorWindow, CascadeTerms LastTerms);

public sealed partial class CascadeTwoLoopPidController
{
    /// <summary>Validate and freeze the entire state before changing any controller field.
    /// The caller holds the same computation gate used for capture and update.</summary>
    public void RestoreState(CascadePidState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Tuning is null || state.MeasurementHistory is null || state.ErrorWindow is null)
            throw new ArgumentException("Estado do PID incompleto.");
        state = state with { MeasurementHistory = state.MeasurementHistory.ToArray(), ErrorWindow = state.ErrorWindow.ToArray() };
        var terms = state.LastTerms;
        var numbers = new[] { state.Setpoint, state.Output, state.Integral, state.PreviousError, state.PreviousDerivative,
            terms.Error, terms.Proportional, terms.Integral, terms.Derivative, terms.DeltaOutput, terms.Output,
            terms.PredictedMeasurement, terms.MeasurementRate, terms.RateSetpoint, terms.GainFactor };
        if (!state.Tuning.IsValid || !Enum.IsDefined(state.Tuning.SlopeMethod) || numbers.Any(value => !double.IsFinite(value)) ||
            state.Output < state.Tuning.OutputMin || state.Output > state.Tuning.OutputMax ||
            state.Integral < state.Tuning.IntegralMin || state.Integral > state.Tuning.IntegralMax ||
            state.MeasurementHistory.Any(value => !double.IsFinite(value)) || state.ErrorWindow.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Estado do PID inválido.");
        Tuning = state.Tuning; Setpoint = state.Setpoint; _output = state.Output; _integral = state.Integral;
        _ePrev = state.PreviousError; _dfPrev = state.PreviousDerivative; _hasPrevious = state.HasPrevious;
        _dotHistory.Clear(); foreach (var value in state.MeasurementHistory) _dotHistory.Enqueue(value);
        _errorWindow.Clear(); foreach (var value in state.ErrorWindow) _errorWindow.Enqueue(value);
        LastTerms = state.LastTerms;
    }
}
