using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenTECHub.ViewModels;

/// <summary>Calibration procedures exposed as one navigation destination.</summary>
public sealed partial class CalibrationViewModel : ObservableObject, IDisposable
{
    public CalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        PHControlViewModel phControl,
        IPumpCalibrationProfileStore? pumpProfileStore = null,
        IDialogService? dialogs = null)
    {
        PH = new PHCalibrationViewModel(device, settings, phControl);
        Oxygen = new OxygenCalibrationViewModel(device, settings);
        Temperature = new TemperatureCalibrationViewModel(device, settings);
        Flow = new FlowCalibrationViewModel(device, settings);
        Biomass = new BiomassCalibrationViewModel(device, settings);
        Pump = new PumpCalibrationViewModel(device, settings, profileStore: pumpProfileStore, dialogs: dialogs);
    }

    public PHCalibrationViewModel PH { get; }

    public OxygenCalibrationViewModel Oxygen { get; }

    /// <summary>Reactor probe offset correction (D-073).</summary>
    public TemperatureCalibrationViewModel Temperature { get; }

    public FlowCalibrationViewModel Flow { get; }

    /// <summary>Guided biomass blank/threshold procedure (Phase 3 WP1).</summary>
    public BiomassCalibrationViewModel Biomass { get; }

    /// <summary>Continuous dual-range calibration and hose profiles for the external pump node.</summary>
    public PumpCalibrationViewModel Pump { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public void Select(string target)
    {
        SelectedTabIndex = target switch
        {
            // Tab order in CalibrationView.xaml. Biomass and pump used to be swapped here.
            "ph" => 0,
            "oxygen" => 1,
            "temperature" => 2,
            "flow" => 3,
            "biomass" => 4,
            "pump" => 5,
            _ => SelectedTabIndex,
        };
    }

    public void Dispose()
    {
        PH.Dispose();
        Oxygen.Dispose();
        Temperature.Dispose();
        Flow.Dispose();
        Biomass.Dispose();
        Pump.Dispose();
    }
}
