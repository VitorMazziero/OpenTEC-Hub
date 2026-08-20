using System.Windows;

namespace TecnalHub.Views.Dialogs;

/// <summary>Destructive confirmation that defaults to cancellation.</summary>
public partial class DestructiveConfirmationDialog : Window
{
    public DestructiveConfirmationDialog(string title, string consequence, string exactCommand)
    {
        InitializeComponent();
        DataContext = new DialogContent(title, consequence, exactCommand);
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

    private sealed record DialogContent(string DialogTitle, string Consequence, string ExactCommand);
}
