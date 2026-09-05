using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

public enum PHCalibrationStage
{
    Idle,
    AwaitingFirstBuffer,
    StabilizingFirst,
    AveragingFirst,
    AwaitingSecondBuffer,
    StabilizingSecond,
    AveragingSecond,
    Proposed,
    Applied,
    Failed,
}

/// <summary>
/// Guided one- and two-point pH acquisition. It consumes accepted raw telemetry
/// frames, preserving v.6's stability and averaging equation without its repeated
/// polling of the same frame.
/// </summary>
public sealed partial class PHCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly PHControlViewModel _phControl;
    private readonly Queue<double> _stability = new();
    private readonly List<double> _averaging = [];

    private SensorSnapshot? _lastSnapshot;
    private bool _twoPoint;
    private int _window;
    private int _averageCount;
    private double _threshold;
    private double _reference1;
    private double _reference2;
    private double? _raw1;
    private double? _raw2;
    private double? _proposedSlope;
    private double? _proposedIntercept;

    public PHCalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        PHControlViewModel phControl)
    {
        _device = device;
        _settings = settings;
        _phControl = phControl;

        var calibration = settings.Current.Calibration;
        StabilityWindowText = calibration.PHStabilityWindow.ToString(CultureInfo.CurrentCulture);
        StabilityThresholdText = calibration.PHStabilityStandardDeviation.ToString(
            "G", CultureInfo.CurrentCulture);
        AverageSamplesText = calibration.PHAverageSamples.ToString(CultureInfo.CurrentCulture);
        RefreshCurrentEquation(settings.Current);

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    public partial string Reference1Text { get; set; } = "7.00";

    [ObservableProperty]
    public partial string Reference2Text { get; set; } = "4.00";

    [ObservableProperty]
    public partial string StabilityWindowText { get; set; } = "20";

    [ObservableProperty]
    public partial string StabilityThresholdText { get; set; } = "5";

    [ObservableProperty]
    public partial string AverageSamplesText { get; set; } = "20";

    [ObservableProperty]
    public partial PHCalibrationStage Stage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Pronto. Selecione um ou dois pontos para iniciar.";

    [ObservableProperty]
    public partial string InstructionText { get; set; } =
        "O controle de pH será desligado antes de retirar a sonda do reator.";

    [ObservableProperty]
    public partial string CurrentRawText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentCalibratedText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentStandardDeviationText { get; set; } = "—";

    [ObservableProperty]
    public partial string FirstPointText { get; set; } = "Não adquirido";

    [ObservableProperty]
    public partial string SecondPointText { get; set; } = "Não adquirido";

    [ObservableProperty]
    public partial string ProposedEquationText { get; set; } = "Nenhuma curva proposta.";

    [ObservableProperty]
    public partial string CurrentEquationText { get; set; } = "—";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    public bool IsAwaitingOperator => Stage is
        PHCalibrationStage.AwaitingFirstBuffer or PHCalibrationStage.AwaitingSecondBuffer;

    public bool IsAcquiring => Stage is
        PHCalibrationStage.StabilizingFirst or PHCalibrationStage.AveragingFirst or
        PHCalibrationStage.StabilizingSecond or PHCalibrationStage.AveragingSecond;

    public bool CanStart => !IsAwaitingOperator && !IsAcquiring;

    public bool CanConfirmPoint => IsAwaitingOperator;

    public bool CanCancel => Stage is not PHCalibrationStage.Idle and not PHCalibrationStage.Applied;

    public bool CanApplyProposal => Stage == PHCalibrationStage.Proposed;

    public string StageText => Stage switch
    {
        PHCalibrationStage.AwaitingFirstBuffer => "Aguardando tampão 1",
        PHCalibrationStage.StabilizingFirst => "Estabilizando ponto 1",
        PHCalibrationStage.AveragingFirst => "Amostrando ponto 1",
        PHCalibrationStage.AwaitingSecondBuffer => "Aguardando tampão 2",
        PHCalibrationStage.StabilizingSecond => "Estabilizando ponto 2",
        PHCalibrationStage.AveragingSecond => "Amostrando ponto 2",
        PHCalibrationStage.Proposed => "Resultado proposto",
        PHCalibrationStage.Applied => "Aplicado no app",
        PHCalibrationStage.Failed => "Recusado",
        _ => "Pronto",
    };

    partial void OnStageChanged(PHCalibrationStage value)
    {
        OnPropertyChanged(nameof(IsAwaitingOperator));
        OnPropertyChanged(nameof(IsAcquiring));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanConfirmPoint));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanApplyProposal));
        OnPropertyChanged(nameof(StageText));
        StartOnePointCommand.NotifyCanExecuteChanged();
        StartTwoPointCommand.NotifyCanExecuteChanged();
        ConfirmPointCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnePoint))]
    [NotifyPropertyChangedFor(nameof(ProcedureTitle))]
    [NotifyPropertyChangedFor(nameof(ProcedureHint))]
    public partial bool IsTwoPoint { get; set; } = true;

    public bool IsOnePoint
    {
        get => !IsTwoPoint;
        set
        {
            if (value != !IsTwoPoint)
            {
                IsTwoPoint = !value;
            }
        }
    }

    public string ProcedureTitle => IsTwoPoint
        ? "Calibração linear de dois pontos"
        : "Calibração de um ponto (ajusta o intercepto)";

    public string ProcedureHint => IsTwoPoint
        ? "Estabilize o padrão, confira o raw ao vivo e confirme a aquisição."
        : "Estabilize um único padrão e confirme a aquisição. A inclinação vigente é mantida.";

    partial void OnIsTwoPointChanged(bool value)
    {
        SecondPointText = value ? "Não adquirido" : "Não usado (um ponto)";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartOnePoint() => Start(twoPoint: false);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartTwoPoint() => Start(twoPoint: true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartProcedure() => Start(twoPoint: IsTwoPoint);

    [RelayCommand(CanExecute = nameof(CanConfirmPoint))]
    private void ConfirmPoint()
    {
        _stability.Clear();
        _averaging.Clear();
        ProgressPercent = 0;
        CurrentStandardDeviationText = "—";

        if (Stage == PHCalibrationStage.AwaitingFirstBuffer)
        {
            Stage = PHCalibrationStage.StabilizingFirst;
            StatusText = $"Coletando janela de estabilidade no tampão pH {_reference1:F2}.";
        }
        else if (Stage == PHCalibrationStage.AwaitingSecondBuffer)
        {
            Stage = PHCalibrationStage.StabilizingSecond;
            StatusText = $"Coletando janela de estabilidade no tampão pH {_reference2:F2}.";
        }

        InstructionText = "Não mova a sonda. A aquisição usa cada quadro aceito uma única vez.";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ResetAcquisition();
        Stage = PHCalibrationStage.Idle;
        StatusText = "Calibração cancelada; nenhum coeficiente foi alterado.";
        InstructionText = "O controle de pH permanece desligado até ser reativado explicitamente.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyProposal))]
    private void ApplyProposal()
    {
        if (_proposedSlope is not { } slope || _proposedIntercept is not { } intercept ||
            !double.IsFinite(slope) || !double.IsFinite(intercept))
        {
            Fail("A curva proposta não é finita e foi recusada.");
            return;
        }

        _settings.Update(settings => settings with
        {
            Calibration = settings.Calibration with
            {
                PHSlope = slope,
                PHIntercept = intercept,
                PHStabilityWindow = _window,
                PHStabilityStandardDeviation = _threshold,
                PHAverageSamples = _averageCount,
            },
        });

        Stage = PHCalibrationStage.Applied;
        StatusText = "Coeficientes de pH aplicados ao parser e persistidos.";
        InstructionText = "O próximo valor aceito será ecoado ao módulo como pHCal. Reative a dosagem somente após recolocar a sonda no reator.";
    }

    private void Start(bool twoPoint)
    {
        if (_device.State != ConnectionState.Connected)
        {
            Fail("Conecte ao equipamento antes de iniciar a calibração.");
            return;
        }

        if (!HasValidRaw(_lastSnapshot))
        {
            Fail("Não há leitura bruta válida de pH. Verifique o módulo e a sonda.");
            return;
        }

        if (!TryParseReference(Reference1Text, out _reference1) ||
            (twoPoint && !TryParseReference(Reference2Text, out _reference2)))
        {
            Fail("Referências de pH devem estar entre 0 e 14.");
            return;
        }

        if (twoPoint && Math.Abs(_reference1 - _reference2) <= 1e-9)
        {
            Fail("Use duas referências de pH diferentes.");
            return;
        }

        if (!TryParseInteger(StabilityWindowText, out _window) || _window < 2 || _window > 500 ||
            !TryParsePositive(StabilityThresholdText, out _threshold) ||
            !TryParseInteger(AverageSamplesText, out _averageCount) || _averageCount < 1 || _averageCount > 500)
        {
            Fail("Critérios inválidos: janela 2–500, desvio padrão > 0 e média 1–500.");
            return;
        }

        _phControl.SuspendForCalibration();
        _twoPoint = twoPoint;
        _raw1 = null;
        _raw2 = null;
        _proposedSlope = null;
        _proposedIntercept = null;
        FirstPointText = "Não adquirido";
        SecondPointText = twoPoint ? "Não adquirido" : "Não usado (um ponto)";
        ProposedEquationText = "Nenhuma curva proposta.";
        ResetAcquisition();

        Stage = PHCalibrationStage.AwaitingFirstBuffer;
        StatusText = twoPoint ? "Calibração de dois pontos iniciada." : "Calibração de um ponto iniciada.";
        InstructionText = $"Lave a sonda, coloque-a no tampão pH {_reference1:F2} e confirme o ponto.";
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (!HasValidRaw(snapshot))
        {
            CurrentRawText = "—";
            if (IsAcquiring)
            {
                StatusText = "Aquisição pausada: leitura bruta de pH ausente ou inválida.";
            }
            return;
        }

        var raw = snapshot.PHRaw;
        CurrentRawText = raw.ToString("F1", CultureInfo.CurrentCulture);
        CurrentCalibratedText = snapshot.PHCalibrated.ToString("F2", CultureInfo.CurrentCulture);
        if (!IsAcquiring)
        {
            return;
        }

        if (Stage is PHCalibrationStage.StabilizingFirst or PHCalibrationStage.StabilizingSecond)
        {
            ConsumeStability(raw);
        }
        else
        {
            ConsumeAverage(raw);
        }
    }

    /// <summary>
    /// Re-reads the acquisition criteria while a run is in progress.
    /// </summary>
    /// <remarks>
    /// The criteria are the operator's judgement about a probe that is already in the buffer:
    /// a noisy sensor may need a wider window or a looser σ, and discovering that should not
    /// force a restart. Only well-formed values are adopted, so a half-typed number leaves the
    /// running acquisition on its previous criteria instead of destabilising it.
    /// </remarks>
    private void ApplyLiveCriteria()
    {
        if (!IsAcquiring)
        {
            return;
        }

        if (TryParseInteger(StabilityWindowText, out var window) && window is >= 2 and <= 500)
        {
            _window = window;
        }

        if (TryParsePositive(StabilityThresholdText, out var threshold))
        {
            _threshold = threshold;
        }

        if (TryParseInteger(AverageSamplesText, out var average) && average is >= 1 and <= 500)
        {
            _averageCount = average;
        }

        // Adopting the new criteria can already satisfy the current step, so re-evaluate now
        // rather than waiting for another frame.
        if (Stage is PHCalibrationStage.StabilizingFirst or PHCalibrationStage.StabilizingSecond)
        {
            TrimStabilityWindow();
            EvaluateStability();
        }
        else
        {
            EvaluateAverage();
        }
    }

    partial void OnStabilityWindowTextChanged(string value) => ApplyLiveCriteria();

    partial void OnStabilityThresholdTextChanged(string value) => ApplyLiveCriteria();

    partial void OnAverageSamplesTextChanged(string value) => ApplyLiveCriteria();

    private void TrimStabilityWindow()
    {
        while (_stability.Count > _window)
        {
            _stability.Dequeue();
        }
    }

    private void ConsumeStability(double raw)
    {
        _stability.Enqueue(raw);
        TrimStabilityWindow();
        EvaluateStability();
    }

    private void EvaluateStability()
    {
        var stabilityBase = Stage == PHCalibrationStage.StabilizingFirst ? 0.0 : 50.0;
        ProgressPercent = stabilityBase + (20.0 * _stability.Count / _window);
        if (_stability.Count < _window)
        {
            StatusText = $"Obtendo janela inicial ({_stability.Count}/{_window}).";
            return;
        }

        var values = _stability.ToArray();
        var standardDeviation = CalibrationMath.SampleStandardDeviation(values);
        CurrentStandardDeviationText = standardDeviation.ToString("F2", CultureInfo.CurrentCulture);
        StatusText = $"Estabilidade: σ={standardDeviation:F2} contagens; limite < {_threshold:F2}.";

        if (standardDeviation >= _threshold)
        {
            return;
        }

        _averaging.Clear();
        Stage = Stage == PHCalibrationStage.StabilizingFirst
            ? PHCalibrationStage.AveragingFirst
            : PHCalibrationStage.AveragingSecond;
        StatusText = "Sinal estável. Iniciando média final.";
    }

    private void ConsumeAverage(double raw)
    {
        _averaging.Add(raw);
        EvaluateAverage();
    }

    private void EvaluateAverage()
    {
        var pointBase = _twoPoint
            ? Stage == PHCalibrationStage.AveragingFirst ? 20.0 : 70.0
            : 20.0;
        var span = _twoPoint ? 25.0 : 75.0;

        ProgressPercent = Math.Min(95.0, pointBase + span * _averaging.Count / _averageCount);
        StatusText = $"Média final: {_averaging.Count}/{_averageCount} quadros.";
        if (_averaging.Count < _averageCount)
        {
            return;
        }

        var average = _averaging.Average();
        if (Stage == PHCalibrationStage.AveragingFirst)
        {
            _raw1 = average;
            FirstPointText = $"pH {_reference1:F2} → raw médio {average:F3}";
            if (_twoPoint)
            {
                Stage = PHCalibrationStage.AwaitingSecondBuffer;
                ProgressPercent = 50;
                InstructionText = $"Lave a sonda, coloque-a no tampão pH {_reference2:F2} e confirme o segundo ponto.";
                StatusText = "Primeiro ponto concluído; aguardando troca de tampão.";
                return;
            }

            var slope = _settings.Current.Calibration.PHSlope;
            Propose(slope, _reference1 - (slope * average));
            return;
        }

        _raw2 = average;
        SecondPointText = $"pH {_reference2:F2} → raw médio {average:F3}";
        try
        {
            var fit = CalibrationMath.FitLinear(
                new LinearCalibrationPoint(_raw1!.Value, _reference1),
                new LinearCalibrationPoint(_raw2.Value, _reference2));
            Propose(fit.Slope, fit.Intercept);
        }
        catch (InvalidOperationException exception)
        {
            Fail(exception.Message);
        }
    }

    private void Propose(double slope, double intercept)
    {
        if (!double.IsFinite(slope) || !double.IsFinite(intercept) || Math.Abs(slope) <= 1e-15)
        {
            Fail("A curva calculada é degenerada e não será aplicada.");
            return;
        }

        _proposedSlope = slope;
        _proposedIntercept = intercept;
        ProposedEquationText = FormatEquation("pH", slope, intercept);
        ProgressPercent = 100;
        Stage = PHCalibrationStage.Proposed;
        StatusText = "Aquisição concluída. Revise a equação antes de aplicar.";
        InstructionText = "Aplicar altera o parser do app; a curva ainda não foi modificada.";
    }

    private void Fail(string message)
    {
        ResetAcquisition();
        Stage = PHCalibrationStage.Failed;
        StatusText = message;
        InstructionText = "Corrija a condição indicada e reinicie o procedimento.";
    }

    private void ResetAcquisition()
    {
        _stability.Clear();
        _averaging.Clear();
        CurrentStandardDeviationText = "—";
        ProgressPercent = 0;
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected && (IsAcquiring || IsAwaitingOperator))
        {
            Fail("Conexão perdida; a aquisição foi cancelada sem alterar coeficientes.");
        }
    }

    private void OnSettingsChanged(AppSettings settings) => RefreshCurrentEquation(settings);

    private void RefreshCurrentEquation(AppSettings settings)
        => CurrentEquationText = FormatEquation(
            "pH", settings.Calibration.PHSlope, settings.Calibration.PHIntercept);

    private static string FormatEquation(string label, double slope, double intercept)
    {
        var sign = intercept < 0 ? "−" : "+";
        return string.Create(CultureInfo.CurrentCulture,
            $"{label} = {slope:G13} × raw {sign} {Math.Abs(intercept):G13}");
    }

    private static bool HasValidRaw(SensorSnapshot? snapshot)
        => snapshot is { SensorCommOk: true } &&
           double.IsFinite(snapshot.PHRaw) && snapshot.PHRaw > 0.1;

    private static bool TryParseReference(string? text, out double value)
        => TryParsePositiveOrZero(text, out value) && value <= 14.0;

    private static bool TryParsePositive(string? text, out double value)
        => TryParsePositiveOrZero(text, out value) && value > 0.0;

    private static bool TryParsePositiveOrZero(string? text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
               CultureInfo.InvariantCulture, out value) &&
           double.IsFinite(value) && value >= 0.0;

    private static bool TryParseInteger(string? text, out int value)
    {
        value = default;
        return TryParsePositiveOrZero(text, out var parsed) &&
               Math.Abs(parsed - Math.Round(parsed)) <= 1e-9 &&
               parsed <= int.MaxValue &&
               (value = (int)parsed) >= 0;
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
