using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// The biomass optical sensor (Phase 3 WP1): enable, the momentary blank/start/stop actions,
/// the atomic low/high/optimal integration thresholds and the live Abs/Raw/IT/PWM readouts.
/// </summary>
/// <remarks>
/// <para>
/// The enable is an immediate toggle exactly as v.6's checkbox is — it flips a mode on the hub,
/// and the firmware drops blank/start/stop/threshold sub-commands while the sensor is disabled,
/// so the momentary actions are only enabled once the sensor is on. The thresholds are staged and
/// applied together (raw ADC counts, ints).
/// </para>
/// <para>
/// Biomass is a <b>measurement</b>: it is deliberately excluded from the operator safe-stop, so a
/// stop never blinds the reading — the same treatment the level/foam sensor gets. It is still an
/// owned arbiter actuator, so a future recipe cannot fight the operator over blank/thresholds.
/// </para>
/// </remarks>
public sealed partial class BiomassControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private BiomassControlSettings _committed;

    public BiomassControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.BiomassControl;

        Load(_committed);
        _device.TelemetryReceived += OnTelemetryReceived;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    /// <summary>Sensor enabled — <c>biomassComm</c>. Sent immediately on change, like v.6.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string LowThresholdText { get; set; } = "10000";

    [ObservableProperty]
    public partial string HighThresholdText { get; set; } = "40000";

    [ObservableProperty]
    public partial string OptimalThresholdText { get; set; } = "25000";

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string AbsorbanceText { get; set; } = "—";

    [ObservableProperty]
    public partial string RawText { get; set; } = "—";

    [ObservableProperty]
    public partial string IntegrationTimeText { get; set; } = "—";

    [ObservableProperty]
    public partial string PwmText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsValid;

    public string StateText => IsEnabled ? "Ativo" : "Desligado";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        BlankCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        if (!_initialised)
        {
            return;
        }

        // The enable is an immediate mode switch on the hub, not a staged field.
        _device.Send(CommandBuilders.BiomassComm(value));
        StatusText = value
            ? "Sensor de biomassa ativado."
            : "Sensor de biomassa desativado.";
    }

    partial void OnLowThresholdTextChanged(string value) => ValidateAndRefresh();

    partial void OnHighThresholdTextChanged(string value) => ValidateAndRefresh();

    partial void OnOptimalThresholdTextChanged(string value) => ValidateAndRefresh();

    /// <summary>Captures the blank (zero-absorbance) reference. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void Blank()
    {
        _device.Send(CommandBuilders.BiomassBlank());
        StatusText = "Comando de branco enviado ao sensor de biomassa.";
    }

    /// <summary>Starts the acquisition loop. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void Start()
    {
        _device.Send(CommandBuilders.BiomassStart());
        StatusText = "Comando de início enviado ao sensor de biomassa.";
    }

    /// <summary>Stops the acquisition loop. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void Stop()
    {
        _device.Send(CommandBuilders.BiomassStop());
        StatusText = "Comando de parada enviado ao sensor de biomassa.";
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyThresholds()
    {
        if (!TryGetStagedSettings(out var staged))
        {
            StatusText = ValidationError ?? "Revise os limiares de biomassa.";
            return;
        }

        _device.Send(CommandBuilders.BiomassThresholds(
            staged.LowThreshold, staged.HighThreshold, staged.OptimalThreshold));

        _committed = staged;
        _settings.Update(settings => settings with { BiomassControl = staged });
        HasPendingChange = false;
        StatusText = "Limiares de biomassa (baixo/alto/ótimo) enviados.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        HasPendingChange = false;
        StatusText = "Alterações não enviadas dos limiares de biomassa foram revertidas.";
    }

    public bool TryGetStagedSettings(out BiomassControlSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseInteger(LowThresholdText, out var low) || low is < 0 or > 200_000 ||
            !DosingInput.TryParseInteger(HighThresholdText, out var high) || high is < 0 or > 200_000 ||
            !DosingInput.TryParseInteger(OptimalThresholdText, out var optimal) || optimal is < 0 or > 200_000 ||
            low >= high || optimal < low || optimal > high)
        {
            return false;
        }

        settings = new BiomassControlSettings
        {
            LowThreshold = low,
            HighThreshold = high,
            OptimalThreshold = optimal,
        };
        return true;
    }

    private void Load(BiomassControlSettings s)
    {
        LowThresholdText = DosingInput.FormatInt(s.LowThreshold);
        HighThresholdText = DosingInput.FormatInt(s.HighThreshold);
        OptimalThresholdText = DosingInput.FormatInt(s.OptimalThreshold);
    }

    private void ValidateAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        ValidationError = Validate();
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyThresholdsCommand.NotifyCanExecuteChanged();
        RefreshPendingState();
    }

    private string? Validate()
    {
        if (!DosingInput.TryParseInteger(LowThresholdText, out var low) || low is < 0 or > 200_000)
        {
            return "Limiar baixo: inteiro de 0 a 200000 (contagens).";
        }

        if (!DosingInput.TryParseInteger(HighThresholdText, out var high) || high is < 0 or > 200_000)
        {
            return "Limiar alto: inteiro de 0 a 200000 (contagens).";
        }

        if (!DosingInput.TryParseInteger(OptimalThresholdText, out var optimal) || optimal is < 0 or > 200_000)
        {
            return "Limiar ótimo: inteiro de 0 a 200000 (contagens).";
        }

        if (low >= high)
        {
            return "O limiar baixo deve ser menor que o alto.";
        }

        if (optimal < low || optimal > high)
        {
            return "O limiar ótimo deve ficar entre o baixo e o alto.";
        }

        return null;
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        HasPendingChange = !TryGetStagedSettings(out var staged) || staged != _committed;
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        // The four biomass channels arrive together; Abs carries the not-received sentinel,
        // so gate the whole block on it rather than showing 0 counts before any frame.
        if (snapshot.BiomassAbsorbance <= SensorReadings.NotReceived)
        {
            AbsorbanceText = RawText = IntegrationTimeText = PwmText = "—";
            return;
        }

        AbsorbanceText = snapshot.BiomassAbsorbance.ToString("F3", CultureInfo.CurrentCulture);
        RawText = snapshot.BiomassRaw.ToString(CultureInfo.CurrentCulture);
        IntegrationTimeText = snapshot.BiomassIntegrationTimeMs.ToString(CultureInfo.CurrentCulture);
        PwmText = snapshot.BiomassPwmPercent.ToString("F1", CultureInfo.CurrentCulture);
    }

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
