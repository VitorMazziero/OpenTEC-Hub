using System.Windows;
using TecnalHub.Views.Dialogs;

namespace TecnalHub.Services.Dialogs;

/// <summary>WPF implementation of the application's modal dialogs.</summary>
public sealed class DialogService : IDialogService
{
    public bool ConfirmDestructive(string title, string consequence, string exactCommand)
    {
        var dialog = new DestructiveConfirmationDialog(title, consequence, exactCommand)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true;
    }
}
