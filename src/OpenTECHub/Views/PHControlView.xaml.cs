using System.Windows.Controls;
using System.Windows.Input;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views;

public partial class PHControlView : UserControl
{
    public PHControlView()
    {
        InitializeComponent();
        AddHandler(TextBox.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFieldLostFocus));
        AddHandler(TextBox.PreviewKeyDownEvent, new KeyEventHandler(OnFieldPreviewKeyDown));
    }

    private void OnToggleClicked(object sender, System.Windows.RoutedEventArgs e) => ApplyPending();

    private void OnFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyPending();

    private void OnFieldPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPending();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is PHControlViewModel vm)
        {
            vm.RevertCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ApplyPending()
    {
        if (DataContext is PHControlViewModel vm && vm.HasPendingChange && vm.ApplyCommand.CanExecute(null))
        {
            vm.ApplyCommand.Execute(null);
        }
    }
}
