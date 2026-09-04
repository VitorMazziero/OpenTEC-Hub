using System.Windows;
using System.Windows.Controls;

namespace OpenTECHub.Controls;

/// <summary>
/// The validated setpoint entry, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Binds to a <see cref="ViewModels.SubsystemViewModel"/> as its <c>DataContext</c>.
/// Used by the detail pane, the Controle table and later the recipe blocks - the
/// validation rules in <c>docs/UI_DESIGN.md</c> section 9 are only worth anything if
/// every entry point enforces the same ones, and three hand-written copies is how one
/// of them ends up not doing so.
/// </para>
/// <para>
/// <b>Enter applies and Esc reverts</b> are wired here rather than at the page level so
/// they cannot be forgotten by a caller. Note that <c>Apply</c> re-validates inside the
/// command as well: <c>CanExecute</c> only greys out a button, and the Enter key, a
/// recipe engine or a test all reach the command directly.
/// </para>
/// </remarks>
public partial class SetpointField : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(SetpointField),
            new PropertyMetadata("Setpoint"));

    public static readonly DependencyProperty ShowLabelProperty =
        DependencyProperty.Register(
            nameof(ShowLabel), typeof(bool), typeof(SetpointField),
            new PropertyMetadata(true));

    public SetpointField() => InitializeComponent();

    /// <summary>Caption above the field.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// False in a table, where the column header is already the label.
    /// </summary>
    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }
}
