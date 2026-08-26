using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>Calibration procedures exposed as one navigation destination.</summary>
public sealed class CalibrationViewModel : IDisposable
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
    }

    public PHCalibrationViewModel PH { get; }

    public OxygenCalibrationViewModel Oxygen { get; }

    public FlowCalibrationViewModel Flow { get; }

    /// <summary>Guided biomass blank/threshold procedure (Phase 3 WP1).</summary>
    public BiomassCalibrationViewModel Biomass { get; }

    public void Dispose()
    {
        PH.Dispose();
        Oxygen.Dispose();
        Flow.Dispose();
        Biomass.Dispose();
    }
}
