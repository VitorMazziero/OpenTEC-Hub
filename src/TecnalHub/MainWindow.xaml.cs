using System.Windows;

namespace TecnalHub;

/// <summary>
/// Shell window. Hosts the KPI strip, the navigation rail and the active page.
/// </summary>
/// <remarks>
/// Code-behind stays empty of logic: state and commands belong in
/// <c>ViewModels/ShellViewModel</c>. The only things allowed here are visual-tree
/// concerns WPF cannot express in XAML.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
