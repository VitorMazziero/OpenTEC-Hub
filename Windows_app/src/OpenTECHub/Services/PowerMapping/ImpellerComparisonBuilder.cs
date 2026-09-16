using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Turns finished power assays into side-by-side benchmarking items (§18.3 step 6).
/// </summary>
/// <remarks>
/// Pure and stateless, so a comparison can be rebuilt from stored documents without hardware.
/// Assays from different rigs may still be compared - the operator often wants exactly that - but
/// the difference is named in <see cref="CheckCompatibility"/> and never silently averaged away.
/// </remarks>
public static class ImpellerComparisonBuilder
{
    /// <summary>Reynolds number above which the power number is treated as the turbulent plateau (§4.3).</summary>
    public const double TurbulentReynoldsCutoff = 10_000;

    /// <summary>Relative tolerance under which two rigs count as the same geometry.</summary>
    private const double GeometryTolerance = 0.02;

    /// <summary>Builds one benchmarking row from a stored assay.</summary>
    public static ImpellerComparisonItem BuildItem(PowerTestDocument document, IPowerAnalysisEngine engine)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(engine);

        var geometry = document.Geometry;
        var impeller = geometry.Impellers.Count > 0
            ? geometry.Impellers[0]
            : new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 };

        var accepted = document.Runs.Where(r => r.Phase == PowerRunPhase.Accepted).ToList();

        var ungassed = accepted
            .Where(r => r.GasMode == PowerGasMode.Ungassed && r.Analysis is not null)
            .Select(r => r.Analysis!)
            .Where(a => double.IsFinite(a.AssemblyPowerNumber) && double.IsFinite(a.AssemblyReynoldsNumber))
            .ToList();

        var plateau = engine.FitPlateau(
            ungassed.Select(a => (a.AssemblyReynoldsNumber, a.AssemblyPowerNumber, a.AssemblyPowerNumberCi95)),
            TurbulentReynoldsCutoff);

        var npReCurve = ungassed
            .OrderBy(a => a.AssemblyReynoldsNumber)
            .Select(a => new NpRePoint(a.AssemblyReynoldsNumber, a.AssemblyPowerNumber, a.AssemblyPowerNumberCi95))
            .ToList();

        var ratioCurve = accepted
            .Where(r => r.GasFlowNumber.HasValue && r.PowerRatio.HasValue &&
                        double.IsFinite(r.GasFlowNumber.Value) && double.IsFinite(r.PowerRatio.Value))
            .OrderBy(r => r.GasFlowNumber!.Value)
            .Select(r => new PowerRatioPoint(
                r.GasFlowNumber!.Value,
                r.PowerRatio!.Value,
                r.AgitationRpm,
                r.GasFlowLpm ?? 0.0))
            .ToList();

        double? averageSpecificPower = null;
        if (geometry.LiquidVolumeM3 > 0)
        {
            var specific = accepted
                .Where(r => r.NetPowerW.HasValue && double.IsFinite(r.NetPowerW.Value))
                .Select(r => r.NetPowerW!.Value / geometry.LiquidVolumeM3)
                .ToList();

            if (specific.Count > 0)
            {
                averageSpecificPower = specific.Average();
            }
        }

        // Parasitic drag: the tare the assay actually subtracted, averaged over its accepted points.
        double? parasitic = null;
        var voidPowers = accepted
            .Where(r => r.Analysis is not null && double.IsFinite(r.Analysis.VoidPowerW))
            .Select(r => r.Analysis!.VoidPowerW)
            .ToList();
        if (voidPowers.Count > 0)
        {
            parasitic = voidPowers.Average();
        }

        return new ImpellerComparisonItem
        {
            SourceTestId = document.TestId,
            TestName = document.Name,
            TestDateUtc = document.CompletedUtc ?? document.CreatedUtc,
            ImpellerType = impeller.Type,
            ImpellerName = impeller.Label,
            ImpellerDiameterM = impeller.DiameterM,
            VesselDiameterM = geometry.VesselDiameterM,
            LiquidVolumeM3 = geometry.LiquidVolumeM3,
            LiquidDensityKgM3 = document.Fluid.DensityKgM3,
            TurbulentNpMean = plateau.HasFit ? plateau.PowerNumber : 0.0,
            TurbulentNpCi95 = plateau.HasFit ? plateau.PowerNumberCi95 : 0.0,
            AverageSpecificPowerWm3 = averageSpecificPower,
            ParasiticPowerZeroSpeedW = parasitic,
            PowerRatioCurve = ratioCurve,
            PowerNumberReynoldsCurve = npReCurve,
        };
    }

    /// <summary>
    /// Compares the rigs behind a set of items. Returns whether they are equivalent and, when they
    /// are not, exactly what differs - so a mixed comparison is labelled rather than refused.
    /// </summary>
    public static (bool IsCompatible, IReadOnlyList<string> Notes) CheckCompatibility(
        IReadOnlyList<ImpellerComparisonItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var notes = new List<string>();
        if (items.Count < 2)
        {
            return (true, notes);
        }

        var reference = items[0];

        for (var index = 1; index < items.Count; index++)
        {
            var item = items[index];

            if (!Close(reference.VesselDiameterM, item.VesselDiameterM))
            {
                notes.Add(
                    $"'{item.TestName}': diâmetro do vaso T = {item.VesselDiameterM * 1000:F0} mm difere de " +
                    $"'{reference.TestName}' ({reference.VesselDiameterM * 1000:F0} mm).");
            }

            if (!Close(reference.LiquidVolumeM3, item.LiquidVolumeM3))
            {
                notes.Add(
                    $"'{item.TestName}': volume útil = {item.LiquidVolumeM3 * 1000:F2} L difere de " +
                    $"'{reference.TestName}' ({reference.LiquidVolumeM3 * 1000:F2} L). P/V não é comparável ponto a ponto.");
            }

            if (!Close(reference.LiquidDensityKgM3, item.LiquidDensityKgM3))
            {
                notes.Add(
                    $"'{item.TestName}': densidade ρ = {item.LiquidDensityKgM3:F0} kg/m³ difere de " +
                    $"'{reference.TestName}' ({reference.LiquidDensityKgM3:F0} kg/m³). Np e Re mudam de base.");
            }
        }

        return (notes.Count == 0, notes);
    }

    /// <summary>
    /// Adds the baffling and fluid checks that need the source documents, on top of the geometry
    /// checks that the items alone can answer.
    /// </summary>
    public static (bool IsCompatible, IReadOnlyList<string> Notes) CheckCompatibility(
        IReadOnlyList<ImpellerComparisonItem> items,
        IReadOnlyList<PowerTestDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var (compatible, notes) = CheckCompatibility(items);
        var combined = notes.ToList();

        if (documents.Count >= 2)
        {
            var reference = documents[0];
            foreach (var document in documents.Skip(1))
            {
                if (reference.Geometry.Baffled != document.Geometry.Baffled)
                {
                    combined.Add(
                        $"'{document.Name}': {(document.Geometry.Baffled ? "com" : "sem")} chicanas, " +
                        $"contra '{reference.Name}' {(reference.Geometry.Baffled ? "com" : "sem")} chicanas. " +
                        "O regime de escoamento é outro.");
                }

                if (!Close(reference.Fluid.ViscosityPaS, document.Fluid.ViscosityPaS))
                {
                    combined.Add(
                        $"'{document.Name}': viscosidade μ = {document.Fluid.ViscosityPaS:G3} Pa·s difere de " +
                        $"'{reference.Name}' ({reference.Fluid.ViscosityPaS:G3} Pa·s).");
                }

                if (reference.Geometry.Impellers.Count != document.Geometry.Impellers.Count)
                {
                    combined.Add(
                        $"'{document.Name}': {document.Geometry.Impellers.Count} impelidor(es) no eixo, " +
                        $"contra {reference.Geometry.Impellers.Count} em '{reference.Name}'.");
                }

                if (document.RelativeMode != reference.RelativeMode)
                {
                    combined.Add(
                        $"'{document.Name}': modo {(document.RelativeMode ? "relativo" : "absoluto")} " +
                        $"contra '{reference.Name}' {(reference.RelativeMode ? "relativo" : "absoluto")}. " +
                        "Np sem calibração de torque não é absoluto (§9).");
                }
            }
        }

        return (compatible && combined.Count == notes.Count && combined.Count == 0, combined);
    }

    /// <summary>
    /// Unified CSV with the benchmarking table first, then the raw series that back it (§18.3 step 6.3).
    /// </summary>
    public static string BuildCsv(
        IReadOnlyList<ImpellerComparisonItem> items,
        IReadOnlyList<string>? compatibilityNotes = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var invariant = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.AppendLine("# Comparacao de impelidores - OpenTEC-Hub");
        builder.AppendLine(invariant, $"# Gerado em;{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(invariant, $"# Corte turbulento de Reynolds;{TurbulentReynoldsCutoff}");

        if (compatibilityNotes is { Count: > 0 })
        {
            builder.AppendLine("# ATENCAO - montagens nao equivalentes:");
            foreach (var note in compatibilityNotes)
            {
                builder.AppendLine(invariant, $"# ;{Escape(note)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            "tabela;ensaio;tipo;D_m;D_T;Np_platou;Np_IC95;P_V_medio_W_m3;P_vazio_W;data_utc");

        foreach (var item in items)
        {
            builder.AppendLine(invariant,
                $"benchmark;{Escape(item.TestName)};{item.ImpellerType};{item.ImpellerDiameterM:F4};" +
                $"{item.DiameterRatioDt:F4};{item.TurbulentNpMean:F4};{item.TurbulentNpCi95:F4};" +
                $"{Format(item.AverageSpecificPowerWm3)};{Format(item.ParasiticPowerZeroSpeedW)};{item.TestDateUtc:O}");
        }

        builder.AppendLine();
        builder.AppendLine("tabela;ensaio;reynolds;numero_de_potencia;Np_IC95");
        foreach (var item in items)
        {
            foreach (var point in item.PowerNumberReynoldsCurve)
            {
                builder.AppendLine(invariant,
                    $"np_re;{Escape(item.TestName)};{point.Reynolds:F1};{point.PowerNumber:F5};{point.Uncertainty95:F5}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("tabela;ensaio;numero_de_aeracao_FlG;razao_de_potencia_PG_P0;rotacao_rpm;vazao_L_min");
        foreach (var item in items)
        {
            foreach (var point in item.PowerRatioCurve)
            {
                builder.AppendLine(invariant,
                    $"pg_p0;{Escape(item.TestName)};{point.GasFlowNumber:F6};{point.PowerRatio:F5};" +
                    $"{point.AgitationRpm:F1};{point.GasFlowLpm:F3}");
            }
        }

        return builder.ToString();
    }

    private static string Format(double? value)
        => value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("F5", CultureInfo.InvariantCulture)
            : "";

    private static string Escape(string value)
        => value.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

    private static bool Close(double a, double b)
    {
        if (a <= 0 || b <= 0)
        {
            return Math.Abs(a - b) < 1e-9;
        }

        return Math.Abs(a - b) / Math.Max(a, b) <= GeometryTolerance;
    }
}
