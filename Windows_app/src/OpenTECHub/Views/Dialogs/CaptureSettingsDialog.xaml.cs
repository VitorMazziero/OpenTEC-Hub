using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views.Dialogs;

public partial class CaptureSettingsDialog : Window
{
    private readonly PowerTestViewModel _viewModel;

    public CaptureSettingsDialog(PowerTestViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Esc is the same gesture as the caption X — discard, asking first if there is a diff.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCloseWindow(sender, e);
        }
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// The caption X is "discard": nothing typed here reaches the assay unless the operator
    /// concludes, so an X over a real change asks first. A closed window with no diff is just closed.
    /// </summary>
    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasUnsavedCaptureSettings &&
            MessageBox.Show(this, "Descartar as alterações nos critérios?", "Critérios de parada",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        DialogResult = false;
        Close();
    }

    /// <summary>Concluir. Commits the focused field first — the text boxes update on LostFocus.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        DialogResult = true;
        Close();
    }
}
