using System.Windows;
using System.Windows.Input;

namespace OpenTECHub.Views.Dialogs;

public partial class InputDialog : Window
{
    public string InputText { get; private set; } = "";

    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Commit();
    }

    private void OnInputTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        InputText = InputTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }
}
