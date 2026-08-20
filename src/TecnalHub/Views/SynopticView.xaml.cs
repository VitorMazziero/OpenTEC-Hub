using System.Windows.Controls;

namespace TecnalHub.Views;

/// <summary>
/// Vector schematic of the reactor, with live values pinned at the hardware that
/// produces them.
/// </summary>
/// <remarks>
/// No code-behind logic: selection is expressed as input bindings onto the shell's
/// commands. See <c>docs/UI_DESIGN.md</c> section 2.
/// </remarks>
public partial class SynopticView : UserControl
{
    public SynopticView() => InitializeComponent();
}
