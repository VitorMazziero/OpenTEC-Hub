using System.Windows.Controls;

namespace OpenTECHub.Controls;

/// <summary>
/// The external pump's drawer on Controle: status strip, drive-mode selector, profile
/// parameters beside the stacked flow/volume charts, proportional gas and advanced settings.
/// </summary>
/// <remarks>DataContext is the <see cref="ViewModels.PumpControlViewModel"/>.</remarks>
public partial class ExternalPumpPanel : UserControl
{
    public ExternalPumpPanel()
    {
        InitializeComponent();
    }
}
