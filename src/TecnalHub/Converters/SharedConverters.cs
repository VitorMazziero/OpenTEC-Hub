using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TecnalHub.ViewModels;

namespace TecnalHub.Converters;

/// <summary>
/// Maps a state key to the matching themed brush.
/// </summary>
/// <remarks>
/// Resolves through <c>Application.Current.Resources</c> so the colour follows a
/// runtime theme switch. ViewModels expose a state <i>name</i>, never a brush - see
/// <c>docs/CONVENTIONS.md</c>.
/// </remarks>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            VariableState state => state.ToString(),
            string name => name,
            _ => "Idle",
        };

        return Application.Current?.TryFindResource($"State{key}Brush") as Brush
               ?? Application.Current?.TryFindResource("StateIdleBrush") as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Renders a trend as an arrow glyph.</summary>
public sealed class TrendToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            TrendDirection.Rising => "▲",
            TrendDirection.Falling => "▼",
            _ => "–",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Boolean to <see cref="Visibility"/>. Pass <c>Invert</c> as the parameter to negate.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// Visible when the bound value is non-null and not an empty string.
/// </summary>
/// <remarks>
/// Used for the validation message, which must appear only when there is something to
/// say. Binding <c>Visibility</c> to the message itself keeps the ViewModel free of
/// presentation state.
/// </remarks>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string text && text.Length == 0)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible when the bound value equals the parameter.
/// </summary>
/// <remarks>
/// Drives page switching from the nav rail's selected id. Pages are kept in the visual
/// tree rather than swapped, so a chart does not lose its history and rebuild every
/// time the operator glances at another page.
/// </remarks>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter. Used for nav-rail selection.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
