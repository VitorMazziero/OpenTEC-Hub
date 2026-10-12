using System.Globalization;
using System.Windows;
using System.Windows.Data;
using OpenTECHub.Services.Control;

namespace OpenTECHub.Controls;

/// <summary>True when the value is not null (enables the slider only with a plan).</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Pulse precision zone → the state text brush (alarm, warning, ok).</summary>
public sealed class DoseZoneBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PumpDoseZone.Unavailable => "StateAlarmTextBrush",
            PumpDoseZone.LowPrecision => "StateWarningTextBrush",
            PumpDoseZone.Good => "StateOkTextBrush",
            _ => "TextSecondaryBrush",
        };
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
