using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TecnalHub.ViewModels;

namespace TecnalHub.Views;

/// <summary>All-setpoints and valve-control page.</summary>
public partial class ControlView : UserControl
{
    public ControlView()
    {
        InitializeComponent();
        AddHandler(TextBox.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnTextBoxCompleted), true);
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnToggleChanged), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnToggleChanged), true);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox box)
        {
            ApplyFor(box.DataContext);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.OriginalSource is TextBox escapeBox)
        {
            RevertFor(escapeBox.DataContext);
            e.Handled = true;
        }
    }

    private void OnTextBoxCompleted(object sender, KeyboardFocusChangedEventArgs e)
        => ApplyFor((e.OriginalSource as FrameworkElement)?.DataContext);

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        // Do not interfere with the oxygen cascade toggle: it has an intentional
        // engagement path in ControlParameterRowViewModel.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ControlParameterRowViewModel row && row.IsOxygenRow)
        {
            return;
        }

        ApplyFor((e.OriginalSource as FrameworkElement)?.DataContext);
    }

    private void ApplyFor(object? dataContext)
    {
        if (DataContext is ControlViewModel controlPage && !controlPage.CanActuate)
        {
            return;
        }

        switch (dataContext)
        {
            case ControlParameterRowViewModel row when row.Subsystem.ApplyCommand.CanExecute(null):
                row.Subsystem.ApplyCommand.Execute(null);
                break;
            case PHControlViewModel ph when ph.ApplyCommand.CanExecute(null):
                ph.ApplyCommand.Execute(null);
                break;
            case NutrientControlViewModel nutrient when nutrient.ApplyCommand.CanExecute(null):
                nutrient.ApplyCommand.Execute(null);
                break;
            case AntifoamControlViewModel antifoam when antifoam.ApplyCommand.CanExecute(null):
                antifoam.ApplyCommand.Execute(null);
                break;
            case FoamControlViewModel foam when foam.ApplyCommand.CanExecute(null):
                foam.ApplyCommand.Execute(null);
                break;
            case FlaskAgitatorViewModel agitator when agitator.ApplyCommand.CanExecute(null):
                agitator.ApplyCommand.Execute(null);
                break;
            case FlowControlViewModel when DataContext is ControlViewModel control && control.ApplyFlowStateCommand.CanExecute(null):
                control.ApplyFlowStateCommand.Execute(null);
                break;
        }
    }

    private static void RevertFor(object? dataContext)
    {
        switch (dataContext)
        {
            case ControlParameterRowViewModel row: row.Subsystem.RevertCommand.Execute(null); break;
            case PHControlViewModel ph: ph.RevertCommand.Execute(null); break;
            case NutrientControlViewModel nutrient: nutrient.RevertCommand.Execute(null); break;
            case AntifoamControlViewModel antifoam: antifoam.RevertCommand.Execute(null); break;
            case FoamControlViewModel foam: foam.RevertCommand.Execute(null); break;
            case FlaskAgitatorViewModel agitator: agitator.RevertCommand.Execute(null); break;
        }
    }
}
