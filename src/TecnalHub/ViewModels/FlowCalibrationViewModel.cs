using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Calibration;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>Editable real-flow value and read-only captured voltage.</summary>
public sealed partial class FlowCalibrationPointViewModel : ObservableObject
{
    public FlowCalibrationPointViewModel(string flowText = "", double? voltage = null)
    {
        FlowText = flowText;
        Voltage = voltage;
    }

    [ObservableProperty]
    public partial string FlowText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoltageText))]
    [NotifyPropertyChangedFor(nameof(HasVoltage))]
    public partial double? Voltage { get; set; }

    public string VoltageText => Voltage is { } voltage
        ? voltage.ToString("F6", CultureInfo.CurrentCulture)
        : "—";

    public bool HasVoltage => Voltage is not null;
}

/// <summary>
/// Dedicated v.6 airflow calibration procedure: command a point, fine-adjust it,
/// average FlowVoltage and fit the fixed two-segment curve.
/// </summary>
public sealed partial class FlowCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly List<double> _capture = [];
    private readonly int _captureTarget;
    private double _maximumFlow;

    private SensorSnapshot? _latest;
    private double? _commandedSetpoint;
    private FlowCalibrationPointViewModel? _preparedPoint;

    public FlowCalibrationViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _maximumFlow = settings.Current.Setpoints.MaxFlowLitresPerMinute;
        _captureTarget = Math.Clamp(settings.Current.Calibration.FlowCaptureSamples, 1, 100);

        foreach (var point in settings.Current.Calibration.FlowCalibrationPoints
                     .OrderBy(point => point.FlowLitresPerMinute))
        {
            AddPoint(new FlowCalibrationPointViewModel(
                point.FlowLitresPerMinute.ToString("G", CultureInfo.CurrentCulture),
                point.Voltage));
        }

        if (Points.Count == 0)
        {
            AddPoint(new FlowCalibrationPointViewModel());
        }

        SelectedPoint = Points[0];
        RecalculateCurve();
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<FlowCalibrationPointViewModel> Points { get; } = [];

    [ObservableProperty]
    public partial FlowCalibrationPointViewModel? SelectedPoint { get; set; }

    [ObservableProperty]
    public partial string AdjustmentStepText { get; set; } = "0.1";

    [ObservableProperty]
    public partial string LiveVoltageText { get; set; } = "—";

    [ObservableProperty]
    public partial string CommandedSetpointText { get; set; } = "Nenhum ponto preparado";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Informe a vazão certificada pelo padrão externo e prepare o ponto.";

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial double CaptureProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LowEquationText))]
    [NotifyPropertyChangedFor(nameof(HighEquationText))]
    [NotifyPropertyChangedFor(nameof(CurveStateText))]
    [NotifyPropertyChangedFor(nameof(CanSendCurve))]
    public partial FlowCalibrationCurve Curve { get; set; } = new(null, null);

    public string LowEquationText => Equation("V ≤ 0,0545", Curve.LowVoltage,
        "requer 3 pontos no segmento baixo");

    public string HighEquationText => Equation("V > 0,0545", Curve.HighVoltage,
        "requer 2 pontos no segmento alto");

    public string CurveStateText => Curve switch
    {
        { IsComplete: true } => "Curva completa: dois segmentos prontos",
        { HasAny: true } => "Curva parcial: somente um segmento está pronto",
        _ => "Pontos insuficientes para ajustar a curva",
    };

    public bool CanPrepare => !IsCapturing && _device.State == ConnectionState.Connected &&
                              TryGetSelectedFlow(out _);

    public bool CanAdjust => !IsCapturing && _device.State == ConnectionState.Connected &&
                             _commandedSetpoint is not null &&
                             ReferenceEquals(SelectedPoint, _preparedPoint);

    public bool CanCapture => !IsCapturing && _device.State == ConnectionState.Connected &&
                              _commandedSetpoint is not null &&
                              ReferenceEquals(SelectedPoint, _preparedPoint);

    public bool CanSendCurve => !IsCapturing && Curve.HasAny &&
                                _device.State == ConnectionState.Connected;

    public bool CanEditPoints => !IsCapturing;

    public event Action? CurveChanged;

    partial void OnSelectedPointChanged(FlowCalibrationPointViewModel? value) => NotifyCommandState();

    partial void OnIsCapturingChanged(bool value) => NotifyCommandState();

    [RelayCommand(CanExecute = nameof(CanEditPoints))]
    private void AddEmptyPoint()
    {
        var point = new FlowCalibrationPointViewModel();
        AddPoint(point);
        SelectedPoint = point;
        StatusText = "Novo ponto criado. Informe a vazão real certificada.";
    }

    [RelayCommand(CanExecute = nameof(CanEditPoints))]
    private void RemoveSelectedPoint()
    {
        if (SelectedPoint is not { } selected)
        {
            return;
        }

        selected.PropertyChanged -= OnPointChanged;
        if (ReferenceEquals(selected, _preparedPoint))
        {
            ClearPreparedPoint("Ponto preparado removido; prepare outro ponto para continuar.");
        }
        var index = Points.IndexOf(selected);
        Points.Remove(selected);
        if (Points.Count == 0)
        {
            AddPoint(new FlowCalibrationPointViewModel());
        }

        SelectedPoint = Points[Math.Clamp(index, 0, Points.Count - 1)];
        RecalculateCurve();
        StatusText = "Ponto removido; a curva foi recalculada.";
    }

    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private void PrepareSelectedPoint()
    {
        if (!TryGetSelectedFlow(out var flow))
        {
            StatusText = $"Informe uma vazão entre 0 e {_maximumFlow:G} L/min.";
            return;
        }

        _preparedPoint = SelectedPoint;
        SendCalibrationSetpoint(flow);
        StatusText = "Ponto preparado. Compare com o padrão externo e faça o ajuste fino.";
    }

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private void IncreaseSetpoint() => Adjust(+1.0);

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private void DecreaseSetpoint() => Adjust(-1.0);

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private void CaptureVoltage()
    {
        if (_latest is null || !HasValidVoltage(_latest))
        {
            StatusText = "FlowVoltage ainda não está disponível; aguarde a próxima telemetria.";
            return;
        }

        _capture.Clear();
        IsCapturing = true;
        CaptureProgressPercent = 0;
        StatusText = $"Capturando {_captureTarget} quadros distintos de FlowVoltage.";
    }

    [RelayCommand]
    private void CancelCapture()
    {
        _capture.Clear();
        IsCapturing = false;
        CaptureProgressPercent = 0;
        StatusText = "Captura cancelada; o ponto anterior foi preservado.";
    }

    [RelayCommand(CanExecute = nameof(CanEditPoints))]
    private void SavePoints()
    {
        PersistPoints();
        StatusText = "Pontos certificados salvos no app; nenhum coeficiente foi enviado.";
    }

    [RelayCommand(CanExecute = nameof(CanSendCurve))]
    private void SendCurve()
    {
        var command = TecnalCommand.Create();
        if (Curve.LowVoltage is { } low)
        {
            command.Merge(CommandBuilders.FlowCalibrationLow(low.K, low.F, low.C));
        }

        if (Curve.HighVoltage is { } high)
        {
            command.Merge(CommandBuilders.FlowCalibrationHigh(high.K, high.F, high.C));
        }

        if (command.IsEmpty)
        {
            StatusText = "Nenhum segmento válido para enviar.";
            return;
        }

        _device.Send(command);
        PersistPoints();
        StatusText = Curve.IsComplete
            ? "Dois segmentos enviados ao fluxômetro; pontos salvos no app."
            : "Segmento parcial enviado ao fluxômetro; complete o outro antes do uso em toda a faixa.";
    }

    [RelayCommand]
    private void StopCalibrationRun()
    {
        _capture.Clear();
        IsCapturing = false;
        CaptureProgressPercent = 0;
        _commandedSetpoint = null;
        _preparedPoint = null;
        CommandedSetpointText = "Fluxo em safe-stop";

        if (_device.State == ConnectionState.Connected)
        {
            _device.Send(CommandBuilders.FlowSafeStop(_maximumFlow));
            StatusText = "Ensaio de vazão encerrado; ambas as válvulas foram fechadas.";
        }
        else
        {
            StatusText = "Conexão ausente: não foi possível transmitir o safe-stop.";
        }

        NotifyCommandState();
    }

    public IReadOnlyList<(double Voltage, double Flow)> GetValidPoints()
        => Points.Select(TryReadPoint)
                 .Where(point => point is not null)
                 .Select(point => point!.Value)
                 .OrderBy(point => point.Voltage)
                 .ToArray();

    private void Adjust(double direction)
    {
        if (_commandedSetpoint is not { } current ||
            !TryParseDouble(AdjustmentStepText, out var step) || step <= 0.0)
        {
            StatusText = "Informe um passo de ajuste maior que zero.";
            return;
        }

        SendCalibrationSetpoint(Math.Clamp(current + (direction * step), 0.0, _maximumFlow));
        StatusText = "Ajuste enviado. Aguarde o padrão externo estabilizar.";
    }

    private void SendCalibrationSetpoint(double flow)
    {
        _device.Send(CommandBuilders.FlowCalibrationSetpoint(flow));
        _commandedSetpoint = flow;
        CommandedSetpointText = $"Comandado: {flow:F2} L/min";
        NotifyCommandState();
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _latest = snapshot;
        LiveVoltageText = HasValidVoltage(snapshot)
            ? snapshot.FlowVoltage.ToString("F6", CultureInfo.CurrentCulture) + " V"
            : "—";

        if (!IsCapturing || !HasValidVoltage(snapshot))
        {
            return;
        }

        _capture.Add(snapshot.FlowVoltage);
        CaptureProgressPercent = 100.0 * _capture.Count / _captureTarget;
        StatusText = $"Capturando tensão: {_capture.Count}/{_captureTarget}.";
        if (_capture.Count < _captureTarget)
        {
            return;
        }

        var mean = _capture.Average();
        SelectedPoint!.Voltage = mean;
        _capture.Clear();
        IsCapturing = false;
        CaptureProgressPercent = 100;
        StatusText = $"Ponto concluído: tensão média {mean:F6} V.";
        RecalculateCurve();
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            if (IsCapturing)
            {
                _capture.Clear();
                IsCapturing = false;
                CaptureProgressPercent = 0;
            }

            if (_commandedSetpoint is not null)
            {
                _commandedSetpoint = null;
                _preparedPoint = null;
                CommandedSetpointText = "Estado desconhecido após perda de conexão";
                StatusText = "Conexão perdida; prepare novamente o ponto antes de continuar.";
            }
        }

        NotifyCommandState();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _maximumFlow = settings.Setpoints.MaxFlowLitresPerMinute;
        if (_commandedSetpoint > _maximumFlow)
        {
            ClearPreparedPoint("O limite máximo de vazão mudou; prepare o ponto novamente.");
        }
        NotifyCommandState();
    }

    private void AddPoint(FlowCalibrationPointViewModel point)
    {
        point.PropertyChanged += OnPointChanged;
        Points.Add(point);
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FlowCalibrationPointViewModel.FlowText) or
            nameof(FlowCalibrationPointViewModel.Voltage))
        {
            if (e.PropertyName == nameof(FlowCalibrationPointViewModel.FlowText) &&
                ReferenceEquals(sender, _preparedPoint))
            {
                ClearPreparedPoint("A vazão certificada mudou; prepare o ponto novamente.");
            }
            RecalculateCurve();
            NotifyCommandState();
        }
    }

    private void ClearPreparedPoint(string status)
    {
        _commandedSetpoint = null;
        _preparedPoint = null;
        CommandedSetpointText = "Nenhum ponto preparado";
        StatusText = status;
    }

    private void RecalculateCurve()
    {
        try
        {
            Curve = CalibrationMath.FitFlowCurve(GetValidPoints());
        }
        catch (InvalidOperationException exception)
        {
            Curve = new FlowCalibrationCurve(null, null);
            StatusText = exception.Message;
        }

        CurveChanged?.Invoke();
        NotifyCommandState();
    }

    private void PersistPoints()
    {
        var persisted = GetValidPoints()
            .Select(point => new FlowCalibrationPoint
            {
                FlowLitresPerMinute = point.Flow,
                Voltage = point.Voltage,
            })
            .OrderBy(point => point.FlowLitresPerMinute)
            .ToArray();

        _settings.Update(settings => settings with
        {
            Calibration = settings.Calibration with
            {
                FlowCalibrationPoints = persisted,
                FlowCaptureSamples = _captureTarget,
            },
        });
    }

    private bool TryGetSelectedFlow(out double flow)
    {
        flow = default;
        return SelectedPoint is { } selected &&
               TryParseDouble(selected.FlowText, out flow) &&
               flow >= 0.0 && flow <= _maximumFlow;
    }

    private static (double Voltage, double Flow)? TryReadPoint(FlowCalibrationPointViewModel point)
        => point.Voltage is { } voltage &&
           double.IsFinite(voltage) && voltage >= 0.0 &&
           TryParseDouble(point.FlowText, out var flow) && flow >= 0.0
            ? (voltage, flow)
            : null;

    private static bool HasValidVoltage(SensorSnapshot snapshot)
        => snapshot.SensorCommOk && double.IsFinite(snapshot.FlowVoltage) && snapshot.FlowVoltage >= 0.0;

    private static bool TryParseDouble(string? text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
               CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanAdjust));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanSendCurve));
        OnPropertyChanged(nameof(CanEditPoints));
        PrepareSelectedPointCommand.NotifyCanExecuteChanged();
        IncreaseSetpointCommand.NotifyCanExecuteChanged();
        DecreaseSetpointCommand.NotifyCanExecuteChanged();
        CaptureVoltageCommand.NotifyCanExecuteChanged();
        SendCurveCommand.NotifyCanExecuteChanged();
        AddEmptyPointCommand.NotifyCanExecuteChanged();
        RemoveSelectedPointCommand.NotifyCanExecuteChanged();
        SavePointsCommand.NotifyCanExecuteChanged();
    }

    private static string Equation(
        string label,
        PolynomialCalibration? polynomial,
        string missing)
        => polynomial is { } value
            ? $"{label}: y = {value.K:G8}x² + {value.F:G8}x + {value.C:G8}"
            : $"{label}: {missing}";

    public void Dispose()
    {
        foreach (var point in Points)
        {
            point.PropertyChanged -= OnPointChanged;
        }

        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
