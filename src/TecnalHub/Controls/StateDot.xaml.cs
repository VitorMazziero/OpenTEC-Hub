using System.Windows;
using System.Windows.Controls;
using TecnalHub.Converters;
using TecnalHub.ViewModels;

namespace TecnalHub.Controls;

/// <summary>
/// The state indicator: a filled circle in the state colour.
/// </summary>
/// <remarks>
/// <para>
/// With <see cref="StateChip"/>, one of the only two components permitted to put a
/// state colour on screen. See <c>docs/UI_DESIGN.md</c> section 6.4.
/// </para>
/// <para>
/// <b>A dot never travels alone.</b> Colour is not the only signal anywhere in this
/// application, so a bare dot must sit next to a label, a value or a chip that says the
/// same thing in words - otherwise a red/green-blind operator loses the state entirely.
/// <see cref="Label"/> carries that wording into the tooltip for the cases where the
/// dot is the only mark in its row.
/// </para>
/// </remarks>
public partial class StateDot : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State), typeof(VariableState), typeof(StateDot),
            new PropertyMetadata(VariableState.Idle));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(StateDot),
            new PropertyMetadata(8.0));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(StateDot),
            new PropertyMetadata(null));

    public StateDot() => InitializeComponent();

    /// <summary>How the equipment is behaving.</summary>
    public VariableState State
    {
        get => (VariableState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Diameter in device-independent pixels. 8 is the standard mark.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Tooltip wording, for rows where the dot is the only mark.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
