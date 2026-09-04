using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

public sealed record ScaleUpCriterionOption(ScaleUpCriterion Criterion, string DisplayText, string Explanation);

public sealed record ScaleUpGasRuleOption(ScaleUpGasRule Rule, string DisplayText, string Unit);

/// <summary>One line of the sizing sheet shown on screen.</summary>
public sealed record ScaleUpSheetRow(string Quantity, string Reference, string Target, string Unit);

/// <summary>
/// Bioprocess scale-up calculator (§18.3 step 7): resolves the operating point at a new scale from
/// a criterion plus an independent gas rule, and refuses when the problem is under-determined.
/// </summary>
public sealed partial class BioprocessScaleUpViewModel : ObservableObject
{
    private readonly IBioprocessScaleUpEngine _engine;

    private ScaleUpReference _reference = new();

    public BioprocessScaleUpViewModel(IBioprocessScaleUpEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public IReadOnlyList<ScaleUpCriterionOption> AvailableCriteria { get; } =
    [
        new(ScaleUpCriterion.ConstantVolumetricPower, "P/V constante",
            "Mantém a densidade de potência volumétrica; o critério de partida mais comum."),
        new(ScaleUpCriterion.ConstantKla, "kLa constante",
            "Mantém a capacidade volumétrica de oxigenação, resolvida pelo modelo van 't Riet calibrado."),
        new(ScaleUpCriterion.ConstantTipSpeed, "Velocidade periférica constante",
            "Mantém π·N·D; usado em culturas sensíveis a cisalhamento."),
    ];

    public IReadOnlyList<ScaleUpGasRuleOption> AvailableGasRules { get; } =
    [
        new(ScaleUpGasRule.None, "— selecione —", ""),
        new(ScaleUpGasRule.ConstantVvm, "vvm constante", "vvm"),
        new(ScaleUpGasRule.ConstantSuperficialVelocity, "v_s constante", "m/s"),
        new(ScaleUpGasRule.FixedFlow, "Q_g fixo", "L/min"),
    ];

    public ObservableCollection<ScaleUpSheetRow> SheetRows { get; } = [];

    public ObservableCollection<string> Refusals { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    // --- Reference scale, filled from the active map ---------------------------------------
    [ObservableProperty]
    public partial string ReferenceSummary { get; set; } = "Nenhuma escala de referência carregada.";

    [ObservableProperty]
    public partial bool HasReference { get; set; }

    // --- Target inputs ----------------------------------------------------------------------
    [ObservableProperty]
    public partial double TargetVolumeL { get; set; } = 20.0;

    [ObservableProperty]
    public partial double TargetVesselDiameterMm { get; set; } = 300.0;

    [ObservableProperty]
    public partial double TargetImpellerDiameterMm { get; set; } = 100.0;

    [ObservableProperty]
    public partial double TargetMinRpm { get; set; } = 20.0;

    [ObservableProperty]
    public partial double TargetMaxRpm { get; set; } = 800.0;

    [ObservableProperty]
    public partial ScaleUpCriterion SelectedCriterion { get; set; } = ScaleUpCriterion.ConstantVolumetricPower;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GasRuleUnit))]
    public partial ScaleUpGasRule SelectedGasRule { get; set; } = ScaleUpGasRule.None;

    [ObservableProperty]
    public partial double GasRuleValue { get; set; } = 0.5;

    public string GasRuleUnit =>
        AvailableGasRules.FirstOrDefault(r => r.Rule == SelectedGasRule)?.Unit ?? "";

    public string CriterionExplanation =>
        AvailableCriteria.FirstOrDefault(c => c.Criterion == SelectedCriterion)?.Explanation ?? "";

    // --- Result --------------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasRefusals))]
    public partial ScaleUpResult? Result { get; set; }

    public bool HasResult => Result is { IsSolved: true };

    public bool HasRefusals => Result is { IsSolved: false } && Refusals.Count > 0;

    [ObservableProperty]
    public partial bool HasWarnings { get; set; }

    [ObservableProperty]
    public partial bool IsFloodedAtTarget { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        "Carregue a escala de referência do mapa e defina o alvo.";

    partial void OnSelectedCriterionChanged(ScaleUpCriterion value)
        => OnPropertyChanged(nameof(CriterionExplanation));

    /// <summary>
    /// Adopts a calibrated scale: the geometry and fluid of the map, the plateau Np measured on it,
    /// an anchor operating point, and the domain the correlation was fitted over.
    /// </summary>
    public void SetReference(ScaleUpReference reference, string summary)
    {
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        ReferenceSummary = summary;
        HasReference = reference.LiquidVolumeM3 > 0 && reference.AgitationRpm > 0;

        if (!HasReference)
        {
            StatusMessage = "A escala de referência está incompleta: reconstrua a superfície do mapa primeiro.";
        }
    }

    public ScaleUpReference CurrentReference => _reference;

    public ScaleUpTarget BuildTarget() => new()
    {
        LiquidVolumeM3 = TargetVolumeL / 1000.0,
        VesselDiameterM = TargetVesselDiameterMm / 1000.0,
        ImpellerDiameterM = TargetImpellerDiameterMm / 1000.0,
        MinRpm = TargetMinRpm,
        MaxRpm = TargetMaxRpm,
        Criterion = SelectedCriterion,
        GasRule = SelectedGasRule,
        GasRuleValue = GasRuleValue,
    };

    [RelayCommand]
    public void Calculate()
    {
        var target = BuildTarget();
        var result = _engine.Solve(_reference, target);

        Result = result;

        Refusals.Clear();
        foreach (var refusal in result.Refusals)
        {
            Refusals.Add(refusal);
        }

        Warnings.Clear();
        foreach (var warning in result.Warnings)
        {
            Warnings.Add(warning);
        }

        HasWarnings = Warnings.Count > 0;
        IsFloodedAtTarget = result.IsFlooded;
        OnPropertyChanged(nameof(HasRefusals));

        BuildSheet(result);

        StatusMessage = result.IsSolved
            ? $"Solução: {result.TargetAgitationRpm:F0} rpm e {result.TargetGasFlowLpm:F2} L/min " +
              $"({result.ScaleFactor:F1}× o volume da referência)."
            : $"Cálculo recusado: {result.Refusals.Count} condição(ões) não atendida(s).";
    }

    private void BuildSheet(ScaleUpResult result)
    {
        SheetRows.Clear();
        if (!result.IsSolved)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;

        SheetRows.Add(new ScaleUpSheetRow(
            "Volume útil",
            (_reference.LiquidVolumeM3 * 1000).ToString("F2", culture),
            TargetVolumeL.ToString("F2", culture),
            "L"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Diâmetro do impelidor D",
            (_reference.ImpellerDiameterM * 1000).ToString("F1", culture),
            TargetImpellerDiameterMm.ToString("F1", culture),
            "mm"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Razão D/T",
            (_reference.ImpellerDiameterM / Math.Max(_reference.VesselDiameterM, 1e-9)).ToString("F3", culture),
            (TargetImpellerDiameterMm / Math.Max(TargetVesselDiameterMm, 1e-9)).ToString("F3", culture),
            "–"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Rotação N",
            result.ReferenceAgitationRpm.ToString("F0", culture),
            result.TargetAgitationRpm.ToString("F0", culture),
            "rpm"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Vazão de gás Q_g",
            result.ReferenceGasFlowLpm.ToString("F2", culture),
            result.TargetGasFlowLpm.ToString("F2", culture),
            "L/min"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Vazão de gás",
            "—",
            result.TargetGasFlowVvm.ToString("F3", culture),
            "vvm"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Velocidade superficial v_s",
            result.ReferenceSuperficialVelocityMs.ToString("F5", culture),
            result.TargetSuperficialVelocityMs.ToString("F5", culture),
            "m/s"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Potência específica P/V",
            result.ReferenceVolumetricPowerWm3.ToString("F1", culture),
            result.VolumetricPowerWm3.ToString("F1", culture),
            "W/m³"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Potência de eixo P",
            "—",
            result.ShaftPowerW.ToString("F3", culture),
            "W"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Torque esperado",
            "—",
            result.TorqueNm.ToString("F4", culture),
            "N·m"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Velocidade periférica",
            "—",
            result.TipSpeedMs.ToString("F3", culture),
            "m/s"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Reynolds",
            "—",
            double.IsFinite(result.ReynoldsNumber) ? result.ReynoldsNumber.ToString("N0", culture) : "—",
            "–"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Froude",
            "—",
            double.IsFinite(result.FroudeNumber) ? result.FroudeNumber.ToString("F4", culture) : "—",
            "–"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Número de aeração Fl_G",
            "—",
            double.IsFinite(result.GasFlowNumber) ? result.GasFlowNumber.ToString("F5", culture) : "—",
            "–"));

        SheetRows.Add(new ScaleUpSheetRow(
            "kLa",
            result.ReferenceKlaPerHour?.ToString("F2", culture) ?? "—",
            result.PredictedKlaPerHour?.ToString("F2", culture) ?? "—",
            "1/h"));

        SheetRows.Add(new ScaleUpSheetRow(
            "Margem de flooding Q_g/Q_g,F",
            "—",
            double.IsFinite(result.FloodingMargin) ? result.FloodingMargin.ToString("F3", culture) : "—",
            result.IsFlooded ? "AFOGADO" : "disperso"));
    }

    public string BuildSummaryCsv()
        => BioprocessScaleUpEngine.BuildSummaryCsv(_reference, BuildTarget(), Result ?? _engine.Solve(_reference, BuildTarget()));

    public string SuggestedCsvFileName
        => $"dimensionamento-{TargetVolumeL:F0}L-{DateTime.Now:yyyyMMdd-HHmm}.csv";
}
