namespace OpenTECHub.Protocol;

/// <summary>Tuning of the Hub-resident external-bath cascade (Hub 10.6).</summary>
/// <remarks>
/// The same record carries the operator's draft and the Hub's echo, so the app can show
/// which values are vigent and which are edited. <see cref="Validate"/> mirrors
/// <c>ExternalBathCascade::validateConfig</c> in the firmware one to one: the Hub rejects the
/// whole frame atomically, so a value the app would let through and the Hub refuse is a
/// silent failure the operator cannot see.
/// </remarks>
public sealed record BathCascadeTuning(
    double Kp,
    double TiS,
    double BiasC,
    int PeriodMs,
    double FilterS,
    int CommandMinMs,
    double CommandBandC,
    double SlewCMin,
    double OffsetHighC,
    double OffsetLowC,
    double OutputMinC,
    double OutputMaxC)
{
    /// <summary>Firmware defaults; provisional values until water identification (H09).</summary>
    public static BathCascadeTuning Defaults { get; } =
        new(0.5, 600.0, 0.6, 10_000, 20.0, 30_000, 0.1, 0.5, 5.0, 5.0, 5.0, 90.0);

    /// <summary>Returns null when the Hub would accept the set, otherwise a pt-BR reason.</summary>
    public string? Validate()
    {
        static bool Finite(double v) => double.IsFinite(v);

        if (!Finite(Kp) || Kp <= 0) return "Kp deve ser maior que zero.";
        if (!Finite(TiS) || TiS <= 0) return "Ti deve ser maior que zero.";
        if (!Finite(BiasC)) return "Bias deve ser um número finito.";
        if (PeriodMs is < 100 or > 600_000) return "Período deve estar entre 100 e 600000 ms.";
        if (!Finite(FilterS) || FilterS <= 0 || FilterS > 3600) return "Filtro deve estar entre 0 e 3600 s.";
        if (CommandMinMs is < 0 or > 3_600_000) return "Comando mínimo deve estar entre 0 e 3600000 ms.";
        if (!Finite(CommandBandC) || CommandBandC <= 0 || CommandBandC > 10) return "Banda deve estar entre 0 e 10 °C.";
        if (!Finite(SlewCMin) || SlewCMin <= 0 || SlewCMin > 100) return "Slew deve estar entre 0 e 100 °C/min.";
        if (!Finite(OffsetHighC) || OffsetHighC < 0 || OffsetHighC > 100) return "Offset alto deve estar entre 0 e 100 °C.";
        if (!Finite(OffsetLowC) || OffsetLowC < 0 || OffsetLowC > 100) return "Offset baixo deve estar entre 0 e 100 °C.";
        if (!Finite(OutputMinC) || !Finite(OutputMaxC) || OutputMinC < 0 || OutputMaxC > 100)
            return "Saídas mínima e máxima devem estar entre 0 e 100 °C.";
        if (OutputMinC >= OutputMaxC) return "Saída mínima deve ser menor que a máxima.";
        return null;
    }

    /// <summary>True when every value matches <paramref name="other"/> within display precision.</summary>
    public bool SameAs(BathCascadeTuning? other)
    {
        if (other is null) return false;
        static bool Near(double a, double b) => Math.Abs(a - b) <= 0.0005 * Math.Max(1.0, Math.Abs(a));
        return Near(Kp, other.Kp) && Near(TiS, other.TiS) && Near(BiasC, other.BiasC) &&
               PeriodMs == other.PeriodMs && Near(FilterS, other.FilterS) &&
               CommandMinMs == other.CommandMinMs && Near(CommandBandC, other.CommandBandC) &&
               Near(SlewCMin, other.SlewCMin) && Near(OffsetHighC, other.OffsetHighC) &&
               Near(OffsetLowC, other.OffsetLowC) && Near(OutputMinC, other.OutputMinC) &&
               Near(OutputMaxC, other.OutputMaxC);
    }
}
