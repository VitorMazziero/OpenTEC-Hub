using System.Windows;
using TecnalHub.ViewModels;

namespace TecnalHub.Views.Dialogs;

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
}
