using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Guided biomass blank/threshold procedure (Phase 3 WP1) for the Calibrações page.
/// </summary>
/// <remarks>
/// <para>
/// Biomass has no PC-side coefficient like pH or oxygen: "calibration" is capturing the blank
/// (zero-absorbance) reference against a clear medium and setting the integration thresholds. The
/// procedure enables the sensor, walks the operator through the blank capture with a live
/// absorbance readout to confirm it settled near zero, and applies the thresholds — the same
/// commands the Controle card sends, wrapped in an explicit, ownership-aware sequence.
/// </para>
/// <para>
/// The firmware exposes <b>no HD-mode state</b> (confirmed in <c>OpenTEC_ESP32_v7.ino</c>), so this
/// procedure never shows one. Losing the link refuses the run without changing anything.
/// </para>
/// </remarks>
public sealed partial class BiomassCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;

    public BiomassCalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null)
    {
        _device = device;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _settings = settings;

        var stored = settings.Current.BiomassControl;
        LowThresholdText = stored.LowThreshold.ToString(CultureInfo.CurrentCulture);
        HighThresholdText = stored.HighThreshold.ToString(CultureInfo.CurrentCulture);
        OptimalThresholdText = stored.OptimalThreshold.ToString(CultureInfo.CurrentCulture);

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
    }

    [ObservableProperty]
    public partial bool SensorEnabled { get; set; }

    [ObservableProperty]
    public partial string CurrentAbsorbanceText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentRawText { get; set; } = "—";

    [ObservableProperty]
    public partial string LowThresholdText { get; set; } = "10000";

    [ObservableProperty]
    public partial string HighThresholdText { get; set; } = "40000";

    [ObservableProperty]
    public partial string OptimalThresholdText { get; set; } = "25000";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "1) Coloque o meio de branco no caminho óptico. 2) Ative o sensor. 3) Capture o branco e confirme Abs ≈ 0.";

    public bool IsConnected => _device.State == ConnectionState.Connected;

    /// <summary>The blank/start/stop actions need the link up and the sensor enabled here.</summary>
    public bool CanActOnSensor => IsConnected && SensorEnabled;

    public bool CanApplyThresholds => IsConnected && SensorEnabled && Validate() is null;

    /// <summary>
    /// Why the thresholds cannot be applied, or null when they are well formed.
    /// </summary>
    /// <remarks>
    /// Surfaced so a rejected value explains itself: the entry fields stay editable while
    /// invalid, which is the only way the operator can correct them.
    /// </remarks>
    public string? ThresholdError => Validate();

    partial void OnSensorEnabledChanged(bool value)
    {
        var result = _dispatcher.Dispatch(CommandBuilders.BiomassComm(value));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        StatusText = value
            ? "Sensor ativado. Com o meio de branco no lugar, capture o branco."
            : "Sensor desativado.";
        NotifyActionAvailability();
    }

    partial void OnLowThresholdTextChanged(string value) => NotifyThresholdAvailability();

    partial void OnHighThresholdTextChanged(string value) => NotifyThresholdAvailability();

    partial void OnOptimalThresholdTextChanged(string value) => NotifyThresholdAvailability();

    [RelayCommand(CanExecute = nameof(CanActOnSensor))]
    private void CaptureBlank()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.BiomassBlank());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        StatusText = "Comando de branco enviado. Aguarde e confirme que a absorbância se aproxima de zero.";
    }

    [RelayCommand(CanExecute = nameof(CanActOnSensor))]
    private void Start()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.BiomassStart());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        StatusText = "Aquisição de biomassa iniciada.";
    }

    [RelayCommand(CanExecute = nameof(CanActOnSensor))]
    private void Stop()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.BiomassStop());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        StatusText = "Aquisição de biomassa parada.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyThresholds))]
    private void ApplyThresholds()
    {
        if (Validate() is { } error)
        {
            StatusText = error;
            return;
        }

        var low = int.Parse(LowThresholdText, CultureInfo.CurrentCulture);
        var high = int.Parse(HighThresholdText, CultureInfo.CurrentCulture);
        var optimal = int.Parse(OptimalThresholdText, CultureInfo.CurrentCulture);

        var result = _dispatcher.Dispatch(CommandBuilders.BiomassThresholds(low, high, optimal));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        _settings.Update(settings => settings with
        {
            BiomassControl = settings.BiomassControl with
            {
                LowThreshold = low,
                HighThreshold = high,
                OptimalThreshold = optimal,
            },
        });
        StatusText = "Limiares de integração enviados e persistidos.";
    }

    private string? Validate()
    {
        if (!int.TryParse(LowThresholdText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var low) ||
            !int.TryParse(HighThresholdText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var high) ||
            !int.TryParse(OptimalThresholdText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var optimal))
        {
            return "Limiares devem ser inteiros.";
        }

        if (low is < 0 or > 200_000 || high is < 0 or > 200_000 || optimal is < 0 or > 200_000)
        {
            return "Limiares fora da faixa (0 a 200000 contagens).";
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

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        CurrentAbsorbanceText = snapshot.BiomassAbsorbance > SensorReadings.NotReceived
            ? snapshot.BiomassAbsorbance.ToString("F3", CultureInfo.CurrentCulture)
            : "—";
        CurrentRawText = snapshot.BiomassAbsorbance > SensorReadings.NotReceived
            ? snapshot.BiomassRaw.ToString(CultureInfo.CurrentCulture)
            : "—";
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        OnPropertyChanged(nameof(IsConnected));
        NotifyActionAvailability();
    }

    private void NotifyActionAvailability()
    {
        OnPropertyChanged(nameof(CanActOnSensor));
        CaptureBlankCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NotifyThresholdAvailability();
    }

    private void NotifyThresholdAvailability()
    {
        OnPropertyChanged(nameof(CanApplyThresholds));
        OnPropertyChanged(nameof(ThresholdError));
        ApplyThresholdsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
    }
}
