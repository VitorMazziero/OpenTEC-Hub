namespace OpenTECHub.Services.Recipes;

/// <summary>Direction of a node port.</summary>
public enum PortDirection
{
    /// <summary>Flow into the node.</summary>
    In,

    /// <summary>Flow out of the node.</summary>
    Out,
}

/// <summary>
/// One port on a node, declared once in the block definition.
/// </summary>
/// <param name="Name">Canonical connector name written into recipe JSON.</param>
/// <param name="Direction">Whether the port is an inbound or outbound flow port.</param>
/// <param name="Label">pt-BR label shown on the block face, when it differs from the name.</param>
/// <param name="Multiple">
/// True for a port that accepts many connections — the join blocks' inbound port and the
/// implicit fan-out of any outbound port.
/// </param>
public sealed record RecipePort(string Name, PortDirection Direction, string? Label = null, bool Multiple = false);

/// <summary>
/// The single source of connector names, ported from ReceitasOpenTEC's <c>ConnectorNames</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <b>canonical</b> names are the ones the editor writes today; the <b>tolerated</b>
/// spellings (accented or joined) are recognised on load and never written, so old
/// hand-written and migrated recipes keep working. Every "is this the cascade's Saída/Entrada
/// Loop?" decision must go through <see cref="IsLoopOut"/> / <see cref="IsLoopIn"/>, so the set
/// of accepted spellings is defined in exactly one place and no checker can forget a variant.
/// </para>
/// </remarks>
public static class ConnectorNames
{
    /// <summary>Normal flow out.</summary>
    public const string Out = "Saida";

    /// <summary>Normal flow in.</summary>
    public const string In = "Entrada";

    /// <summary>
    /// Cascade: the loop's <b>exit condition</b>. The block wired here is <i>read</i> once per PID
    /// iteration and never executed as a flow step — the engine only asks it "should the loop stop?".
    /// The wire name is historical ("Saida Loop"); the port is labelled "Condição de Saída".
    /// </summary>
    public const string LoopOut = "Saida Loop";

    /// <summary>Cascade: where the exit condition wires back, closing the loop on the canvas.</summary>
    public const string LoopIn = "Entrada Loop";

    // Tolerated on load (accented / joined), never written — keeps legacy recipes loading.
    private static readonly string[] LoopOutSpellings = [LoopOut, "Saída Loop", "SaidaLoop"];
    private static readonly string[] LoopInSpellings = [LoopIn, "Entrada Loop", "EntradaLoop"];

    /// <summary>True when the name is the cascade's "Saída Loop" (any accepted spelling).</summary>
    public static bool IsLoopOut(string? name)
        => name is not null && Array.IndexOf(LoopOutSpellings, name) >= 0;

    /// <summary>True when the name is the cascade's "Entrada Loop" (any accepted spelling).</summary>
    public static bool IsLoopIn(string? name)
        => name is not null && Array.IndexOf(LoopInSpellings, name) >= 0;

    /// <summary>Normalises a tolerated spelling to its canonical form, for writing.</summary>
    public static string Canonical(string name)
    {
        if (IsLoopOut(name))
        {
            return LoopOut;
        }

        if (IsLoopIn(name))
        {
            return LoopIn;
        }

        return name;
    }
}
