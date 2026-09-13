using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenTECHub.ViewModels;

/// <summary>Calibration procedures exposed as one navigation destination.</summary>
public sealed partial class CalibrationViewModel : ObservableObject, IDisposable
{
    public CalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        PHControlViewModel phControl)
    {
        PH = new PHCalibrationViewModel(device, settings, phControl);
        Oxygen = new OxygenCalibrationViewModel(device, settings);
        Flow = new FlowCalibrationViewModel(device, settings);
        Biomass = new BiomassCalibrationViewModel(device, settings);
        Pump = new PumpCalibrationViewModel(device, settings);
    }

    public PHCalibrationViewModel PH { get; }

    public OxygenCalibrationViewModel Oxygen { get; }

    public FlowCalibrationViewModel Flow { get; }

    /// <summary>Guided biomass blank/threshold procedure (Phase 3 WP1).</summary>
    public BiomassCalibrationViewModel Biomass { get; }

    /// <summary>Linear calibration for the external peristaltic pump node.</summary>
    public PumpCalibrationViewModel Pump { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public void Select(string target)
    {
        SelectedTabIndex = target switch
        {
            "ph" => 0,
            "oxygen" => 1,
            "flow" => 2,
            "pump" => 3,
            "biomass" => 4,
            _ => SelectedTabIndex,
        };
    }

    public void Dispose()
    {
        PH.Dispose();
        Oxygen.Dispose();
        Flow.Dispose();
        Biomass.Dispose();
        Pump.Dispose();
    }
}
