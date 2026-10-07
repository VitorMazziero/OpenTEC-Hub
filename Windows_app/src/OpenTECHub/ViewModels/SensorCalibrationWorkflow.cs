using System.Globalization;

namespace OpenTECHub.ViewModels;

public enum SensorCalibrationPhase
{
    Setup, AwaitingFirst, StabilizingFirst, AveragingFirst,
    AwaitingSecond, StabilizingSecond, AveragingSecond, Review, Saved, Failed,
}

public sealed record CalibrationStep(string Title, string State, bool IsActive, bool IsComplete);

/// <summary>Shared presentation of the pH and oxygen acquisition sequence.</summary>
public sealed record SensorCalibrationWorkflow
{
    public required CalibrationStep[] Steps { get; init; }
    public required string Title { get; init; }
    public required string ActionText { get; init; }
    public required string ReferenceLabel { get; init; }
    public required string CalibratedLabel { get; init; }
    public required string Point1Reference { get; init; }
    public required string Point2Reference { get; init; }
    public required string Point1State { get; init; }
    public required string Point2State { get; init; }
    public required SensorCalibrationPhase Phase { get; init; }
    public bool ShowProposal => Phase == SensorCalibrationPhase.Review;
    public bool IsSaved => Phase == SensorCalibrationPhase.Saved;
    public bool IsPoint1Active => Steps[1].IsActive;
    public bool IsPoint2Active => Steps[2].IsActive;
    public bool CanEditSetup => Phase is SensorCalibrationPhase.Setup or SensorCalibrationPhase.Failed or SensorCalibrationPhase.Saved;

    public static SensorCalibrationWorkflow Create(
        SensorCalibrationPhase phase, bool twoPoint, bool ph, string reference1, string reference2)
    {
        var active = phase switch
        {
            SensorCalibrationPhase.AwaitingFirst or SensorCalibrationPhase.StabilizingFirst or SensorCalibrationPhase.AveragingFirst => 1,
            SensorCalibrationPhase.AwaitingSecond or SensorCalibrationPhase.StabilizingSecond or SensorCalibrationPhase.AveragingSecond => 2,
            SensorCalibrationPhase.Review or SensorCalibrationPhase.Saved => 3,
            _ => 0,
        };
        var standard = ph ? "Tampão" : "Padrão";
        var unit = ph ? "pH" : "% O₂";
        var first = FormatReference(reference1, ph);
        var second = FormatReference(reference2, ph);
        var titles = new[] { "1 · Preparar", "2 · Ponto 1", "3 · Ponto 2", "4 · Salvar" };
        var steps = titles.Select((title, index) =>
        {
            var skipped = index == 2 && !twoPoint;
            var complete = !skipped && (index < active || phase == SensorCalibrationPhase.Saved);
            return new CalibrationStep(title,
                skipped ? "Não usado" : complete ? "Concluído" : index == active ? "Atual" : "A seguir",
                !skipped && index == active, complete);
        }).ToArray();
        return new SensorCalibrationWorkflow
        {
            Phase = phase,
            Steps = steps,
            ReferenceLabel = $"Referência ({unit})",
            CalibratedLabel = ph ? "pH calibrado" : "O₂ calibrado (%)",
            Point1Reference = ph ? $"pH {first}" : $"{first}% O₂",
            Point2Reference = ph ? $"pH {second}" : $"{second}% O₂",
            Point1State = active > 1 ? "✓ Adquirido" : phase == SensorCalibrationPhase.AwaitingFirst ? "Aguardando preparo" : active == 1 ? "Adquirindo" : "A adquirir",
            Point2State = !twoPoint ? "Não usado" : active > 2 ? "✓ Adquirido" : phase == SensorCalibrationPhase.AwaitingSecond ? "Aguardando troca" : active == 2 ? "Adquirindo" : "A adquirir",
            Title = phase switch
            {
                SensorCalibrationPhase.AwaitingFirst => ph ? $"Coloque a sonda no tampão pH {first}" : $"Coloque a sonda no padrão {first}% O₂",
                SensorCalibrationPhase.AwaitingSecond => ph ? $"Troque para o tampão pH {second}" : $"Troque para o padrão {second}% O₂",
                SensorCalibrationPhase.StabilizingFirst => "Ponto 1 · estabilizando",
                SensorCalibrationPhase.AveragingFirst => "Ponto 1 · coletando a média",
                SensorCalibrationPhase.StabilizingSecond => "Ponto 2 · estabilizando",
                SensorCalibrationPhase.AveragingSecond => "Ponto 2 · coletando a média",
                SensorCalibrationPhase.Review => "Confira a nova curva",
                SensorCalibrationPhase.Saved => "✓ Curva salva e em uso",
                SensorCalibrationPhase.Failed => "Calibração interrompida",
                _ => "Escolha as referências",
            },
            ActionText = phase switch
            {
                SensorCalibrationPhase.AwaitingFirst => $"{standard} 1 pronto · adquirir",
                SensorCalibrationPhase.AwaitingSecond => $"{standard} 2 pronto · adquirir",
                SensorCalibrationPhase.StabilizingFirst or SensorCalibrationPhase.AveragingFirst => "Adquirindo ponto 1…",
                SensorCalibrationPhase.StabilizingSecond or SensorCalibrationPhase.AveragingSecond => "Adquirindo ponto 2…",
                SensorCalibrationPhase.Review => "Salvar e usar curva",
                SensorCalibrationPhase.Saved => "Nova calibração",
                _ => "Iniciar calibração",
            },
        };
    }

    private static string FormatReference(string text, bool ph)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(ph ? "F2" : "F1", CultureInfo.CurrentCulture)
            : text;
}
