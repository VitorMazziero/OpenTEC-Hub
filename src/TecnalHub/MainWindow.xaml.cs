using System.Windows;

namespace TecnalHub;

/// <summary>
/// Shell window. Hosts the KPI strip, the navigation rail and the active page.
/// </summary>
/// <remarks>
/// Code-behind holds no logic: state and commands live in
/// <see cref="ViewModels.ShellViewModel"/>. The only thing here is the responsive
/// switch, which is a visual-tree concern XAML cannot express - WPF has no media
/// queries, and a VisualStateManager would need the same width threshold written
/// twice.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// Below this width the detail pane moves under the synoptic.
    /// </summary>
    /// <remarks>
    /// Side by side, the pane needs ~380 px and the diagram stops being readable
    /// under ~700 px. See <c>docs/UI_DESIGN.md</c>, "Responsive behaviour".
    /// </remarks>
    private const double SidePaneMinimumWidth = 1200;

    /// <summary>
    /// Below this width the variable rail folds away.
    /// </summary>
    /// <remarks>
    /// Nav rail, variable rail, synoptic and detail pane all want horizontal space; at
    /// four columns the diagram is the one that suffers, and the diagram is what makes
    /// this a bioreactor application. The operator's preference is untouched - the rail
    /// returns on its own once there is room. See <c>docs/UI_DESIGN.md</c> section 4.7.
    /// </remarks>
    private const double VariableRailMinimumWidth = 1400;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var wide = ActualWidth >= SidePaneMinimumWidth;

        if (DataContext is ViewModels.ShellViewModel shell)
        {
            shell.IsRailAffordable = ActualWidth >= VariableRailMinimumWidth;
        }

        // The HOSTS are toggled, never the panes inside them. Each host holds both a
        // controllable and a read-only pane, and which one shows is a data question
        // answered by a binding - assigning Visibility on those panes from here would
        // replace the binding with a local value and permanently break it.
        SideDetailHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = wide ? new GridLength(380) : new GridLength(0);

        DrawerDetailHost.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        DrawerRow.Height = wide ? new GridLength(0) : new GridLength(300);
    }
}
