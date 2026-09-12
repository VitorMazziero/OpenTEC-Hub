using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>Editable real-flow value and read-only captured voltage.</summary>
public sealed partial class FlowCalibrationPointViewModel : ObservableObject
{
    /// <summary>The flowmeter's ADC reference: a typed voltage outside this range is a transcription error.</summary>
    public const double MaximumVoltage = 3.3;

    private bool _syncing;

    public FlowCalibrationPointViewModel(string flowText = "", double? voltage = null, FlowVoltageSource source = FlowVoltageSource.Captured)
    {
        FlowText = flowText;
        VoltageText = "";
        Source = source;
        Voltage = voltage;
    }

    [ObservableProperty]
    public partial string FlowText { get; set; }

    /// <summary>
    /// The voltage as a number. Set by a capture (mean of N frames) or by parsing what the operator
    /// typed; null when the text is empty or not a number.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoltage))]
    [NotifyPropertyChangedFor(nameof(IsVoltageOutOfRange))]
    public partial double? Voltage { get; set; }

    /// <summary>
    /// The voltage as the operator sees and edits it (§O). Mirrors <see cref="Voltage"/> both ways:
    /// a capture formats it, typing parses it (comma or point). Editing marks the point
    /// <see cref="FlowVoltageSource.Typed"/>; a capture marks it <see cref="FlowVoltageSource.Captured"/>.
    /// </summary>
    [ObservableProperty]
    public partial string VoltageText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTyped))]
    [NotifyPropertyChangedFor(nameof(SourceLabel))]
    public partial FlowVoltageSource Source { get; set; }

    public bool HasVoltage => Voltage is not null;

    public bool IsTyped => Source == FlowVoltageSource.Typed;

    /// <summary>A discreet mark on the row: a typed voltage is a transcription, not a measurement.</summary>
    public string SourceLabel => IsTyped ? "digitada" : "";

    /// <summary>Negative or above the ADC reference: shown as an error and left out of the fit.</summary>
    public bool IsVoltageOutOfRange => Voltage is { } v && (v < 0.0 || v > MaximumVoltage);

    /// <summary>Sets the voltage from a telemetry capture: formats the text and marks the source.</summary>
    public void SetCapturedVoltage(double voltage)
    {
        Source = FlowVoltageSource.Captured;
        Voltage = voltage;
    }

    partial void OnVoltageChanged(double? value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            VoltageText = value is { } v ? v.ToString("F6", CultureInfo.CurrentCulture) : "";
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnVoltageTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            Source = FlowVoltageSource.Typed;
            Voltage = double.TryParse((value ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                ? parsed
                : null;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>
/// Airflow calibration procedure: send a trial setpoint, nudge it until the external
/// standard reads the flow you want, average FlowVoltage into the selected row and fit the
/// two-segment curve.
/// </summary>
/// <remarks>
/// The commanded setpoint is deliberately independent of the table row: the operator aims at a
/// convenient flow ("about 1 L/min"), reads the true value off the certified standard and types
/// that into the row — or nudges the setpoint with the step buttons until the standard shows the
/// exact flow wanted. Either way the row records the true flow against the measured voltage.
/// </remarks>
public sealed partial class FlowCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly List<double> _capture = [];
    private readonly TimeProvider _time;
    private readonly int _captureTarget;
    private double _maximumFlow;

    private SensorSnapshot? _latest;
    private double? _commandedSetpoint;
    private string? _pendingConfirmationText;

    public FlowCalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        TimeProvider? time = null)
    {
        _device = device;
        _time = time ?? TimeProvider.System;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _settings = settings;
        _maximumFlow = settings.Current.Setpoints.MaxFlowLitresPerMinute;
        _captureTarget = Math.Clamp(settings.Current.Calibration.FlowCaptureSamples, 1, 100);

        // A settings file written before the reference run existed carries an empty array, which
        // overrides the record's default. Fall back to the certified points so the workspace
        // always opens on the curve the flowmeter is actually running.
        var stored = settings.Current.Calibration.FlowCalibrationPoints;
        var seed = stored.Length > 0 ? stored : CalibrationSettings.CertifiedReferencePoints;

        foreach (var point in seed.OrderBy(point => point.FlowLitresPerMinute))
        {
            AddPoint(new FlowCalibrationPointViewModel(
                point.FlowLitresPerMinute.ToString("G", CultureInfo.CurrentCulture),
                point.Voltage,
                point.Source));
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

    /// <summary>The trial flow sent to the flowmeter; the operator aims near a round value.</summary>
    [ObservableProperty]
    public partial string SetpointText { get; set; } = "1";

    [ObservableProperty]
    public partial string AdjustmentStepText { get; set; } = "0.1";

    [ObservableProperty]
    public partial string LiveVoltageText { get; set; } = "—";

    [ObservableProperty]
    public partial string CommandedSetpointText { get; set; } = "Nenhum setpoint enviado";

    /// <summary>
    /// Live acquisition feedback. Starts empty because the standing instruction is shown under
    /// the panel heading; repeating it here would just be the same sentence twice.
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    public partial bool IsFlowmeterOnline { get; set; }

    [ObservableProperty]
    public partial bool IsFlowCommandPending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    public partial bool IsAwaitingAck { get; set; }

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial double CaptureProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LowEquationText))]
    [NotifyPropertyChangedFor(nameof(HighEquationText))]
    [NotifyPropertyChangedFor(nameof(CurveStateText))]
    [NotifyPropertyChangedFor(nameof(ContinuityText))]
    [NotifyPropertyChangedFor(nameof(CanSendCurve))]
    public partial FlowCalibrationCurve Curve { get; set; } = CalibrationMath.FirmwareDefault;

    /// <summary>True while the shown curve is the V05 factory curve rather than one fitted here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveStateText))]
    public partial bool IsUsingFirmwareDefault { get; set; } = true;

    public string LowEquationText => Equation("V ≤ 0,0545", Curve.LowVoltage,
        "requer ao menos 1 ponto abaixo do limiar e o segmento alto");

    public string HighEquationText => Equation("V > 0,0545", Curve.HighVoltage,
        "requer 2 pontos no segmento alto");

    public string CurveStateText => (IsUsingFirmwareDefault, Curve) switch
    {
        (true, _) => "Curva padrão de fábrica (firmware V05)",
        (false, { IsComplete: true }) => "Curva ajustada: dois segmentos prontos",
        (false, { HasAny: true }) => "Curva parcial: somente um segmento está pronto",
        _ => "Pontos insuficientes para ajustar a curva",
    };

    /// <summary>The jump at the split — the defect the anchored quartic exists to remove.</summary>
    public string ContinuityText => Curve.DiscontinuityAtSplit is { } jump
        ? $"Salto no limiar (0,0545 V): {Math.Abs(jump):F6} L/min"
        : "Salto no limiar: indisponível (curva incompleta)";

    /// <summary>
    /// How long a flow command may wait for the flowmeter's ack before the page says so and lets
    /// the operator send again (§L.5, bench of 2026-09-11: a fragmented calibration frame left the
    /// page in "aguardando" with the buttons dead). The Hub keeps retrying on its own meanwhile.
    /// </summary>
    public static readonly TimeSpan AckOverdueAfter = TimeSpan.FromSeconds(15);
    private long _awaitingAckSinceTimestamp;

    /// <summary>True once an ack has been pending longer than <see cref="AckOverdueAfter"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(CanSendSetpoint))]
    [NotifyPropertyChangedFor(nameof(CanSendCurve))]
    public partial bool IsAckOverdue { get; set; }

    public bool CanSendFlowCommands => _device.State == ConnectionState.Connected &&
                                       IsFlowmeterOnline && (!IsAwaitingAck || IsAckOverdue);

    /// <summary>The trial setpoint may be sent whenever the link and flowmeter allow it.</summary>
    public bool CanSendSetpoint => !IsCapturing && CanSendFlowCommands;

    /// <summary>Nudging only makes sense once a setpoint is actually out there.</summary>
    public bool CanAdjust => !IsCapturing && CanSendFlowCommands && _commandedSetpoint is not null;

    /// <summary>Capturing needs a live setpoint and a row to write the voltage into.</summary>
    public bool CanCapture => !IsCapturing && CanSendFlowCommands &&
                              _commandedSetpoint is not null &&
                              SelectedPoint is not null;

    public bool CanSendCurve => !IsCapturing && Curve.HasAny &&
                                CanSendFlowCommands;

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

    [RelayCommand(CanExecute = nameof(CanSendSetpoint))]
    private void SendSetpoint()
    {
        if (!TryParseDouble(SetpointText, out var flow) || flow < 0.0 || flow > _maximumFlow)
        {
            StatusText = $"Informe um setpoint entre 0 e {_maximumFlow:G} L/min.";
            return;
        }

        SendCalibrationSetpoint(flow);
    }

    /// <summary>Loads the V05 factory curve back into the workspace without sending anything.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPoints))]
    private void RestoreFirmwareDefault()
    {
        Curve = CalibrationMath.FirmwareDefault;
        IsUsingFirmwareDefault = true;
        StatusText = "Curva padrão de fábrica (V05) carregada; use “Salvar e enviar curva” para gravá-la.";
        CurveChanged?.Invoke();
        NotifyCommandState();
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
        var command = OpenTECCommand.Create().Set(CommandKeys.MaxFlow, _maximumFlow);
        if (Curve.LowVoltage is { } low)
        {
            command.Merge(CommandBuilders.FlowCalibrationLow(low.K, low.F, low.C, low.A, low.B));
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

        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        PersistPoints();
        MarkAwaitingAck(Curve.IsComplete
            ? "Dois segmentos enviados ao fluxômetro; pontos salvos no app."
            : "Segmento parcial enviado ao fluxômetro; complete o outro antes do uso em toda a faixa.");
    }

    public IReadOnlyList<(double Voltage, double Flow)> GetValidPoints()
        => Points.Select(TryReadPoint)
                 .Where(point => point is not null)
                 .Select(point => point!.Value)
                 .OrderBy(point => point.Voltage)
                 .ToArray();

    /// <summary>The rows whose typed voltage is outside the ADC range; they are excluded from the fit.</summary>
    public int OutOfRangePointCount => Points.Count(point => point.IsVoltageOutOfRange);

    private void Adjust(double direction)
    {
        if (_commandedSetpoint is not { } current ||
            !TryParseDouble(AdjustmentStepText, out var step) || step <= 0.0)
        {
            StatusText = "Informe um passo de ajuste maior que zero.";
            return;
        }

        SendCalibrationSetpoint(Math.Clamp(current + (direction * step), 0.0, _maximumFlow));
    }

    private void SendCalibrationSetpoint(double flow)
    {
        var result = _dispatcher.Dispatch(CommandBuilders.FlowCalibrationSetpoint(flow));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        _commandedSetpoint = flow;
        // Keep the entry in step with what is actually commanded, so the step buttons and the
        // typed value never disagree about the current trial point.
        SetpointText = flow.ToString("0.###", CultureInfo.CurrentCulture);
        CommandedSetpointText = $"Comandado: {flow:F2} L/min";
        MarkAwaitingAck("Comando confirmado. Compare com o padrão externo e anote a vazão real.");
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        var wasAwaiting = IsAwaitingAck;
        _latest = snapshot;
        IsFlowmeterOnline = snapshot.FlowmeterOnline;
        IsFlowCommandPending = snapshot.FlowCommandPending;
        IsAwaitingAck = snapshot.FlowCommandPending;
        LiveVoltageText = HasValidVoltage(snapshot)
            ? snapshot.FlowVoltage.ToString("F6", CultureInfo.CurrentCulture) + " V"
            : "—";

        if (!IsAwaitingAck)
        {
            IsAckOverdue = false;
        }
        else if (!wasAwaiting)
        {
            _awaitingAckSinceTimestamp = _time.GetTimestamp();
        }
        else if (!IsAckOverdue && _time.GetElapsedTime(_awaitingAckSinceTimestamp) >= AckOverdueAfter)
        {
            IsAckOverdue = true;
        }

        if (!IsFlowmeterOnline)
        {
            StatusText = "Fluxômetro Desconectado da Central.";
        }
        else if (IsAwaitingAck && IsAckOverdue)
        {
            StatusText = $"Sem confirmação do fluxômetro há {_time.GetElapsedTime(_awaitingAckSinceTimestamp).TotalSeconds:F0} s. " +
                         "O Hub continuará reenviando até o link voltar; você pode reenviar a curva ou o setpoint.";
        }
        else if (IsAwaitingAck)
        {
            StatusText = "Aguardando confirmação do fluxômetro...";
        }
        else if (wasAwaiting && _pendingConfirmationText is { } confirmed)
        {
            StatusText = confirmed;
            _pendingConfirmationText = null;
        }

        NotifyCommandState();

        if (!IsCapturing || !IsFlowmeterOnline || !HasValidVoltage(snapshot))
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
        SelectedPoint!.SetCapturedVoltage(mean);
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
                ClearCommandedSetpoint(
                    "Estado desconhecido após perda de conexão",
                    "Conexão perdida; envie o setpoint novamente antes de continuar.");
            }
        }

        NotifyCommandState();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _maximumFlow = settings.Setpoints.MaxFlowLitresPerMinute;
        if (_commandedSetpoint > _maximumFlow)
        {
            ClearCommandedSetpoint(
                "Nenhum setpoint enviado",
                "O limite máximo de vazão mudou; envie o setpoint novamente.");
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
            // Editing the certified flow no longer invalidates the trial setpoint: the row
            // records what the external standard read, which is exactly what the operator is
            // expected to type while the setpoint stays put.
            RecalculateCurve();
            NotifyCommandState();
            if (sender is FlowCalibrationPointViewModel { IsVoltageOutOfRange: true })
            {
                StatusText = $"Tensão fora de 0–{FlowCalibrationPointViewModel.MaximumVoltage:0.0} V: o ponto fica fora do ajuste até ser corrigido.";
            }
        }
    }

    private void ClearCommandedSetpoint(string commandedText, string status)
    {
        _commandedSetpoint = null;
        CommandedSetpointText = commandedText;
        StatusText = status;
    }

    private void RecalculateCurve()
    {
        try
        {
            var fitted = CalibrationMath.FitFlowCurve(GetValidPoints());

            // Without enough certified points there is nothing to fit; showing the factory
            // curve is more useful than an empty plot, and it is what the flowmeter is running.
            if (fitted.HasAny)
            {
                Curve = fitted;
                IsUsingFirmwareDefault = false;
            }
            else
            {
                Curve = CalibrationMath.FirmwareDefault;
                IsUsingFirmwareDefault = true;
            }
        }
        catch (InvalidOperationException exception)
        {
            Curve = CalibrationMath.FirmwareDefault;
            IsUsingFirmwareDefault = true;
            StatusText = exception.Message;
        }

        CurveChanged?.Invoke();
        NotifyCommandState();
    }

    private void PersistPoints()
    {
        var persisted = Points
            .Select(point => (Point: point, Read: TryReadPoint(point)))
            .Where(item => item.Read is not null)
            .Select(item => new FlowCalibrationPoint
            {
                FlowLitresPerMinute = item.Read!.Value.Flow,
                Voltage = item.Read.Value.Voltage,
                Source = item.Point.Source,
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

    private static (double Voltage, double Flow)? TryReadPoint(FlowCalibrationPointViewModel point)
        => point.Voltage is { } voltage &&
           double.IsFinite(voltage) && voltage >= 0.0 && voltage <= FlowCalibrationPointViewModel.MaximumVoltage &&
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
        OnPropertyChanged(nameof(CanSendSetpoint));
        OnPropertyChanged(nameof(CanAdjust));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanSendCurve));
        OnPropertyChanged(nameof(CanEditPoints));
        SendSetpointCommand.NotifyCanExecuteChanged();
        IncreaseSetpointCommand.NotifyCanExecuteChanged();
        DecreaseSetpointCommand.NotifyCanExecuteChanged();
        CaptureVoltageCommand.NotifyCanExecuteChanged();
        SendCurveCommand.NotifyCanExecuteChanged();
        AddEmptyPointCommand.NotifyCanExecuteChanged();
        RemoveSelectedPointCommand.NotifyCanExecuteChanged();
        SavePointsCommand.NotifyCanExecuteChanged();
        RestoreFirmwareDefaultCommand.NotifyCanExecuteChanged();
    }

    private void MarkAwaitingAck(string confirmationText)
    {
        _pendingConfirmationText = confirmationText;
        _awaitingAckSinceTimestamp = _time.GetTimestamp();
        IsAckOverdue = false;
        IsAwaitingAck = true;
        StatusText = "Aguardando confirmação do fluxômetro...";
        NotifyCommandState();
    }

    private static string Equation(
        string label,
        PolynomialCalibration? polynomial,
        string missing)
    {
        if (polynomial is not { } value)
        {
            return $"{label}: {missing}";
        }

        var quartic = value.IsQuartic
            ? $"{value.A:G8}x⁴ + {value.B:G8}x³ + "
            : "";
        return $"{label}: y = {quartic}{value.K:G8}x² + {value.F:G8}x + {value.C:G8}";
    }

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
