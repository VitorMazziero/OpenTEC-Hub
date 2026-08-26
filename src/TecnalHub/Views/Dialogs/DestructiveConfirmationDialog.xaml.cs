using System.Windows;

namespace TecnalHub.Views.Dialogs;

/// <summary>Destructive confirmation that defaults to cancellation.</summary>
public partial class DestructiveConfirmationDialog : Window
{
    public DestructiveConfirmationDialog(
        string title,
        string consequence,
        string? exactCommand = null,
        string confirmText = "Confirmar",
        string cancelText = "Cancelar",
        bool isDanger = true)
    {
        InitializeComponent();
        var hasExactCommand = !string.IsNullOrWhiteSpace(exactCommand);
        var resolvedConfirmText = confirmText != "Confirmar"
            ? confirmText
            : (hasExactCommand ? "Confirmar parada" : "Confirmar");

        DataContext = new DialogContent(
            title,
            consequence,
            exactCommand ?? "",
            hasExactCommand,
            resolvedConfirmText,
            cancelText,
            isDanger);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private sealed record DialogContent(
        string DialogTitle,
        string Consequence,
        string ExactCommand,
        bool HasExactCommand,
        string ConfirmText,
        string CancelText,
        bool IsDanger);
}
