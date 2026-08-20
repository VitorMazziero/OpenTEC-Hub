using System.Windows;
using System.Windows.Controls;

namespace TecnalHub.Views;

/// <summary>
/// Theme-aware operational overlay around the neutral reactor equipment render.
/// </summary>
/// <remarks>
/// Selection remains expressed through the shell commands. The only view-specific
/// code is a fail-safe that exposes the bundled vector schematic if the PNG cannot
/// be decoded. See <c>docs/UI_DESIGN.md</c> section 5.1.
/// </remarks>
public partial class SynopticView : UserControl
{
    public SynopticView() => InitializeComponent();

    private void OnReactorRenderFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ReactorRender.Visibility = Visibility.Collapsed;
        VectorFallback.Visibility = Visibility.Visible;
    }
}
