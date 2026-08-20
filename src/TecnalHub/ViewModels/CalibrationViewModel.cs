using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>Three calibration procedures exposed as one navigation destination.</summary>
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
    }

    public PHCalibrationViewModel PH { get; }

    public OxygenCalibrationViewModel Oxygen { get; }

    public FlowCalibrationViewModel Flow { get; }

    public void Dispose()
    {
        PH.Dispose();
        Oxygen.Dispose();
        Flow.Dispose();
    }
}
