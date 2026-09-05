using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.Control;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The read-only conditional-OUR readout on the cascade tuning workspace (WP8).
/// </summary>
/// <remarks>
/// It formats the <see cref="IOurSoftSensor"/> reading for a glance — the value while accepted,
/// the reason while refused, the kLa behind it and the accepted-interval total. It never
/// actuates; OUR is an <c>estimado</c> observation, not a setpoint. A refused frame shows an em
/// dash, never a zero.
/// </remarks>
public sealed partial class OurViewModel : ObservableObject, IDisposable
{
    private const string Unit = "mmol L⁻¹ h⁻¹";

    private readonly IOurSoftSensor _sensor;

    public OurViewModel(IOurSoftSensor sensor)
    {
        _sensor = sensor;
        _sensor.Updated += OnUpdated;
    }

    private OurSample Sample => _sensor.Latest;

    /// <summary>The conditional OUR while accepted; an em dash otherwise — never a zero.</summary>
    public string OurText => Sample.ConditionalOurMmolPerLPerHour is { } value
        ? value.ToString("F1", CultureInfo.CurrentCulture) + " " + Unit
        : "—";

    public bool IsAccepted => Sample.Accepted;

    /// <summary>The single accept/refuse reason, in pt-BR.</summary>
    public string StatusText => Sample.Status switch
    {
        OurStatus.Accepted => "Aceito · quase-estacionário na banda.",
        OurStatus.WarmingUp => "Aquecendo · sem histórico de taxa suficiente.",
        OurStatus.WaitingForSetpoint => "Aguardando o DOT atingir o setpoint.",
        OurStatus.NoKla => "Sem kLa · nenhum mapa publicado ativo, ou ponto fora do mapa.",
        OurStatus.OutOfBand => "Recusado · DOT fora da banda de estabilidade.",
        OurStatus.NotQuasiSteady => "Recusado · DOT variando rápido demais.",
        _ => "—",
    };

    /// <summary>Drives the status dot: green while accepted, amber while refused for a process reason.</summary>
    public VariableState State => Sample.Status switch
    {
        OurStatus.Accepted => VariableState.Ok,
        OurStatus.OutOfBand or OurStatus.NotQuasiSteady => VariableState.Warning,
        _ => VariableState.Idle,
    };

    public string KlaText => Sample.KlaPerHour is { } kla
        ? kla.ToString("F1", CultureInfo.CurrentCulture) + " /h"
        : "—";

    public string DotRateText =>
        Sample.DotRatePointsPerHour.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " pp/h";

    /// <summary>Accepted-interval integral ∫OUR dt — kept separate from any total consumption.</summary>
    public string CumulativeText => Sample.AcceptedDurationHours > 0
        ? Sample.CumulativeMmolPerL.ToString("F1", CultureInfo.CurrentCulture) + " mmol L⁻¹"
        : "—";

    public string MeanText => _sensor.Latest.AcceptedDurationHours > 0
        ? (Sample.CumulativeMmolPerL / Sample.AcceptedDurationHours).ToString("F1", CultureInfo.CurrentCulture) + " " + Unit
        : "—";

    public string AcceptedDurationText => Sample.AcceptedDurationHours > 0
        ? Sample.AcceptedDurationHours.ToString("F2", CultureInfo.CurrentCulture) + " h"
        : "—";

    public string ActiveMapText => _sensor.ActiveMap is { } map
        ? map.Payload.Name
        : "Nenhum mapa kLa ativo";

    [RelayCommand]
    private void ResetTotals() => _sensor.ResetTotals();

    private void OnUpdated()
    {
        OnPropertyChanged(nameof(OurText));
        OnPropertyChanged(nameof(IsAccepted));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(KlaText));
        OnPropertyChanged(nameof(DotRateText));
        OnPropertyChanged(nameof(CumulativeText));
        OnPropertyChanged(nameof(MeanText));
        OnPropertyChanged(nameof(AcceptedDurationText));
        OnPropertyChanged(nameof(ActiveMapText));
    }

    public void Dispose() => _sensor.Updated -= OnUpdated;
}
