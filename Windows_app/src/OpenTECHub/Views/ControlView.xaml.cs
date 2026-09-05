using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views;

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
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSliderDragCompleted), true);
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
    {
        var dataContext = (e.OriginalSource as FrameworkElement)?.DataContext;
        if (dataContext is BiomassControlViewModel)
        {
            // Biomass thresholds are a three-field atomic contract. Moving focus between
            // low/high/optimal only stages the values; Enter or "Enviar limiares" applies.
            return;
        }

        ApplyFor(dataContext);
    }

    private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e)
        => ApplyFor((e.OriginalSource as FrameworkElement)?.DataContext);

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.Tag?.ToString() == "ExpanderToggle")
        {
            return;
        }

        // Do not interfere with the oxygen cascade toggle: it has an intentional
        // engagement path in ControlParameterRowViewModel.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ControlParameterRowViewModel row && row.IsOxygenRow)
        {
            return;
        }

        if ((e.OriginalSource as FrameworkElement)?.DataContext is BiomassControlViewModel)
        {
            // Biomass enable is an immediate property-driven command. Do not also treat
            // the toggle as an implicit threshold apply.
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
            case ControlParameterRowViewModel row
                when DataContext is ControlViewModel flowPage &&
                     ReferenceEquals(row.Subsystem, flowPage.FlowSubsystem) &&
                     flowPage.ApplyFlowStateCommand.CanExecute(null):
                flowPage.ApplyFlowStateCommand.Execute(null);
                break;
            case ControlParameterRowViewModel row when row.Subsystem.ApplyCommand.CanExecute(null):
                row.Subsystem.ApplyCommand.Execute(null);
                break;
            case SubsystemViewModel sub when sub.ApplyCommand.CanExecute(null):
                sub.ApplyCommand.Execute(null);
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
            case BiomassControlViewModel biomass when biomass.ApplyThresholdsCommand.CanExecute(null):
                biomass.ApplyThresholdsCommand.Execute(null);
                break;
            case ServoDriveViewModel servo when servo.ApplyPollIntervalCommand.CanExecute(null):
                servo.ApplyPollIntervalCommand.Execute(null);
                break;
            case FlowControlViewModel when DataContext is ControlViewModel control && control.ApplyFlowStateCommand.CanExecute(null):
                control.ApplyFlowStateCommand.Execute(null);
                break;
            case ControlViewModel control when control.ApplyFlowStateCommand.CanExecute(null):
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
            case BiomassControlViewModel biomass: biomass.RevertCommand.Execute(null); break;
            case PumpControlViewModel pump: pump.RevertCommand.Execute(null); break;
        }
    }
}
