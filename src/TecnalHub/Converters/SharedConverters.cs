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
/// <para>
/// Resolves through <c>Application.Current.Resources</c> so the colour follows a
/// runtime theme switch. ViewModels expose a state <i>name</i>, never a brush - see
/// <c>docs/CONVENTIONS.md</c>.
/// </para>
/// <para>
/// Pass <c>Text</c> as the parameter to get the darkened <c>State*TextBrush</c> variant.
/// <b>Anything that renders a state as a word must do this.</b> The fill variants are
/// tuned for an 8 px dot at 3:1 and do not clear 4.5:1 as type - amber on white is the
/// case that fails hardest. See <c>docs/UI_DESIGN.md</c> section 3.3.
/// </para>
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

        var suffix = string.Equals(parameter as string, "Text", StringComparison.OrdinalIgnoreCase)
            ? "TextBrush"
            : "Brush";

        return Application.Current?.TryFindResource($"State{key}{suffix}") as Brush
               ?? Application.Current?.TryFindResource($"StateIdle{suffix}") as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a state to its pt-BR label, for <c>StateChip</c>.</summary>
/// <remarks>
/// <b>Colour is never the only signal.</b> Every state that reaches the screen as a
/// colour also reaches it as a word, so a red/green-blind operator can run a
/// cultivation. See <c>docs/UI_DESIGN.md</c> section 11.
/// </remarks>
public sealed class StateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            VariableState.Ok => "Normal",
            VariableState.Actuating => "Atuando",
            VariableState.Warning => "Atenção",
            VariableState.Alarm => "Alarme",
            _ => "Ocioso",
        };

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
    {
        var hasContent = value is not null && !(value is string text && text.Length == 0);

        // Invert is how a range hint yields the floor to its error message: two lines
        // of advice competing for the same glance is one line too many.
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasContent = !hasContent;
        }

        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

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

/// <summary>
/// A non-negative double to a star <see cref="GridLength"/>.
/// </summary>
/// <remarks>
/// Lets a proportion drive a grid column width directly, which is how the cascade
/// allocation bar places each actuator's window on the 0-100 % effort track without any
/// pixel arithmetic in code-behind. A non-positive or non-finite value collapses to
/// <c>0*</c>.
/// </remarks>
public sealed class DoubleToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var weight = value is double d && double.IsFinite(d) && d > 0 ? d : 0.0;
        return new GridLength(weight, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// An <c>#RRGGBB</c> hex string to a frozen <see cref="SolidColorBrush"/>.
/// </summary>
/// <remarks>
/// The recipe block category colours are declared once, as hex, in
/// <c>RecipeNodeCatalog</c> — data shared by the model, not a themed token — so the view
/// converts them here rather than the ViewModel exposing a brush.
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                // Fall through to the neutral default below.
            }
        }

        return Brushes.Gray;
    }

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

/// <summary>Inverts a boolean value.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Maps a loop's "Ativo" flag to the state its dot may show.</summary>
/// <remarks>
/// A green dot on a loop nobody switched on says the reactor is doing something it is
/// not. The dot only leaves <see cref="VariableState.Idle"/> once the operator has
/// enabled the monitoring/control loop for that row. See <c>docs/UI_DESIGN.md</c>
/// section 3.3.
/// </remarks>
public sealed class ActiveToStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? VariableState.Ok : VariableState.Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// An external device's dot:
/// <c>(isEnabled, isOffline, isPending, hasRoutingMismatch, [variableState])</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dot on the four external-device rows used to bind straight to the operator's own
/// enable checkbox, so it reported their intent back at them and called it hardware state.
/// This is the correction: the switch only decides idle from active, and what the Hub says
/// decides everything above that.
/// </para>
/// <para>
/// <b>It carries the same severity as the chip beside it, deliberately.</b> A red
/// <i>desconectado</i> chip next to an amber dot is two different answers to one question,
/// and the operator has to work out which to believe. So the order here is the chip's own
/// precedence: a reported absence is an alarm even when the operator's switch is off —
/// the device is not there, and that is true regardless of what they meant to do with it.
/// </para>
/// </remarks>
public sealed class ExternalDeviceStateConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var enabled = values.Length > 0 && values[0] is true;
        var offline = values.Length > 1 && values[1] is true;
        var pending = values.Length > 2 && values[2] is true;
        var routingMismatch = values.Length > 3 && values[3] is true;

        if (offline)
        {
            return VariableState.Alarm;
        }

        if (pending || routingMismatch)
        {
            return VariableState.Warning;
        }

        // A fifth value is the row's own variable state, for the one external device that has
        // a measured band to report. Nothing above this point can be expressed by that state -
        // it knows about the reading, not about the link - so the link conditions come first
        // and this only fills in the healthy case.
        if (values.Length > 4 && values[4] is VariableState measured)
        {
            return enabled ? measured : VariableState.Idle;
        }

        return enabled ? VariableState.Ok : VariableState.Idle;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Gates a variable's own state on its loop being active: <c>(state, isActive)</c>.
/// </summary>
/// <remarks>
/// The companion to <see cref="ActiveToStateConverter"/> for rows that do have a live
/// reading. While the loop is off the reading is not being acted on, so the dot stays
/// idle rather than reporting a band the controller is not holding.
/// </remarks>
public sealed class ActiveVariableStateConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = values.Length > 1 && values[1] is true;
        return active && values[0] is VariableState state ? state : VariableState.Idle;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
