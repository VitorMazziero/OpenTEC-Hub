using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>External biomass sensor: communication, thresholds and live diagnostic readings.</summary>
public sealed partial class BiomassControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;

    public BiomassControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        var saved = settings.Current.BiomassControl;
        LowText = saved.LowThreshold.ToString();
        HighText = saved.HighThreshold.ToString();
        OptimalText = saved.OptimalThreshold.ToString();
        _device.TelemetryReceived += OnTelemetry;
    }

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool AppliedIsEnabled { get; set; }
    [ObservableProperty] public partial string LowText { get; set; } = "10000";
    [ObservableProperty] public partial string HighText { get; set; } = "40000";
    [ObservableProperty] public partial string OptimalText { get; set; } = "25000";
    [ObservableProperty] public partial int? AppliedOptimalThreshold { get; set; }
    [ObservableProperty] public partial string AbsorbanceText { get; set; } = "—";
    [ObservableProperty] public partial string RawText { get; set; } = "—";
    [ObservableProperty] public partial string IntegrationTimeText { get; set; } = "—";
    [ObservableProperty] public partial string PwmText { get; set; } = "—";

    [RelayCommand]
    private void ApplyCommunication()
    {
        _device.Send(CommandBuilders.BiomassCommunication(IsEnabled));
        AppliedIsEnabled = IsEnabled;
    }

    [RelayCommand]
    private void ApplyThresholds()
    {
        if (!int.TryParse(LowText, out var low) || !int.TryParse(HighText, out var high) ||
            !int.TryParse(OptimalText, out var optimal) || low < 0 || high < low || optimal < low || optimal > high)
        {
            return;
        }
        _device.Send(CommandBuilders.BiomassThresholds(low, high, optimal));
        _settings.Update(s => s with { BiomassControl = new BiomassControlSettings { LowThreshold = low, HighThreshold = high, OptimalThreshold = optimal } });
        AppliedOptimalThreshold = optimal;
    }

    [RelayCommand] private void Blank() => _device.Send(CommandBuilders.BiomassBlank());
    [RelayCommand] private void Start() => _device.Send(TecnalCommand.Create().Set(CommandKeys.Start, 1));
    [RelayCommand] private void Stop() => _device.Send(TecnalCommand.Create().Set(CommandKeys.Stop, 1));

    private void OnTelemetry(SensorSnapshot s)
    {
        AbsorbanceText = s.BiomassAbsorbance > SensorReadings.NotReceived ? s.BiomassAbsorbance.ToString("F3") + " AU" : "—";
        RawText = s.BiomassRaw.ToString();
        IntegrationTimeText = s.BiomassIntegrationTimeMs + " ms";
        PwmText = s.BiomassPwmPercent.ToString("F1") + " %";
    }

    public void Dispose() => _device.TelemetryReceived -= OnTelemetry;
}
