namespace OpenTECHub.Protocol;

/// <summary>
/// The five external-pump flow profiles the firmware understands, by wire <c>mode</c> value.
/// </summary>
/// <remarks>
/// The numbers are the firmware's, not an implementation detail: they travel on the wire as
/// the <c>mode</c> key. <c>0</c> is the idle/disabled mode the safe frame carries. See
/// <c>docs/PROTOCOL.md</c> §3.5 and the v.6 <c>pump_mode_window</c>.
/// </remarks>
public enum PumpProfileMode
{
    /// <summary>Idle. Carried by the safe disable frame, never as an operating profile.</summary>
    Idle = 0,

    /// <summary>Constant: <c>Q(t') = λ</c>.</summary>
    Constant = 1,

    /// <summary>Linear: <c>Q(t') = λ + φ·t'</c>.</summary>
    Linear = 2,

    /// <summary>Exponential: <c>Q(t') = λ·e^(φ·t')</c>.</summary>
    Exponential = 3,

    /// <summary>Polynomial: <c>Q(t') = p0 + p1·t' + … + pN·t'^N</c>, <c>N ≤ 20</c>.</summary>
    Polynomial = 4,

    /// <summary>Piecewise-linear interpolation through operator points, 2 to 100 of them.</summary>
    Piecewise = 5,
}
