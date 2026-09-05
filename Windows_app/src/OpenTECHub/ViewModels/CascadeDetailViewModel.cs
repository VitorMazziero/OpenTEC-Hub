using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.Control;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The read-only cascade view behind the oxygen detail pane's <c>Cascata</c>/<c>PID</c>/
/// <c>Saída</c> tabs.
/// </summary>
/// <remarks>
/// It formats <see cref="ICascadeService"/> state for a glance on the synoptic; the controls
/// that change it live on <c>Controle → Controle de oxigênio</c>, so this
/// pane never actuates. Only oxygen has these tabs, because it is the only device with an
/// app-side controller whose terms and output the application can observe.
/// </remarks>
public sealed partial class CascadeDetailViewModel : ObservableObject, IDisposable
{
    private readonly ICascadeService _cascade;

    public CascadeDetailViewModel(ICascadeService cascade)
    {
        _cascade = cascade;
        _cascade.Updated += OnUpdated;
    }

    /// <summary>True once the loop is computing, so the tabs show numbers rather than a hint.</summary>
    public bool IsRunning => _cascade.IsArmed;

    public bool IsEngaged => _cascade.IsEngaged;

    /// <summary>The one-line state shown at the top of every cascade tab.</summary>
    public string StateText => !_cascade.IsArmed
        ? "Controle de O₂ inativo."
        : _cascade.IsEngaged
            ? "Automático ativo · enviando comandos."
            : "Consultiva · calcula, mas não envia.";

    public string ModeText => _cascade.Mode switch
    {
        CascadeMode.AgitationOnly => "Somente agitação",
        CascadeMode.AerationOnly => "Somente aeração",
        _ => "Trajetória kLa",
    };

    // ── Cascata / Saída (setpoint, allocation, kLa) ──────────────────────────

    public string SetpointText => Percent(_cascade.OxygenSetpoint);

    public string KlaDemandText => _cascade.ActiveKlaDemand is { } kla
        ? kla.ToString("F1", CultureInfo.CurrentCulture) + " /h"
        : "—";

    public string AgitationText => _cascade.LastActuation is { } a
        ? a.AgitationRpm.ToString(CultureInfo.CurrentCulture) + " rpm"
        : "—";

    public string AerationText => _cascade.LastActuation is { } a
        ? a.AerationLpm.ToString("F2", CultureInfo.CurrentCulture) + " L/min"
        : "—";

    // ── PID terms ────────────────────────────────────────────────────────────

    public string OutputText => IsRunning ? Percent(_cascade.Terms.Output) : "—";

    public string ErrorText => Signed(_cascade.Terms.Error, "%");

    public string PredictedText => IsRunning ? Percent(_cascade.Terms.PredictedMeasurement) : "—";

    public string RateText => IsRunning
        ? _cascade.Terms.MeasurementRate.ToString("F3", CultureInfo.CurrentCulture) + " %/s"
        : "—";

    public string ProportionalText => Fixed(_cascade.Terms.Proportional);

    public string IntegralText => Fixed(_cascade.Terms.Integral);

    public string DerivativeText => Fixed(_cascade.Terms.Derivative);

    public string DeltaOutputText => IsRunning
        ? _cascade.Terms.DeltaOutput.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture)
        : "—";

    public bool IsSaturated => IsRunning && _cascade.Terms.Saturated;

    private void OnUpdated()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsEngaged));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(SetpointText));
        OnPropertyChanged(nameof(KlaDemandText));
        OnPropertyChanged(nameof(AgitationText));
        OnPropertyChanged(nameof(AerationText));
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(PredictedText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(ProportionalText));
        OnPropertyChanged(nameof(IntegralText));
        OnPropertyChanged(nameof(DerivativeText));
        OnPropertyChanged(nameof(DeltaOutputText));
        OnPropertyChanged(nameof(IsSaturated));
    }

    private static string Percent(double value)
        => value.ToString("F1", CultureInfo.CurrentCulture) + " %";

    private string Fixed(double value)
        => IsRunning ? value.ToString("F2", CultureInfo.CurrentCulture) : "—";

    private string Signed(double value, string unit)
        => IsRunning ? value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " " + unit : "—";

    public void Dispose() => _cascade.Updated -= OnUpdated;
}
