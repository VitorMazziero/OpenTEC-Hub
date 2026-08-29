using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Calibration;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

public enum OxygenCalibrationStage
{
    Idle,
    AwaitingFirstStandard,
    StabilizingFirst,
    AveragingFirst,
    AwaitingSecondStandard,
    StabilizingSecond,
    AveragingSecond,
    Proposed,
    Applied,
    Failed,
}

/// <summary>
/// Guided one- and two-point dissolved oxygen acquisition with stability and averaging.
/// Mirrors the robust architecture and acquisition criteria of pH calibration.
/// </summary>
public sealed partial class OxygenCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly Queue<double> _stability = new();
    private readonly List<double> _averaging = [];

    private SensorSnapshot? _lastSnapshot;
    private bool _twoPoint = true;
    private int _window;
    private int _averageCount;
    private double _threshold;
    private double _reference1;
    private double _reference2;
    private double? _raw1;
    private double? _raw2;
    private double? _proposedSlope;
    private double? _proposedIntercept;

    public OxygenCalibrationViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;

        var calibration = settings.Current.Calibration;
        StabilityWindowText = calibration.OxygenStabilityWindow.ToString(CultureInfo.CurrentCulture);
        StabilityThresholdText = calibration.OxygenStabilityStandardDeviation.ToString(
            "G", CultureInfo.CurrentCulture);
        AverageSamplesText = calibration.OxygenAverageSamples.ToString(CultureInfo.CurrentCulture);
        RefreshCurrentEquation(settings.Current);

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnePoint))]
    [NotifyPropertyChangedFor(nameof(ProcedureTitle))]
    [NotifyPropertyChangedFor(nameof(ProcedureHint))]
    public partial bool IsTwoPoint { get; set; } = true;

    public bool IsOnePoint
    {
        get => !IsTwoPoint;
        set => IsTwoPoint = !value;
    }

    public string ProcedureTitle => IsTwoPoint
        ? "Calibração linear de dois pontos"
        : "Calibração de um ponto (ajusta o intercepto)";

    public string ProcedureHint => IsTwoPoint
        ? "Estabilize o padrão, confira o raw ao vivo e confirme a aquisição."
        : "Estabilize um único padrão (normalmente 100%) e confirme a aquisição. A inclinação vigente é mantida.";

    [ObservableProperty]
    public partial string Reference1Text { get; set; } = "0";

    [ObservableProperty]
    public partial string Reference2Text { get; set; } = "100";

    [ObservableProperty]
    public partial string StabilityWindowText { get; set; } = "20";

    [ObservableProperty]
    public partial string StabilityThresholdText { get; set; } = "5";

    [ObservableProperty]
    public partial string AverageSamplesText { get; set; } = "20";

    [ObservableProperty]
    public partial OxygenCalibrationStage Stage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Pronto. Selecione um ou dois pontos para iniciar.";

    [ObservableProperty]
    public partial string InstructionText { get; set; } =
        "Prepare as soluções padrão de calibração de oxigênio dissolvido.";

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
        OxygenCalibrationStage.AwaitingFirstStandard or OxygenCalibrationStage.AwaitingSecondStandard;

    public bool IsAcquiring => Stage is
        OxygenCalibrationStage.StabilizingFirst or OxygenCalibrationStage.AveragingFirst or
        OxygenCalibrationStage.StabilizingSecond or OxygenCalibrationStage.AveragingSecond;

    public bool CanStart => !IsAwaitingOperator && !IsAcquiring;

    public bool CanConfirmPoint => IsAwaitingOperator;

    public bool CanCancel => Stage is not OxygenCalibrationStage.Idle and not OxygenCalibrationStage.Applied;

    public bool CanApplyProposal => Stage == OxygenCalibrationStage.Proposed;

    public string StageText => Stage switch
    {
        OxygenCalibrationStage.AwaitingFirstStandard => "Aguardando padrão 1",
        OxygenCalibrationStage.StabilizingFirst => "Estabilizando ponto 1",
        OxygenCalibrationStage.AveragingFirst => "Amostrando ponto 1",
        OxygenCalibrationStage.AwaitingSecondStandard => "Aguardando padrão 2",
        OxygenCalibrationStage.StabilizingSecond => "Estabilizando ponto 2",
        OxygenCalibrationStage.AveragingSecond => "Amostrando ponto 2",
        OxygenCalibrationStage.Proposed => "Resultado proposto",
        OxygenCalibrationStage.Applied => "Aplicado no app",
        OxygenCalibrationStage.Failed => "Recusado",
        _ => "Pronto",
    };

    partial void OnStageChanged(OxygenCalibrationStage value)
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

    partial void OnIsTwoPointChanged(bool value)
    {
        if (!value && Reference1Text.Trim() is "0" or "0.0" or "0,0")
        {
            Reference1Text = "100";
        }

        SecondPointText = value ? "Não adquirido" : "Não usado (um ponto)";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartOnePoint() => Start(twoPoint: false);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartTwoPoint() => Start(twoPoint: true);

    [RelayCommand(CanExecute = nameof(CanConfirmPoint))]
    private void ConfirmPoint()
    {
        _stability.Clear();
        _averaging.Clear();
        ProgressPercent = 0;
        CurrentStandardDeviationText = "—";

        if (Stage == OxygenCalibrationStage.AwaitingFirstStandard)
        {
            Stage = OxygenCalibrationStage.StabilizingFirst;
            StatusText = $"Coletando janela de estabilidade no padrão {_reference1:F1}%.";
        }
        else if (Stage == OxygenCalibrationStage.AwaitingSecondStandard)
        {
            Stage = OxygenCalibrationStage.StabilizingSecond;
            StatusText = $"Coletando janela de estabilidade no padrão {_reference2:F1}%.";
        }

        InstructionText = "Não mova a sonda. Aguardando estabilização do sinal de O₂.";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ResetAcquisition();
        Stage = OxygenCalibrationStage.Idle;
        StatusText = "Calibração cancelada; nenhum coeficiente foi alterado.";
        InstructionText = "Pronto para iniciar novo procedimento de calibração.";
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
                OxygenA = slope,
                OxygenB = intercept,
                OxygenStabilityWindow = _window,
                OxygenStabilityStandardDeviation = _threshold,
                OxygenAverageSamples = _averageCount,
            },
        });

        Stage = OxygenCalibrationStage.Applied;
        StatusText = "Coeficientes de oxigênio aplicados ao parser e persistidos.";
        InstructionText = "Os novos coeficientes já estão em vigor para todas as leituras de O₂.";
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
            Fail("Não há leitura bruta válida de oxigênio. Verifique o módulo e a sonda.");
            return;
        }

        if (!TryParseReference(Reference1Text, out _reference1) ||
            (twoPoint && !TryParseReference(Reference2Text, out _reference2)))
        {
            Fail("Referências de oxigênio devem estar entre 0 e 200%.");
            return;
        }

        if (twoPoint && Math.Abs(_reference1 - _reference2) <= 1e-9)
        {
            Fail("Use duas referências de oxigênio diferentes.");
            return;
        }

        if (!TryParseInteger(StabilityWindowText, out _window) || _window < 2 || _window > 500 ||
            !TryParsePositive(StabilityThresholdText, out _threshold) ||
            !TryParseInteger(AverageSamplesText, out _averageCount) || _averageCount < 1 || _averageCount > 500)
        {
            Fail("Critérios inválidos: janela 2–500, desvio padrão > 0 e média 1–500.");
            return;
        }

        _twoPoint = twoPoint;
        _raw1 = null;
        _raw2 = null;
        _proposedSlope = null;
        _proposedIntercept = null;
        FirstPointText = "Não adquirido";
        SecondPointText = twoPoint ? "Não adquirido" : "Não usado (um ponto)";
        ProposedEquationText = "Nenhuma curva proposta.";
        ResetAcquisition();

        Stage = OxygenCalibrationStage.AwaitingFirstStandard;
        StatusText = twoPoint ? "Calibração de dois pontos iniciada." : "Calibração de um ponto iniciada.";
        InstructionText = $"Coloque a sonda no padrão de {_reference1:F1}% O₂ e confirme o ponto.";
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (!HasValidRaw(snapshot))
        {
            CurrentRawText = "—";
            CurrentCalibratedText = "—";
            if (IsAcquiring)
            {
                StatusText = "Aquisição pausada: leitura bruta de oxigênio ausente ou inválida.";
            }
            return;
        }

        var raw = snapshot.OxygenRaw;
        CurrentRawText = raw.ToString("F1", CultureInfo.CurrentCulture);
        CurrentCalibratedText = snapshot.OxygenCalibrated.ToString("F1", CultureInfo.CurrentCulture);

        if (!IsAcquiring)
        {
            return;
        }

        if (Stage is OxygenCalibrationStage.StabilizingFirst or OxygenCalibrationStage.StabilizingSecond)
        {
            ConsumeStability(raw);
        }
        else
        {
            ConsumeAverage(raw);
        }
    }

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

        if (Stage is OxygenCalibrationStage.StabilizingFirst or OxygenCalibrationStage.StabilizingSecond)
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
        var stabilityBase = Stage == OxygenCalibrationStage.StabilizingFirst ? 0.0 : 50.0;
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
        Stage = Stage == OxygenCalibrationStage.StabilizingFirst
            ? OxygenCalibrationStage.AveragingFirst
            : OxygenCalibrationStage.AveragingSecond;
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
            ? Stage == OxygenCalibrationStage.AveragingFirst ? 20.0 : 70.0
            : 20.0;
        var span = _twoPoint ? 25.0 : 75.0;

        ProgressPercent = Math.Min(95.0, pointBase + span * _averaging.Count / _averageCount);
        StatusText = $"Média final: {_averaging.Count}/{_averageCount} quadros.";
        if (_averaging.Count < _averageCount)
        {
            return;
        }

        var average = _averaging.Average();
        if (Stage == OxygenCalibrationStage.AveragingFirst)
        {
            _raw1 = average;
            FirstPointText = $"{_reference1:F1}% O₂ → raw médio {average:F3}";
            if (_twoPoint)
            {
                Stage = OxygenCalibrationStage.AwaitingSecondStandard;
                ProgressPercent = 50;
                InstructionText = $"Coloque a sonda no padrão de {_reference2:F1}% O₂ e confirme o segundo ponto.";
                StatusText = "Primeiro ponto concluído; aguardando troca de padrão.";
                return;
            }

            var slope = _settings.Current.Calibration.OxygenA;
            Propose(slope, _reference1 - (slope * average));
            return;
        }

        _raw2 = average;
        SecondPointText = $"{_reference2:F1}% O₂ → raw médio {average:F3}";
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
        _proposedSlope = slope;
        _proposedIntercept = intercept;
        Stage = OxygenCalibrationStage.Proposed;
        ProgressPercent = 100;
        ProposedEquationText = string.Create(CultureInfo.CurrentCulture,
            $"O₂ = ({slope:G6} × raw) + ({intercept:G6})");
        StatusText = "Aquisição concluída. Revise a curva proposta antes de aplicar.";
        InstructionText = "Clique em 'Aplicar no app' para salvar os novos coeficientes.";
    }

    private void Fail(string reason)
    {
        ResetAcquisition();
        Stage = OxygenCalibrationStage.Failed;
        ProgressPercent = 0;
        StatusText = reason;
        InstructionText = "Corrija a condição informada e reinicie o procedimento.";
    }

    private void ResetAcquisition()
    {
        _stability.Clear();
        _averaging.Clear();
        ProgressPercent = 0;
        CurrentStandardDeviationText = "—";
    }

    private void RefreshCurrentEquation(AppSettings settings)
    {
        var calibration = settings.Calibration;
        CurrentEquationText = string.Create(CultureInfo.CurrentCulture,
            $"O₂ = ({calibration.OxygenA:G6} × raw) + ({calibration.OxygenB:G6})");
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected && (IsAcquiring || IsAwaitingOperator))
        {
            Fail("Conexão perdida; a aquisição foi cancelada sem alterar coeficientes.");
        }
    }

    private void OnSettingsChanged(AppSettings settings) => RefreshCurrentEquation(settings);

    private static bool HasValidRaw(SensorSnapshot? snapshot) =>
        snapshot is not null &&
        double.IsFinite(snapshot.OxygenRaw) &&
        snapshot.OxygenRaw > 0.0;

    private static bool TryParseReference(string text, out double reference)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out reference) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out reference))
        {
            return double.IsFinite(reference) && reference is >= 0.0 and <= 200.0;
        }

        reference = 0.0;
        return false;
    }

    private static bool TryParseInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) ||
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParsePositive(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return double.IsFinite(value) && value > 0.0;
        }

        value = 0.0;
        return false;
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
