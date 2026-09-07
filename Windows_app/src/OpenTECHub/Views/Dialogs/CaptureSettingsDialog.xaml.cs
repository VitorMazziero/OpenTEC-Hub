using System.Windows;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views.Dialogs;

public partial class CaptureSettingsDialog : Window
{
    public CaptureSettingsDialog(PowerTestViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
