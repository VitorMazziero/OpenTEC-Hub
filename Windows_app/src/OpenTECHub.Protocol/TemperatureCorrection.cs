using System.Globalization;

namespace OpenTECHub.Protocol;

/// <summary>
/// Offset correction of the reactor temperature sensor: <c>real = read + offset</c> (D-073).
/// </summary>
/// <remarks>
/// <para>
/// The module's probe reads a constant amount off the real reactor temperature. Everything in
/// the application works with the <b>real</b> temperature: the parser corrects the readings and
/// the setpoint echo, and every <c>tempSetpoint</c> leaves the PC in the module's own scale
/// (<c>real − offset</c>), so the module's loop and the Hub's bath cascade, which both compare
/// against the uncorrected probe, hold the reactor at the real setpoint.
/// </para>
/// <para>
/// <c>tempSetpoint = 0</c> means "temperature off" (and stops the bath cascade); it is never
/// shifted.
/// </para>
/// </remarks>
public static class TemperatureCorrection
{
    /// <summary>Largest accepted correction, in °C. A bigger difference is a probe fault, not an offset.</summary>
    public const double MaximumOffsetC = 5.0;

    /// <summary>True when <paramref name="offsetC"/> is usable as a correction.</summary>
    public static bool IsValidOffset(double offsetC)
        => double.IsFinite(offsetC) && Math.Abs(offsetC) <= MaximumOffsetC;

    /// <summary>The real temperature for a probe reading.</summary>
    public static double ToReal(double readC, double offsetC) => readC + offsetC;

    /// <summary>
    /// A copy of <paramref name="command"/> whose <c>tempSetpoint</c> is in the module's scale,
    /// or <paramref name="command"/> itself when there is nothing to shift.
    /// </summary>
    public static OpenTECCommand ToModule(OpenTECCommand command, double offsetC)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (offsetC == 0 || !IsValidOffset(offsetC) ||
            command.GetRawValue(CommandKeys.TempSetpoint) is not { } raw ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ||
            real <= 0)
        {
            return command;
        }

        var module = Math.Round(real - offsetC, 2);
        return OpenTECCommand.Create().Merge(command).Set(CommandKeys.TempSetpoint, Math.Max(module, 0.01));
    }
}
