using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Linear calibration procedure for the external peristaltic pump node:
/// stages slope and intercept, calculates flow previews for internal speed references (250, 500, 1000),
/// monitors applied telemetry echoes, writes auditable JSON receipts, and dispatches to the node.
/// </summary>
public sealed partial class PumpCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string _calibrationsDirectory;
    private bool _initialised;

    private DateTime? _resetVolumeRequestedAt;
    private bool _awaitingResetVolume;
    private DateTime? _calibrationRequestedAt;
    private (double Slope, double Intercept)? _requestedCalibration;
    private bool _isPumpCommandPending;

    public PumpCalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null,
        string? calibrationsDirectory = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _calibrationsDirectory = calibrationsDirectory ?? AppPaths.CalibrationsDirectory;

        var pumpSettings = settings.Current.PumpControl;
        SlopeText = DosingInput.Format(pumpSettings.CalibrationSlope, 4);
        InterceptText = DosingInput.Format(pumpSettings.CalibrationIntercept, 4);

        IsConnected = _device.State == ConnectionState.Connected;

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;

        _initialised = true;
        ValidateAndRefresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    public partial bool IsPumpOnline { get; set; }

    [ObservableProperty]
    public partial string SlopeText { get; set; } = "0.0280";

    [ObservableProperty]
    public partial string InterceptText { get; set; } = "1.7602";

    [ObservableProperty]
    public partial string Preview250Text { get; set; } = "—";

    [ObservableProperty]
    public partial string Preview500Text { get; set; } = "—";

    [ObservableProperty]
    public partial string Preview1000Text { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedSlopeText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedInterceptText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentVolumeText { get; set; } = "—";

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ajuste o ganho (slope) e o deslocamento (intercept) da bomba peristáltica.";

    public bool IsValid => ValidationError is null;

    public bool IsAwaitingCalibration => _requestedCalibration.HasValue;

    public bool CanEditCalibration => !IsAwaitingCalibration && !_isPumpCommandPending;

    public bool CanApply => IsConnected && IsPumpOnline && IsValid && CanEditCalibration && !_awaitingResetVolume;

    public bool CanResetVolume => IsConnected && IsPumpOnline && !_awaitingResetVolume && !IsAwaitingCalibration && !_isPumpCommandPending;

    partial void OnSlopeTextChanged(string value) => ValidateAndRefresh();

    partial void OnInterceptTextChanged(string value) => ValidateAndRefresh();

    private void ValidateAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        if (!DosingInput.TryParseDouble(SlopeText, out var slope) || slope <= 0.0)
        {
            ValidationError = "Ganho (slope): número real positivo > 0 (mL/min por unidade de velocidade).";
            Preview250Text = Preview500Text = Preview1000Text = "—";
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(CanApply));
            ApplyCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!DosingInput.TryParseDouble(InterceptText, out var intercept))
        {
            ValidationError = "Deslocamento (intercept): número real válido (mL/min).";
            Preview250Text = Preview500Text = Preview1000Text = "—";
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(CanApply));
            ApplyCommand.NotifyCanExecuteChanged();
            return;
        }

        ValidationError = null;
        var q250 = (slope * 250.0) + intercept;
        var q500 = (slope * 500.0) + intercept;
        var q1000 = (slope * 1000.0) + intercept;

        Preview250Text = q250.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";
        Preview500Text = q500.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";
        Preview1000Text = q1000.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!DosingInput.TryParseDouble(SlopeText, out var slope) || slope <= 0.0 ||
            !DosingInput.TryParseDouble(InterceptText, out var intercept))
        {
            StatusText = "Parâmetros inválidos para calibração da bomba.";
            return;
        }

        var command = CommandBuilders.PumpCalibration(slope, intercept);
        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _requestedCalibration = (slope, intercept);
        _calibrationRequestedAt = _timeProvider.GetUtcNow().UtcDateTime;
        NotifyCommandAvailability();
        StatusText = $"Calibração enviada ao nó (slope: {slope.ToString("F4", CultureInfo.InvariantCulture)}, " +
                     $"intercept: {intercept.ToString("F4", CultureInfo.InvariantCulture)}). Aguardando eco aplicado...";
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    private void Revert()
    {
        var committed = _settings.Current.PumpControl;
        SlopeText = DosingInput.Format(committed.CalibrationSlope, 4);
        InterceptText = DosingInput.Format(committed.CalibrationIntercept, 4);
        StatusText = "Valores de calibração restaurados das configurações persistidas.";
    }

    [RelayCommand(CanExecute = nameof(CanResetVolume))]
    private void ResetVolume()
    {
        var command = CommandBuilders.PumpResetVolume();
        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _resetVolumeRequestedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _awaitingResetVolume = true;
        NotifyCommandAvailability();
        StatusText = "Comando de zerar volume enviado. Aguardando confirmação do nó...";
    }

    private string? WriteCalibrationReceipt(
        double requestedSlope,
        double requestedIntercept,
        double appliedSlope,
        double appliedIntercept,
        SensorSnapshot snapshot)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var receipt = new
            {
                timestampUtc = now.ToString("o"),
                node = "external_pump",
                requested = new { slope = requestedSlope, intercept = requestedIntercept },
                applied = new { slope = appliedSlope, intercept = appliedIntercept },
                hubFirmwareVersion = snapshot.HubFirmwareVersion,
                pumpNode = new
                {
                    ip = snapshot.PumpNode.Ip,
                    mac = snapshot.PumpNode.Mac,
                    firmwareVersion = snapshot.PumpNode.FirmwareVersion
                },
                previews = new
                {
                    speed_250 = (appliedSlope * 250.0) + appliedIntercept,
                    speed_500 = (appliedSlope * 500.0) + appliedIntercept,
                    speed_1000 = (appliedSlope * 1000.0) + appliedIntercept
                }
            };

            Directory.CreateDirectory(_calibrationsDirectory);
            var filename = $"bomba-externa-{now:yyyy-MM-dd_HH-mm-ss}.json";
            var filePath = Path.Combine(_calibrationsDirectory, filename);
            var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch (Exception ex)
        {
            StatusText = $"Calibração confirmada, mas o recibo não pôde ser gravado: {ex.Message}";
            return null;
        }
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        IsPumpOnline = snapshot.PumpOnline;
        if (snapshot.PumpCommandPending.HasValue)
        {
            _isPumpCommandPending = snapshot.PumpCommandPending.Value;
        }

        if (snapshot.PumpSlope.HasValue && snapshot.PumpSlope.Value > SensorReadings.NotReceived)
        {
            AppliedSlopeText = snapshot.PumpSlope.Value.ToString("F4", CultureInfo.CurrentCulture);
        }
        else
        {
            AppliedSlopeText = "—";
        }

        if (snapshot.PumpIntercept.HasValue && snapshot.PumpIntercept.Value > SensorReadings.NotReceived)
        {
            AppliedInterceptText = snapshot.PumpIntercept.Value.ToString("F4", CultureInfo.CurrentCulture);
        }
        else
        {
            AppliedInterceptText = "—";
        }

        ConfirmCalibrationIfEchoed(snapshot);

        CurrentFlowText = snapshot.PumpFlow > SensorReadings.NotReceived
            ? snapshot.PumpFlow.ToString("F3", CultureInfo.CurrentCulture) + " mL/min"
            : "—";

        CurrentVolumeText = snapshot.PumpVolume > SensorReadings.NotReceived
            ? snapshot.PumpVolume.ToString("F3", CultureInfo.CurrentCulture) + " mL"
            : "—";

        if (_awaitingResetVolume)
        {
            if (snapshot.PumpVolume is >= 0 and < 0.05)
            {
                _awaitingResetVolume = false;
                _resetVolumeRequestedAt = null;
                NotifyCommandAvailability();
                StatusText = "Volume acumulado da bomba zerado com sucesso.";
            }
            else if (_resetVolumeRequestedAt.HasValue &&
                     (_timeProvider.GetUtcNow().UtcDateTime - _resetVolumeRequestedAt.Value).TotalSeconds > 5.0)
            {
                _awaitingResetVolume = false;
                _resetVolumeRequestedAt = null;
                NotifyCommandAvailability();
                StatusText = "Aviso: nó da bomba não confirmou zeramento do volume em 5 s.";
            }
        }

        NotifyCommandAvailability();
    }

    private void ConfirmCalibrationIfEchoed(SensorSnapshot snapshot)
    {
        if (_requestedCalibration is not { } requested)
        {
            return;
        }

        if (snapshot.PumpSlope is { } appliedSlope && snapshot.PumpIntercept is { } appliedIntercept &&
            NearlyEqual(appliedSlope, requested.Slope) && NearlyEqual(appliedIntercept, requested.Intercept))
        {
            _settings.Update(s => s with
            {
                PumpControl = s.PumpControl with
                {
                    CalibrationSlope = appliedSlope,
                    CalibrationIntercept = appliedIntercept
                }
            });

            _requestedCalibration = null;
            _calibrationRequestedAt = null;
            NotifyCommandAvailability();

            var receiptPath = WriteCalibrationReceipt(
                requested.Slope,
                requested.Intercept,
                appliedSlope,
                appliedIntercept,
                snapshot);
            if (receiptPath is not null)
            {
                StatusText = $"Calibração confirmada pelo nó. Recibo salvo em {Path.GetFileName(receiptPath)}.";
            }
            return;
        }

        if (_calibrationRequestedAt.HasValue &&
            (_timeProvider.GetUtcNow().UtcDateTime - _calibrationRequestedAt.Value).TotalSeconds > 15.0)
        {
            _requestedCalibration = null;
            _calibrationRequestedAt = null;
            NotifyCommandAvailability();
            StatusText = "Aviso: a bomba não ecoou a calibração solicitada em 15 s; nenhum recibo foi gravado.";
        }
    }

    private static bool NearlyEqual(double actual, double requested)
        => Math.Abs(actual - requested) <= 0.0001;

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        IsConnected = change.State == ConnectionState.Connected;
        if (!IsConnected)
        {
            IsPumpOnline = false;
            AppliedSlopeText = "—";
            AppliedInterceptText = "—";
            CurrentFlowText = "—";
            CurrentVolumeText = "—";
            _awaitingResetVolume = false;
            _resetVolumeRequestedAt = null;
            _requestedCalibration = null;
            _calibrationRequestedAt = null;
            _isPumpCommandPending = false;
            NotifyCommandAvailability();
        }

        NotifyCommandAvailability();
    }

    private void NotifyCommandAvailability()
    {
        OnPropertyChanged(nameof(IsAwaitingCalibration));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanResetVolume));
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ResetVolumeCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
    }
}
