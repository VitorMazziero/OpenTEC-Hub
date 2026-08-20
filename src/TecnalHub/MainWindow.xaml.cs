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

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var wide = ActualWidth >= SidePaneMinimumWidth;

        SideDetail.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = wide ? new GridLength(380) : new GridLength(0);

        DrawerDetail.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        DrawerRow.Height = wide ? new GridLength(0) : new GridLength(300);
    }
}
