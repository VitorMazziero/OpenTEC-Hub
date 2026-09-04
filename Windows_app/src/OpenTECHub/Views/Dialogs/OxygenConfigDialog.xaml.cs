using System.Windows;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views.Dialogs;

public partial class OxygenConfigDialog : Window
{
    public OxygenConfigDialog(OxygenConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested = () =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
