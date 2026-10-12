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
            // The tab order on screen, not in CalibrationView.xaml: the view's constructor moves
            // the pump tab next to temperature (index 3). Indexed by the XAML order, "pump"
            // opened Biomassa, "flow" opened the pump and "biomass" opened Vazão de ar.
            "ph" => 0,
            "oxygen" => 1,
            "temperature" => 2,
            "pump" or "pump-curve" or "pump-dose" => 3,
            "flow" => 4,
            "biomass" => 5,
            _ => SelectedTabIndex,
        };

        // The pump card links straight to the calibration its mode needs.
        if (target == "pump-curve")
        {
            Pump.IsCurveSection = true;
        }
        else if (target == "pump-dose")
        {
            Pump.IsDoseSection = true;
        }
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
