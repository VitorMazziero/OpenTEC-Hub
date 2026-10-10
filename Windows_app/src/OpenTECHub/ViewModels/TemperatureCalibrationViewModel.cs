using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Calibrações › Temperatura: a constant correction of the reactor probe (D-073).
/// </summary>
/// <remarks>
/// <c>real = read + correction</c>. Once applied, the whole app shows, logs and controls the real
/// temperature, and the reactor setpoint reaches the module as <c>real − correction</c>. The
/// correction can be typed, or computed from a reference thermometer read at the same moment.
/// Applying only saves the setting; nothing is sent to the equipment.
/// </remarks>
public sealed partial class TemperatureCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private double? _lastRaw;

    public TemperatureCalibrationViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        OffsetText = FormatOffset(settings.Current.Calibration.TemperatureOffsetC);
        _device.TelemetryReceived += OnTelemetryReceived;
        RefreshCurrent();
    }

    /// <summary>The correction to apply, °C (positive when the probe reads below the real value).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CorrectedPreviewText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string OffsetText { get; set; } = "0.0";

    /// <summary>A reference thermometer reading taken now, °C.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeFromReferenceCommand))]
    public partial string ReferenceText { get; set; } = "";

    [ObservableProperty] public partial string RawText { get; set; } = "—";
    [ObservableProperty] public partial string CorrectedText { get; set; } = "—";
    [ObservableProperty] public partial string CurrentOffsetText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>What the live reading becomes with the correction being edited.</summary>
    public string CorrectedPreviewText =>
        _lastRaw is { } raw && TryParse(OffsetText, out var offset) && TemperatureCorrection.IsValidOffset(offset)
            ? $"Com esta correção: {Format(raw + offset)} °C"
            : "";

    public bool CanApply => TryParse(OffsetText, out var offset) && TemperatureCorrection.IsValidOffset(offset) &&
                            Math.Abs(offset - _settings.Current.Calibration.TemperatureOffsetC) > 1e-9;

    public bool CanComputeFromReference => _lastRaw is not null && TryParse(ReferenceText, out var reference) &&
                                           reference is > 0 and < 100;

    [RelayCommand(CanExecute = nameof(CanComputeFromReference))]
    private void ComputeFromReference()
    {
        if (_lastRaw is not { } raw || !TryParse(ReferenceText, out var reference)) return;
        var offset = Math.Round(reference - raw, 2);
        if (!TemperatureCorrection.IsValidOffset(offset))
        {
            StatusText = $"A diferença ({Format(offset)} °C) passa de ±{Format(TemperatureCorrection.MaximumOffsetC)} °C: confira a sonda e a referência.";
            return;
        }
        OffsetText = FormatOffset(offset);
        StatusText = $"Correção calculada: referência {Format(reference)} °C − lida {Format(raw)} °C. Confira e aplique.";
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryParse(OffsetText, out var offset) || !TemperatureCorrection.IsValidOffset(offset))
        {
            StatusText = $"A correção deve estar entre −{Format(TemperatureCorrection.MaximumOffsetC)} e +{Format(TemperatureCorrection.MaximumOffsetC)} °C.";
            return;
        }
        _settings.Update(s => s with { Calibration = s.Calibration with { TemperatureOffsetC = offset } });
        StatusText = offset == 0
            ? "Correção removida: a temperatura do reator volta a ser a leitura do módulo."
            : $"Correção de {FormatOffset(offset)} °C aplicada. Leituras, registros e setpoints passam a usar a temperatura real.";
        RefreshCurrent();
    }

    [RelayCommand]
    private void Clear()
    {
        OffsetText = FormatOffset(0);
        if (CanApply) Apply();
    }

    partial void OnOffsetTextChanged(string value) => ApplyCommand.NotifyCanExecuteChanged();

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        if (snapshot.TemperatureRaw > 10 && snapshot.TemperatureRaw < 100)
        {
            _lastRaw = snapshot.TemperatureRaw;
            RawText = Format(snapshot.TemperatureRaw);
            CorrectedText = Format(snapshot.Temperature);
        }
        OnPropertyChanged(nameof(CorrectedPreviewText));
        ComputeFromReferenceCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCurrent()
    {
        var offset = _settings.Current.Calibration.TemperatureOffsetC;
        CurrentOffsetText = offset == 0
            ? "Sem correção: a temperatura do reator é a leitura do módulo."
            : $"Correção em uso: {FormatOffset(offset)} °C (real = lida {FormatOffset(offset)}).";
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CorrectedPreviewText));
    }

    private static string FormatOffset(double value)
        => value.ToString("+0.0#;−0.0#;0.0", CultureInfo.CurrentCulture);

    private static string Format(double value) => value.ToString("0.0#", CultureInfo.CurrentCulture);

    private static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().Replace('−', '-').Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
