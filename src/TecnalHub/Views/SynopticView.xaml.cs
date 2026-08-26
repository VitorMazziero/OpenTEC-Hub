using System.Windows.Controls;

namespace TecnalHub.Views;

/// <summary>
/// Operational process cards beside the approved reactor side-view render.
/// </summary>
/// <remarks>
/// Selection remains expressed through the shell commands; the reactor image is
/// presentation-only and contains no live control state.
/// </remarks>
public partial class SynopticView : UserControl
{
    public SynopticView() => InitializeComponent();
}
