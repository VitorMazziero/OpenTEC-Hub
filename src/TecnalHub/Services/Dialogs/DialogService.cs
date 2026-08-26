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

    public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false)
    {
        var dialog = new DestructiveConfirmationDialog(
            title,
            message,
            exactCommand: null,
            confirmText: confirmText,
            cancelText: cancelText,
            isDanger: isDanger)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true;
    }

    public bool PromptInput(string title, string message, out string response, string initialValue = "")
    {
        var dialog = new InputDialog(title, message, initialValue)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() == true)
        {
            response = dialog.InputText;
            return true;
        }

        response = "";
        return false;
    }
}
