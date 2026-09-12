namespace OpenTECHub.Protocol;

/// <summary>One of the flowmeter's two MOSFET valve outputs, named as on the hardware panel.</summary>
public enum GasInput
{
    Input1 = 1,
    Input2 = 2,
}

/// <summary>
/// How the three valves of the gas rig are wired to the flowmeter's two outputs.
/// </summary>
/// <remarks>
/// <para>
/// The rig has three valves — <b>A</b> (air into the reactor), <b>B</b> (nitrogen) and
/// <b>C</b> (vent/bypass) — but the flowmeter drives only two outputs, and B and C are wired
/// to the <i>same</i> one. So the whole wiring is one fact: which input A is on; B/C are on
/// the other. The physical document
/// (<c>docs/plans/sistema_valvulas_ensaios_potencia_kLa</c>) fixes the default as
/// MOSFET 2 → A and MOSFET 1 → B+C; the operator can swap it in Configurações.
/// </para>
/// <para>
/// This is the only type that knows the mapping. Everything else speaks in
/// <see cref="GasRoute"/> intentions and lets <see cref="GasRouting"/> translate.
/// </para>
/// </remarks>
public sealed record GasRigConfiguration(GasInput AirInletInput)
{
    /// <summary>MOSFET 2 → A; MOSFET 1 → B+C — the wiring in the physical document.</summary>
    public static readonly GasRigConfiguration Default = new(GasInput.Input2);

    /// <summary>The input B and C share: always the one A is not on.</summary>
    public GasInput VentAndNitrogenInput => AirInletInput == GasInput.Input1 ? GasInput.Input2 : GasInput.Input1;

    /// <summary>The wiring as it reads on the panel: "A na entrada 2 · B/C na entrada 1".</summary>
    public string Describe()
        => $"A na entrada {(int)AirInletInput} · B/C na entrada {(int)VentAndNitrogenInput}";
}

/// <summary>Where the gas should go. The only three states a producer may ask for.</summary>
public enum GasRoute
{
    /// <summary>A closed, B/C closed. Valid only with a zero setpoint — otherwise the line is dead-ended.</summary>
    Closed,

    /// <summary>A open, B/C closed: air through the sparger.</summary>
    Reactor,

    /// <summary>B/C open, A closed: the line vents through C and nitrogen may enter through B.</summary>
    VentAndNitrogen,
}

/// <summary>What the flowmeter's echo says the valves are doing, including the two states nobody asks for.</summary>
public enum ObservedGasRoute
{
    Closed,
    Reactor,
    VentAndNitrogen,

    /// <summary>Both outputs energised: air and nitrogen paths open at once.</summary>
    BothOpen,

    /// <summary>Setpoint above zero with both outputs off: the controller is pushing into a closed line.</summary>
    DeadEnd,
}

/// <summary>
/// Intention ↔ outputs, in one place. Pure; no WPF, no I/O.
/// </summary>
public static class GasRouting
{
    /// <summary>The <c>valve_1</c>/<c>valve_2</c> pair that realises <paramref name="route"/> on <paramref name="rig"/>.</summary>
    public static (bool Valve1, bool Valve2) Resolve(GasRoute route, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var open = route switch
        {
            GasRoute.Closed => (GasInput?)null,
            GasRoute.Reactor => rig.AirInletInput,
            GasRoute.VentAndNitrogen => rig.VentAndNitrogenInput,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown gas route."),
        };

        return (open == GasInput.Input1, open == GasInput.Input2);
    }

    /// <summary>Reads an echo back into a route; the anomalous pairs get their own names.</summary>
    public static ObservedGasRoute Interpret(bool valve1, bool valve2, double setpoint, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        if (valve1 && valve2)
        {
            return ObservedGasRoute.BothOpen;
        }

        if (!valve1 && !valve2)
        {
            return setpoint > 0.0 ? ObservedGasRoute.DeadEnd : ObservedGasRoute.Closed;
        }

        var open = valve1 ? GasInput.Input1 : GasInput.Input2;
        return open == rig.AirInletInput ? ObservedGasRoute.Reactor : ObservedGasRoute.VentAndNitrogen;
    }

    /// <summary>The route as the operator reads it, with the hardware names.</summary>
    public static string Describe(GasRoute route) => route switch
    {
        GasRoute.Closed => "Fechado",
        GasRoute.Reactor => "Reator (A)",
        GasRoute.VentAndNitrogen => "Descarga + N₂ (B/C)",
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown gas route."),
    };

    /// <summary>The route with the input it lands on: "Reator (A na entrada 2)".</summary>
    public static string Describe(GasRoute route, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        return route switch
        {
            GasRoute.Closed => "Fechado",
            GasRoute.Reactor => $"Reator (A na entrada {(int)rig.AirInletInput})",
            GasRoute.VentAndNitrogen => $"Descarga + N₂ (B/C na entrada {(int)rig.VentAndNitrogenInput})",
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown gas route."),
        };
    }

    /// <summary>The observed state as the operator reads it; the anomalies are named as such.</summary>
    public static string Describe(ObservedGasRoute observed) => observed switch
    {
        ObservedGasRoute.Closed => "Fechado",
        ObservedGasRoute.Reactor => "Reator (A)",
        ObservedGasRoute.VentAndNitrogen => "Descarga + N₂ (B/C)",
        ObservedGasRoute.BothOpen => "A e B/C abertas",
        ObservedGasRoute.DeadEnd => "Gás sem destino",
        _ => throw new ArgumentOutOfRangeException(nameof(observed), observed, "Unknown observed route."),
    };

    /// <summary>The wire pair annotated with what each input drives: "valve_1=0 (B/C) · valve_2=1 (A)".</summary>
    public static string DescribeWire(bool valve1, bool valve2, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var label1 = rig.AirInletInput == GasInput.Input1 ? "A" : "B/C";
        var label2 = rig.AirInletInput == GasInput.Input2 ? "A" : "B/C";
        return $"valve_1={(valve1 ? 1 : 0)} ({label1}) · valve_2={(valve2 ? 1 : 0)} ({label2})";
    }

    /// <summary>True for the observed states that are a commanded route, false for the two anomalies.</summary>
    public static bool IsNominal(ObservedGasRoute observed)
        => observed is ObservedGasRoute.Closed or ObservedGasRoute.Reactor or ObservedGasRoute.VentAndNitrogen;
}
