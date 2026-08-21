using System.Globalization;

namespace TecnalHub.ViewModels;

/// <summary>
/// Shared operator-input parsing for the WP7 dosing cards.
/// </summary>
/// <remarks>
/// The nutrient, antifoam, foam and flask-agitator cards all stage plain text and share
/// the same rules: accept a pt-BR decimal comma, refuse anything non-finite, and never
/// silently substitute a plausible default. The wire is a separate matter —
/// <see cref="Protocol.TecnalCommand"/> formats invariantly by construction — so a comma
/// typed here can never reach the device as <c>6,98</c>. See <c>docs/PROTOCOL.md</c> §2.2.
/// </remarks>
internal static class DosingInput
{
    public static bool TryParseDouble(string? text, out double value)
        => double.TryParse(
               (text ?? "").Trim().Replace(',', '.'),
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out value) &&
           double.IsFinite(value);

    public static bool TryParseInteger(string? text, out int value)
    {
        value = default;
        return TryParseDouble(text, out var parsed) &&
               Math.Abs(parsed - Math.Round(parsed)) <= 1e-9 &&
               parsed is >= int.MinValue and <= int.MaxValue &&
               (value = (int)parsed) == parsed;
    }

    public static string Format(double value, int decimals)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    public static string FormatInt(int value) => value.ToString(CultureInfo.CurrentCulture);
}
