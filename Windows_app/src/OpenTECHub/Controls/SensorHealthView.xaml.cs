using System.Windows;
using System.Windows.Controls;

namespace OpenTECHub.Controls;

/// <summary>Compact, shared statistical health summary for measured sensors.</summary>
/// <remarks>
/// Two layouts over the same bindings: the stacked list (five rows, label left and value
/// right) and <see cref="IsCompact"/>, a strip of five columns in two rows (label above,
/// value below) that lets a drawer stay two lines tall instead of six.
/// </remarks>
public partial class SensorHealthView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(SensorHealthView),
        new PropertyMetadata(false, (d, _) => ((SensorHealthView)d).ApplyLayout()));

    public SensorHealthView()
    {
        InitializeComponent();
        ApplyLayout();
    }

    /// <summary>Horizontal two-row strip instead of the five-row list.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private void ApplyLayout()
    {
        StackedLayout.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactLayout.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
    }
}
