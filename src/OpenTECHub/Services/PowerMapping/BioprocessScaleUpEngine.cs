using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>Model-driven bioprocess scale-up (§18.3 step 7). Pure and stateless.</summary>
public interface IBioprocessScaleUpEngine
{
    ScaleUpResult Solve(ScaleUpReference reference, ScaleUpTarget target);
}

/// <summary>
/// Solves the operating point at a new scale from a criterion plus an independent gas rule.
/// </summary>
/// <remarks>
/// <para>
/// A scale criterion is one equation in two unknowns (<c>N</c> and <c>Q_g</c>). The gas rule is the
/// second equation. Without it - or without the target geometry - the calculation is refused rather
/// than quietly resolved by an assumption the operator never made (§18.3 step 7.1).
/// </para>
/// <para>
/// Everything here is an estimate for sizing. It is arithmetic on a correlation fitted at one
/// scale, not a validation of the process at another.
/// </para>
/// </remarks>
public sealed class BioprocessScaleUpEngine : IBioprocessScaleUpEngine
{
    /// <summary>Volume jump above which a single step is called out as aggressive.</summary>
    private const double LargeScaleJump = 20.0;

    public ScaleUpResult Solve(ScaleUpReference reference, ScaleUpTarget target)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);

        var refusals = new List<string>();
        var warnings = new List<string>();

        // --- Identifiability: refuse rather than choose a hidden solution -------------------
        if (target.GasRule == ScaleUpGasRule.None)
        {
            refusals.Add(
                "Sem regra de gás: um critério de escala sozinho fixa uma equação para duas incógnitas " +
                "(N e Q_g). Defina vvm, v_s ou Q_g para tornar a solução única.");
        }
        else if (!double.IsFinite(target.GasRuleValue))
        {
            refusals.Add("O valor da regra de gás precisa ser finito.");
        }
        else if (target.GasRule != ScaleUpGasRule.FixedFlow && target.GasRuleValue <= 0)
        {
            refusals.Add("A regra de gás precisa de um valor positivo.");
        }
        else if (target.GasRule == ScaleUpGasRule.FixedFlow && target.GasRuleValue < 0)
        {
            refusals.Add("A vazão fixa de gás não pode ser negativa.");
        }

        if (!double.IsFinite(target.LiquidVolumeM3) || target.LiquidVolumeM3 <= 0)
        {
            refusals.Add("Volume útil do reator alvo não informado.");
        }

        if (!double.IsFinite(target.VesselDiameterM) || target.VesselDiameterM <= 0)
        {
            refusals.Add("Diâmetro do vaso alvo (T) não informado.");
        }

        if (!double.IsFinite(target.ImpellerDiameterM) || target.ImpellerDiameterM <= 0)
        {
            refusals.Add("Diâmetro do impelidor alvo (D) não informado.");
        }

        if (!double.IsFinite(reference.LiquidVolumeM3) || reference.LiquidVolumeM3 <= 0 ||
            !double.IsFinite(reference.ImpellerDiameterM) || reference.ImpellerDiameterM <= 0 ||
            !double.IsFinite(reference.VesselDiameterM) || reference.VesselDiameterM <= 0)
        {
            refusals.Add("A escala de referência está incompleta (volume útil, D ou T ausentes).");
        }

        if (!double.IsFinite(reference.AgitationRpm) || reference.AgitationRpm <= 0)
        {
            refusals.Add("A escala de referência precisa de um ponto de operação com N > 0.");
        }

        if ((!double.IsFinite(reference.TurbulentPowerNumber) || reference.TurbulentPowerNumber <= 0) &&
            target.Criterion != ScaleUpCriterion.ConstantTipSpeed)
        {
            refusals.Add(
                "Sem número de potência de platô (Np) medido não há como converter P/V em rotação. " +
                "Ajuste o platô turbulento no ensaio de referência.");
        }

        if (target.Criterion == ScaleUpCriterion.ConstantKla)
        {
            if (reference.Correlation is not { HasFit: true } correlation ||
                !double.IsFinite(correlation.K) || correlation.K <= 0 ||
                !double.IsFinite(correlation.Alpha) || !double.IsFinite(correlation.Beta))
            {
                refusals.Add(
                    "O critério de kLa constante exige a correlação van 't Riet ajustada " +
                    "(vincule um mapa de kLa e ajuste o modelo antes).");
            }
            else if (Math.Abs(correlation.Alpha) < 1e-12)
            {
                refusals.Add("A correlação ajustada tem α = 0: P/V não é invertível para um kLa alvo.");
            }
        }

        if (!double.IsFinite(reference.DensityKgM3) || reference.DensityKgM3 <= 0)
        {
            refusals.Add("A densidade do fluido de referência precisa ser positiva e finita.");
        }

        if (!double.IsFinite(reference.GasFlowLpm) || reference.GasFlowLpm < 0)
        {
            refusals.Add("A vazão de gás da referência precisa ser não negativa e finita.");
        }

        if (!double.IsFinite(target.MinRpm) || !double.IsFinite(target.MaxRpm) || target.MaxRpm <= target.MinRpm)
        {
            refusals.Add("A faixa de rotação admissível do alvo é vazia (N máx ≤ N mín).");
        }

        if (refusals.Count > 0)
        {
            return new ScaleUpResult
            {
                IsSolved = false,
                Refusals = refusals,
                Criterion = target.Criterion,
                GasRule = target.GasRule,
            };
        }

        // --- Reference quantities -----------------------------------------------------------
        var referenceRps = PowerCalc.RevPerSecond(reference.AgitationRpm);
        var referenceShaftPower = reference.TurbulentPowerNumber *
                                  reference.DensityKgM3 *
                                  Math.Pow(referenceRps, 3) *
                                  Math.Pow(reference.ImpellerDiameterM, 5);
        var referenceVolumetricPower = referenceShaftPower / reference.LiquidVolumeM3;
        var referenceSuperficial = PowerCalc.GasSuperficialVelocity(reference.GasFlowLpm, reference.VesselDiameterM);

        double? referenceKla = null;
        if (reference.Correlation is { HasFit: true, K: > 0 } refCorrelation &&
            referenceVolumetricPower > 0 && referenceSuperficial > 0)
        {
            referenceKla = refCorrelation.K *
                           Math.Pow(referenceVolumetricPower, refCorrelation.Alpha) *
                           Math.Pow(referenceSuperficial, refCorrelation.Beta);
        }

        // --- Gas rule fixes Q_g at the target ------------------------------------------------
        var targetArea = Math.PI / 4.0 * target.VesselDiameterM * target.VesselDiameterM;
        var targetFlowLpm = target.GasRule switch
        {
            ScaleUpGasRule.ConstantVvm => target.GasRuleValue * target.LiquidVolumeM3 * 1000.0,
            ScaleUpGasRule.ConstantSuperficialVelocity => target.GasRuleValue * targetArea * 60000.0,
            _ => target.GasRuleValue,
        };

        var targetSuperficial = PowerCalc.GasSuperficialVelocity(targetFlowLpm, target.VesselDiameterM);

        // --- Criterion fixes N at the target -------------------------------------------------
        double targetRps;
        double requiredVolumetricPower;

        switch (target.Criterion)
        {
            case ScaleUpCriterion.ConstantTipSpeed:
            {
                // π·N·D constant, so N scales as the diameter ratio.
                targetRps = referenceRps * (reference.ImpellerDiameterM / target.ImpellerDiameterM);
                requiredVolumetricPower = VolumetricPowerAt(targetRps, reference, target);
                break;
            }

            case ScaleUpCriterion.ConstantKla:
            {
                var correlation = reference.Correlation!;
                if (targetSuperficial <= 0)
                {
                    return Refuse(
                        "O critério de kLa constante precisa de v_s > 0 no alvo: com gás nulo a correlação " +
                        "van 't Riet não é invertível.",
                        target);
                }

                var targetKla = referenceKla ?? 0.0;
                if (targetKla <= 0)
                {
                    return Refuse(
                        "Não foi possível avaliar o kLa da escala de referência (P/V ou v_s nulos), " +
                        "então não há alvo de kLa a manter.",
                        target);
                }

                requiredVolumetricPower = PowerCalc.ScaleUpRequiredVolumetricPower(
                    targetKla, targetSuperficial, correlation.K, correlation.Alpha, correlation.Beta);

                if (!double.IsFinite(requiredVolumetricPower) || requiredVolumetricPower <= 0)
                {
                    return Refuse(
                        "A inversão da correlação não produziu um P/V positivo para o kLa alvo.",
                        target);
                }

                targetRps = RpsForVolumetricPower(requiredVolumetricPower, reference, target);
                break;
            }

            default:
            {
                requiredVolumetricPower = referenceVolumetricPower;
                targetRps = RpsForVolumetricPower(requiredVolumetricPower, reference, target);
                break;
            }
        }

        if (!double.IsFinite(targetRps) || targetRps <= 0)
        {
            return Refuse("A rotação resultante não é um número físico. Verifique geometria e Np.", target);
        }

        var targetRpm = targetRps * 60.0;

        // Recompute the power actually delivered at the solved rotation, so every reported
        // quantity comes from the same operating point.
        var shaftPower = reference.TurbulentPowerNumber *
                         reference.DensityKgM3 *
                         Math.Pow(targetRps, 3) *
                         Math.Pow(target.ImpellerDiameterM, 5);
        var volumetricPower = shaftPower / target.LiquidVolumeM3;
        var torque = targetRps > 0 ? shaftPower / (2.0 * Math.PI * targetRps) : 0.0;
        var tipSpeed = Math.PI * targetRps * target.ImpellerDiameterM;

        var reynolds = PowerCalc.ReynoldsNumber(
            reference.DensityKgM3, targetRpm, target.ImpellerDiameterM, reference.ViscosityPaS);
        var froude = PowerCalc.FroudeNumber(targetRpm, target.ImpellerDiameterM);
        var flowNumber = PowerCalc.AerationNumber(targetFlowLpm, targetRpm, target.ImpellerDiameterM);

        var floodingFlow = PowerCalc.NienowFloodingGasFlowLpm(
            targetRpm, target.ImpellerDiameterM, target.VesselDiameterM);
        var floodingMargin = floodingFlow > 0 ? targetFlowLpm / floodingFlow : double.PositiveInfinity;
        var isFlooded = floodingFlow > 0 && targetFlowLpm > floodingFlow;

        double? predictedKla = null;
        if (reference.Correlation is { HasFit: true, K: > 0 } fitted && volumetricPower > 0 && targetSuperficial > 0)
        {
            predictedKla = fitted.K *
                           Math.Pow(volumetricPower, fitted.Alpha) *
                           Math.Pow(targetSuperficial, fitted.Beta);
        }

        // --- Warnings: the estimate stands, but its footing is stated ------------------------
        if (targetRpm < target.MinRpm || targetRpm > target.MaxRpm)
        {
            warnings.Add(
                $"A rotação necessária ({targetRpm:F0} rpm) está fora da faixa admissível " +
                $"({target.MinRpm:F0}–{target.MaxRpm:F0} rpm) do reator alvo.");
        }

        if (isFlooded)
        {
            warnings.Add(
                $"Afogamento na nova geometria: Q_g = {targetFlowLpm:F2} L/min supera o limite de Nienow " +
                $"({floodingFlow:F2} L/min) a {targetRpm:F0} rpm. O impelidor não dispersaria o gás.");
        }
        else if (floodingMargin > 0.8 && double.IsFinite(floodingMargin))
        {
            warnings.Add(
                $"Perto da fronteira de flooding: Q_g/Q_g,F = {floodingMargin:F2} " +
                $"(limite de Nienow {floodingFlow:F2} L/min a {targetRpm:F0} rpm).");
        }

        if (!double.IsFinite(reynolds) || !double.IsFinite(reference.ViscosityPaS) || reference.ViscosityPaS <= 0)
        {
            warnings.Add(
                "Viscosidade dinâmica de referência não informada ou inválida (μ ≤ 0): o número de Reynolds não pôde ser calculado.");
        }
        else if (reynolds < 10_000)
        {
            warnings.Add(
                $"Re = {reynolds:N0} abaixo de 10⁴: fora do regime turbulento em que Np foi medido como platô, " +
                "então a conversão P/V ↔ N carrega erro.");
        }

        AppendDomainWarning(
            warnings, "P/V", volumetricPower, "W/m³",
            reference.CalibratedMinVolumetricPower, reference.CalibratedMaxVolumetricPower, "F0");

        AppendDomainWarning(
            warnings, "v_s", targetSuperficial, "m/s",
            reference.CalibratedMinSuperficialVelocity, reference.CalibratedMaxSuperficialVelocity, "F5");

        var scaleFactor = target.LiquidVolumeM3 / reference.LiquidVolumeM3;
        if (scaleFactor > LargeScaleJump)
        {
            warnings.Add(
                $"Salto de escala de {scaleFactor:F0}× num único passo. Correlações ajustadas em bancada " +
                "raramente se sustentam por mais de uma ordem de grandeza sem escala intermediária.");
        }

        var referenceDt = reference.ImpellerDiameterM / reference.VesselDiameterM;
        var targetDt = target.ImpellerDiameterM / target.VesselDiameterM;
        if (Math.Abs(targetDt - referenceDt) / Math.Max(referenceDt, 1e-9) > 0.05)
        {
            warnings.Add(
                $"D/T muda de {referenceDt:F3} para {targetDt:F3}: a semelhança geométrica não é mantida, " +
                "e o Np de platô da referência pode não valer no alvo.");
        }

        if (target.Criterion == ScaleUpCriterion.ConstantKla && reference.Correlation is { } used)
        {
            warnings.Add(
                $"kLa alvo resolvido pelo modelo ajustado (R² = {used.R2:F3}, {used.ValidPointsCount} pontos). " +
                "O resultado é dimensionamento, não validação do processo na nova escala.");
        }

        return new ScaleUpResult
        {
            IsSolved = true,
            Refusals = [],
            Warnings = warnings,
            TargetAgitationRpm = targetRpm,
            TargetGasFlowLpm = targetFlowLpm,
            TargetGasFlowVvm = target.LiquidVolumeM3 > 0
                ? PowerCalc.LpmToVvm(targetFlowLpm, target.LiquidVolumeM3)
                : 0.0,
            TargetSuperficialVelocityMs = targetSuperficial,
            ShaftPowerW = shaftPower,
            VolumetricPowerWm3 = volumetricPower,
            TorqueNm = torque,
            TipSpeedMs = tipSpeed,
            ReynoldsNumber = reynolds,
            FroudeNumber = froude,
            GasFlowNumber = flowNumber,
            PredictedKlaPerHour = predictedKla,
            FloodingGasFlowLpm = floodingFlow,
            FloodingMargin = floodingMargin,
            IsFlooded = isFlooded,
            ScaleFactor = scaleFactor,
            ReferenceVolumetricPowerWm3 = referenceVolumetricPower,
            ReferenceSuperficialVelocityMs = referenceSuperficial,
            ReferenceAgitationRpm = reference.AgitationRpm,
            ReferenceGasFlowLpm = reference.GasFlowLpm,
            ReferenceKlaPerHour = referenceKla,
            Criterion = target.Criterion,
            GasRule = target.GasRule,
        };
    }

    /// <summary>N [rev/s] that delivers a given P/V in the target geometry, from P = Np·ρ·N³·D⁵.</summary>
    private static double RpsForVolumetricPower(
        double volumetricPower,
        ScaleUpReference reference,
        ScaleUpTarget target)
    {
        var denominator = reference.TurbulentPowerNumber *
                          reference.DensityKgM3 *
                          Math.Pow(target.ImpellerDiameterM, 5);

        if (denominator <= 0)
        {
            return double.NaN;
        }

        return Math.Cbrt(volumetricPower * target.LiquidVolumeM3 / denominator);
    }

    private static double VolumetricPowerAt(double rps, ScaleUpReference reference, ScaleUpTarget target)
    {
        var power = reference.TurbulentPowerNumber *
                    reference.DensityKgM3 *
                    Math.Pow(rps, 3) *
                    Math.Pow(target.ImpellerDiameterM, 5);

        return target.LiquidVolumeM3 > 0 ? power / target.LiquidVolumeM3 : double.NaN;
    }

    private static void AppendDomainWarning(
        List<string> warnings,
        string symbol,
        double value,
        string unit,
        double? calibratedMin,
        double? calibratedMax,
        string format)
    {
        if (calibratedMin is not { } min || calibratedMax is not { } max || max <= min)
        {
            return;
        }

        if (value < min || value > max)
        {
            warnings.Add(
                $"{symbol} = {value.ToString(format, CultureInfo.CurrentCulture)} {unit} está fora do domínio " +
                $"calibrado ({min.ToString(format, CultureInfo.CurrentCulture)}–" +
                $"{max.ToString(format, CultureInfo.CurrentCulture)} {unit}). É extrapolação.");
        }
    }

    private static ScaleUpResult Refuse(string reason, ScaleUpTarget target) => new()
    {
        IsSolved = false,
        Refusals = [reason],
        Criterion = target.Criterion,
        GasRule = target.GasRule,
    };

    /// <summary>Technical sizing sheet in CSV (§18.3 step 7.2).</summary>
    public static string BuildSummaryCsv(
        ScaleUpReference reference,
        ScaleUpTarget target,
        ScaleUpResult result)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(result);

        var invariant = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.AppendLine("# Folha de dimensionamento de bioprocesso - OpenTEC-Hub");
        builder.AppendLine(invariant, $"# Gerado em;{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(invariant, $"# Criterio de escala;{result.Criterion}");
        builder.AppendLine(invariant, $"# Regra de gas;{result.GasRule}");
        builder.AppendLine("# AVISO;Resultado de dimensionamento; nao e validacao do processo na nova escala.");
        builder.AppendLine();

        builder.AppendLine("secao;grandeza;referencia;alvo;unidade");
        builder.AppendLine(invariant,
            $"geometria;volume util;{reference.LiquidVolumeM3 * 1000:F3};{target.LiquidVolumeM3 * 1000:F3};L");
        builder.AppendLine(invariant,
            $"geometria;diametro do vaso T;{reference.VesselDiameterM:F4};{target.VesselDiameterM:F4};m");
        builder.AppendLine(invariant,
            $"geometria;diametro do impelidor D;{reference.ImpellerDiameterM:F4};{target.ImpellerDiameterM:F4};m");
        var refDt = reference.VesselDiameterM > 0
            ? (reference.ImpellerDiameterM / reference.VesselDiameterM).ToString("F4", invariant)
            : "-";
        var targetDt = target.VesselDiameterM > 0
            ? (target.ImpellerDiameterM / target.VesselDiameterM).ToString("F4", invariant)
            : "-";
        builder.AppendLine(invariant, $"geometria;razao D/T;{refDt};{targetDt};-");

        if (!result.IsSolved)
        {
            builder.AppendLine();
            builder.AppendLine("secao;recusa");
            foreach (var refusal in result.Refusals)
            {
                builder.AppendLine(invariant, $"recusa;{Escape(refusal)}");
            }

            return builder.ToString();
        }

        builder.AppendLine(invariant,
            $"operacao;rotacao N;{result.ReferenceAgitationRpm:F1};{result.TargetAgitationRpm:F1};rpm");
        builder.AppendLine(invariant,
            $"operacao;vazao de gas Qg;{result.ReferenceGasFlowLpm:F3};{result.TargetGasFlowLpm:F3};L/min");
        builder.AppendLine(invariant,
            $"operacao;vazao de gas;{Vvm(reference)};{result.TargetGasFlowVvm:F4};vvm");
        builder.AppendLine(invariant,
            $"operacao;velocidade superficial v_s;{result.ReferenceSuperficialVelocityMs:F6};" +
            $"{result.TargetSuperficialVelocityMs:F6};m/s");
        builder.AppendLine(invariant,
            $"potencia;potencia de eixo P;;{result.ShaftPowerW:F4};W");
        builder.AppendLine(invariant,
            $"potencia;potencia especifica P/V;{result.ReferenceVolumetricPowerWm3:F2};" +
            $"{result.VolumetricPowerWm3:F2};W/m3");
        builder.AppendLine(invariant, $"potencia;torque esperado;;{result.TorqueNm:F5};N.m");
        builder.AppendLine(invariant, $"cisalhamento;velocidade periferica;;{result.TipSpeedMs:F4};m/s");
        var reynoldsStr = double.IsFinite(result.ReynoldsNumber)
            ? result.ReynoldsNumber.ToString("F0", invariant)
            : "-";
        builder.AppendLine(invariant, $"adimensional;Reynolds;;{reynoldsStr};-");
        var froudeStr = double.IsFinite(result.FroudeNumber)
            ? result.FroudeNumber.ToString("F5", invariant)
            : "-";
        builder.AppendLine(invariant, $"adimensional;Froude;;{froudeStr};-");
        var flGStr = double.IsFinite(result.GasFlowNumber)
            ? result.GasFlowNumber.ToString("F6", invariant)
            : "-";
        builder.AppendLine(invariant, $"adimensional;numero de aeracao FlG;;{flGStr};-");
        builder.AppendLine(invariant,
            $"transferencia;kLa;{Format(result.ReferenceKlaPerHour)};{Format(result.PredictedKlaPerHour)};1/h");
        builder.AppendLine(invariant,
            $"flooding;Qg limite de Nienow;;{result.FloodingGasFlowLpm:F3};L/min");
        builder.AppendLine(invariant,
            $"flooding;margem Qg/Qg_F;;{result.FloodingMargin:F3};-");
        builder.AppendLine(invariant,
            $"flooding;situacao;;{(result.IsFlooded ? "AFOGADO" : "disperso")};-");
        builder.AppendLine(invariant, $"escala;fator de volume;;{result.ScaleFactor:F2};x");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("secao;aviso");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine(invariant, $"aviso;{Escape(warning)}");
            }
        }

        return builder.ToString();
    }

    private static string Vvm(ScaleUpReference reference)
        => reference.LiquidVolumeM3 > 0
            ? PowerCalc.LpmToVvm(reference.GasFlowLpm, reference.LiquidVolumeM3).ToString("F4", CultureInfo.InvariantCulture)
            : "";

    private static string Format(double? value)
        => value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("F3", CultureInfo.InvariantCulture)
            : "";

    private static string Escape(string value)
        => value.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
}
