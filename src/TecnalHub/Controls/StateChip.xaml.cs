using System.Windows;
using System.Windows.Controls;
using TecnalHub.ViewModels;

namespace TecnalHub.Controls;

/// <summary>
/// State as a dot plus the pt-BR word for it.
/// </summary>
/// <remarks>
/// Used in pane and page headers, where there is room to say what the colour means.
/// <see cref="StateDot"/> alone is for dense contexts - tiles, rails, table rows.
/// See <c>docs/UI_DESIGN.md</c> section 6.4.
/// </remarks>
public partial class StateChip : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State), typeof(VariableState), typeof(StateChip),
            new PropertyMetadata(VariableState.Idle));

    public StateChip() => InitializeComponent();

    /// <summary>How the equipment is behaving.</summary>
    public VariableState State
    {
        get => (VariableState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
