using System;
using System.Collections.ObjectModel;
using System.Windows;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Views.Dialogs;

namespace OpenTECHub.Services.Dialogs;

/// <summary>WPF implementation of the application's modal dialogs.</summary>
public sealed class DialogService : IDialogService
{
    public bool ConfirmDestructive(string title, string consequence, string exactCommand)
    {
        var dialog = new DestructiveConfirmationDialog(title, consequence, exactCommand)
        {
            Owner = Application.Current?.MainWindow,
        };

        DialogBounds.ConstrainToOwner(dialog);
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

        DialogBounds.ConstrainToOwner(dialog);
        return dialog.ShowDialog() == true;
    }

    public bool PromptInput(string title, string message, out string response, string initialValue = "")
    {
        var dialog = new InputDialog(title, message, initialValue)
        {
            Owner = Application.Current?.MainWindow,
        };

        DialogBounds.ConstrainToOwner(dialog);
        if (dialog.ShowDialog() == true)
        {
            response = dialog.InputText;
            return true;
        }

        response = "";
        return false;
    }

    public RecipeStartOption PromptRecipeStart(string recipeName)
    {
        var dialog = new StartRecipeDialog(recipeName)
        {
            Owner = Application.Current?.MainWindow,
        };

        DialogBounds.ConstrainToOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : RecipeStartOption.Cancel;
    }

    public bool ShowImpellerCatalog(
        ObservableCollection<Impeller> catalog,
        Impeller? targetStage,
        out Impeller? selectedImpeller,
        Action? saveCatalog = null,
        Action<Impeller>? onAddToAssembly = null)
    {
        var dialog = new ImpellerCatalogDialog(catalog, targetStage, saveCatalog, onAddToAssembly)
        {
            Owner = Application.Current?.MainWindow,
        };

        DialogBounds.ConstrainToOwner(dialog);
        if (dialog.ShowDialog() == true)
        {
            selectedImpeller = dialog.SelectedResult;
            return true;
        }

        selectedImpeller = null;
        return false;
    }

    public void ShowCaptureSettings(ViewModels.PowerTestViewModel viewModel)
    {
        var dialog = new CaptureSettingsDialog(viewModel)
        {
            Owner = Application.Current?.MainWindow,
        };
        DialogBounds.ConstrainToOwner(dialog);
        dialog.ShowDialog();
    }
}
