using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>One volumetric run in the table: S held for a measured time, V collected, Q derived.</summary>
public sealed partial class PumpCalibrationRunViewModel : ObservableObject
{
    public PumpCalibrationRunViewModel(PumpCalibrationPoint point)
    {
        Point = point;
    }

    public PumpCalibrationPoint Point { get; }

    public double SpeedUnits => Point.SpeedUnits;

    public double Seconds => Point.Seconds;

    public double VolumeMl => Point.VolumeMl;

    public double FlowMlPerMin => Point.FlowMlPerMin;

    public string SpeedText => SpeedUnits.ToString("F0", CultureInfo.CurrentCulture);

    public string SecondsText => Seconds.ToString("F1", CultureInfo.CurrentCulture) + " s";

    public string VolumeText => VolumeMl.ToString("F1", CultureInfo.CurrentCulture) + " mL";

    public string FlowText => FlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";

    /// <summary>Q − (a·S + b) against the current fit, mL/min; null before a fit exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResidualText))]
    public partial double? Residual { get; set; }

    public string ResidualText => Residual is { } r
        ? (r >= 0 ? "+" : "") + r.ToString("F2", CultureInfo.CurrentCulture)
        : "—";
}

/// <summary>
/// Calibration of the external peristaltic pump: <c>Q = slope · S + intercept</c>, S the
/// node's internal speed (0..1000).
/// </summary>
/// <remarks>
/// <para>
/// Two ways in. The coefficient fields can be typed directly (the original Etapa 7 flow) or
/// derived from <b>volumetric runs</b>: the operator picks S and a duration, the app holds
/// the pump at S with <c>pump_speed</c>, stops it from its own clock, and the operator types
/// the volume collected in a graduated vessel. Each run is a point (S, Q = V/Δt); a least
/// squares line over the runs fills the coefficient fields on request. Volume, never mass:
/// this bench has no balance in the loop, and the vessel is the reference.
/// </para>
/// <para>
/// The stop is automatic on purpose. The node has no timer on <c>pump_speed</c>, so the app
/// is the only party that knows how long the pump ran; the elapsed time recorded on the
/// point is the interval between the start frame and the stop frame leaving this process,
/// not the nominal duration. Applying coefficients still goes through the node's echo and
/// writes the receipt only on confirmation, exactly as before.
/// </para>
/// </remarks>
public sealed partial class PumpCalibrationViewModel : ObservableObject, IDisposable
{
    /// <summary>Shortest run the UI accepts. Below this the vessel reading dominates the error.</summary>
    public const double MinRunSeconds = 5.0;

    /// <summary>Longest run the UI accepts; ten minutes fills any bench vessel at full speed.</summary>
    public const double MaxRunSeconds = 600.0;

    private static readonly TimeSpan RunTickInterval = TimeSpan.FromMilliseconds(250);

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

    // ---- volumetric run state ----
    private DispatcherTimer? _runTimer;
    private DateTimeOffset _runStartedAt;
    private double _runSpeed;
    private double _runPlannedSeconds;
    private (double Speed, double Seconds)? _lastRun;
    private bool _stopPendingOnReconnect;
    private PumpLinearFit? _fit;
    private PumpLinearFit? _fitBehindCoefficients;

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
        foreach (var point in pumpSettings.CalibrationPoints)
        {
            Runs.Add(new PumpCalibrationRunViewModel(point));
        }

        IsConnected = _device.State == ConnectionState.Connected;

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;

        _initialised = true;
        ValidateAndRefresh();
        ValidateRunInputs();
        RecomputeFit();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
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
    public partial string StatusText { get; set; } = "Ajuste o ganho (slope) e o deslocamento (intercept) da bomba peristáltica, ou construa a curva com acionamentos volumétricos.";

    // ---- volumetric run: inputs ----

    /// <summary>Internal speed S for the next run, 1..1000.</summary>
    [ObservableProperty]
    public partial string RunSpeedText { get; set; } = "500";

    /// <summary>How long the next run holds S, in seconds.</summary>
    [ObservableProperty]
    public partial string RunDurationText { get; set; } = "60";

    /// <summary>Volume read on the graduated vessel after the last run, mL.</summary>
    [ObservableProperty]
    public partial string MeasuredVolumeText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
    public partial string? RunValidationError { get; set; }

    // ---- volumetric run: progress ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
    [NotifyPropertyChangedFor(nameof(CanAbortRun))]
    [NotifyPropertyChangedFor(nameof(CanAddRunPoint))]
    [NotifyPropertyChangedFor(nameof(CanEditRuns))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double RunProgressPercent { get; set; }

    [ObservableProperty]
    public partial string RunCountdownText { get; set; } = "";

    /// <summary>A run finished and its volume has not been entered yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddRunPoint))]
    public partial bool HasPendingRun { get; set; }

    /// <summary>"S 500 durante 60,2 s" for the run awaiting its volume.</summary>
    [ObservableProperty]
    public partial string RunSummaryText { get; set; } = "";

    // ---- volumetric run: table and fit ----

    public ObservableCollection<PumpCalibrationRunViewModel> Runs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseFit))]
    public partial bool HasFit { get; set; }

    [ObservableProperty]
    public partial string FitSlopeText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitInterceptText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitRSquaredText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitSummaryText { get; set; } = "Nenhum acionamento registrado.";

    /// <summary>Why the fit cannot be used yet (too few runs, single speed, non-positive slope); null when usable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseFit))]
    public partial string? FitWarning { get; set; }

    public bool IsValid => ValidationError is null;

    public bool IsAwaitingCalibration => _requestedCalibration.HasValue;

    public bool CanEditCalibration => !IsAwaitingCalibration && !_isPumpCommandPending;

    public bool CanApply => IsConnected && IsPumpOnline && IsValid && CanEditCalibration && !_awaitingResetVolume && !IsRunning;

    public bool CanResetVolume => IsConnected && IsPumpOnline && !_awaitingResetVolume && !IsAwaitingCalibration && !_isPumpCommandPending && !IsRunning;

    public bool CanStartRun => IsConnected && IsPumpOnline && !IsRunning && RunValidationError is null &&
                               !_isPumpCommandPending && !IsAwaitingCalibration && !_awaitingResetVolume;

    public bool CanAbortRun => IsRunning;

    public bool CanAddRunPoint => HasPendingRun && !IsRunning && TryParseVolume(out _);

    public bool CanEditRuns => !IsRunning;

    public bool CanUseFit => HasFit && FitWarning is null && CanEditCalibration && !IsRunning;

    /// <summary>The current least squares line over <see cref="Runs"/>, or null.</summary>
    public PumpLinearFit? Fit => _fit;

    partial void OnSlopeTextChanged(string value) => ValidateAndRefresh();

    partial void OnInterceptTextChanged(string value) => ValidateAndRefresh();

    partial void OnRunSpeedTextChanged(string value) => ValidateRunInputs();

    partial void OnRunDurationTextChanged(string value) => ValidateRunInputs();

    partial void OnMeasuredVolumeTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanAddRunPoint));
        AddRunPointCommand.NotifyCanExecuteChanged();
    }

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

        // Typing over the fitted coefficients breaks their provenance; the receipt must not
        // claim a fit that is no longer what is being applied.
        if (_fitBehindCoefficients is { } behind &&
            (!NearlyEqual(slope, behind.Slope) || !NearlyEqual(intercept, behind.Intercept)))
        {
            _fitBehindCoefficients = null;
        }

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void ValidateRunInputs()
    {
        if (!_initialised)
        {
            return;
        }

        if (!TryParseRunInputs(out _, out _, out var error))
        {
            RunValidationError = error;
        }
        else
        {
            RunValidationError = null;
        }

        StartRunCommand.NotifyCanExecuteChanged();
    }

    private bool TryParseRunInputs(out double speed, out double seconds, out string? error)
    {
        speed = 0;
        seconds = 0;
        if (!DosingInput.TryParseDouble(RunSpeedText, out speed) || speed < 1.0 || speed > 1000.0)
        {
            error = "Velocidade S: inteiro entre 1 e 1000 (unidade interna da bomba).";
            return false;
        }

        if (!DosingInput.TryParseDouble(RunDurationText, out seconds) || seconds < MinRunSeconds || seconds > MaxRunSeconds)
        {
            error = $"Duração: entre {MinRunSeconds:F0} e {MaxRunSeconds:F0} s.";
            return false;
        }

        speed = Math.Round(speed);
        error = null;
        return true;
    }

    private bool TryParseVolume(out double volumeMl)
        => DosingInput.TryParseDouble(MeasuredVolumeText, out volumeMl) && volumeMl > 0.0 && double.IsFinite(volumeMl);

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

    // ------------------------------------------------------------------
    // Volumetric runs
    // ------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private void StartRun()
    {
        if (!TryParseRunInputs(out var speed, out var seconds, out var error))
        {
            StatusText = error ?? "Parâmetros do acionamento inválidos.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed((int)speed));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _runStartedAt = _timeProvider.GetUtcNow();
        _runSpeed = speed;
        _runPlannedSeconds = seconds;
        _lastRun = null;
        HasPendingRun = false;
        RunSummaryText = "";
        MeasuredVolumeText = "";
        RunProgressPercent = 0;
        RunCountdownText = $"{seconds.ToString("F0", CultureInfo.CurrentCulture)} s restantes";
        IsRunning = true;
        StatusText = $"Bomba a S = {speed:F0} por {seconds:F0} s. A parada é automática; recolha o volume no recipiente graduado.";
        NotifyCommandAvailability();
        EnsureRunTimer().Start();
    }

    [RelayCommand(CanExecute = nameof(CanAbortRun))]
    private void AbortRun() => FinishRun(aborted: true);

    /// <summary>
    /// Advances the run clock. The dispatcher timer calls it every 250 ms while a run is on;
    /// tests call it directly after moving their <see cref="TimeProvider"/>.
    /// </summary>
    public void Tick()
    {
        if (!IsRunning)
        {
            return;
        }

        var elapsed = (_timeProvider.GetUtcNow() - _runStartedAt).TotalSeconds;
        RunProgressPercent = Math.Clamp(elapsed / _runPlannedSeconds * 100.0, 0.0, 100.0);
        RunCountdownText = $"{Math.Max(0.0, _runPlannedSeconds - elapsed).ToString("F0", CultureInfo.CurrentCulture)} s restantes";

        if (elapsed >= _runPlannedSeconds)
        {
            FinishRun(aborted: false);
        }
    }

    private void FinishRun(bool aborted)
    {
        if (!IsRunning)
        {
            return;
        }

        // The stop frame is what ends the run. If the arbiter refuses it (something else took
        // the pump meanwhile) the run stays open and the next tick tries again - a refused stop
        // must never be reported as a stop.
        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        if (!result.Accepted)
        {
            StatusText = "PARADA RECUSADA - a bomba pode continuar girando: " + DispatchRefusal.Describe(result);
            return;
        }

        var elapsed = (_timeProvider.GetUtcNow() - _runStartedAt).TotalSeconds;
        _runTimer?.Stop();
        IsRunning = false;
        RunProgressPercent = aborted ? RunProgressPercent : 100.0;
        RunCountdownText = "";

        if (aborted)
        {
            _lastRun = null;
            HasPendingRun = false;
            RunSummaryText = "";
            StatusText = $"Acionamento abortado após {elapsed.ToString("F1", CultureInfo.CurrentCulture)} s; nenhum ponto foi criado.";
        }
        else
        {
            _lastRun = (_runSpeed, elapsed);
            RunSummaryText = $"S {_runSpeed:F0} durante {elapsed.ToString("F1", CultureInfo.CurrentCulture)} s";
            HasPendingRun = true;
            StatusText = $"Bomba parada após {elapsed.ToString("F1", CultureInfo.CurrentCulture)} s. Leia o volume no recipiente graduado e informe abaixo.";
        }

        NotifyCommandAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanAddRunPoint))]
    private void AddRunPoint()
    {
        if (_lastRun is not { } run || !TryParseVolume(out var volume))
        {
            StatusText = "Informe o volume coletado (mL, maior que zero) do último acionamento.";
            return;
        }

        var point = new PumpCalibrationPoint
        {
            SpeedUnits = run.Speed,
            Seconds = run.Seconds,
            VolumeMl = volume,
            CapturedAtUtc = _timeProvider.GetUtcNow().ToString("o"),
        };
        Runs.Add(new PumpCalibrationRunViewModel(point));
        _lastRun = null;
        HasPendingRun = false;
        RunSummaryText = "";
        MeasuredVolumeText = "";
        PersistRuns();
        RecomputeFit();
        StatusText = $"Ponto registrado: S {point.SpeedUnits:F0} → {point.FlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture)} mL/min " +
                     $"({point.VolumeMl.ToString("F1", CultureInfo.CurrentCulture)} mL em {point.Seconds.ToString("F1", CultureInfo.CurrentCulture)} s).";
        NotifyCommandAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanEditRuns))]
    private void RemoveRun(PumpCalibrationRunViewModel? run)
    {
        if (run is null || !Runs.Remove(run))
        {
            return;
        }

        PersistRuns();
        RecomputeFit();
        StatusText = "Ponto removido.";
    }

    [RelayCommand(CanExecute = nameof(CanEditRuns))]
    private void ClearRuns()
    {
        if (Runs.Count == 0)
        {
            return;
        }

        Runs.Clear();
        PersistRuns();
        RecomputeFit();
        StatusText = "Todos os acionamentos foram removidos.";
    }

    [RelayCommand(CanExecute = nameof(CanUseFit))]
    private void UseFit()
    {
        if (_fit is not { } fit)
        {
            return;
        }

        SlopeText = DosingInput.Format(fit.Slope, 4);
        InterceptText = DosingInput.Format(fit.Intercept, 4);
        _fitBehindCoefficients = fit;
        StatusText = $"Coeficientes do ajuste copiados (n = {fit.Count}, R² = {FormatR2(fit.RSquared)}). " +
                     "Confira a pré-visualização e use \"Aplicar calibração\" para enviar ao nó.";
    }

    private void RecomputeFit()
    {
        _fit = PumpLinearFit.TryFit(Runs.Select(r => r.Point).ToList());
        foreach (var run in Runs)
        {
            run.Residual = _fit is { } f ? run.FlowMlPerMin - f.Predict(run.SpeedUnits) : null;
        }

        if (_fit is not { } fit)
        {
            HasFit = false;
            FitSlopeText = FitInterceptText = FitRSquaredText = "—";
            FitWarning = Runs.Count switch
            {
                0 => null,
                1 => "São necessários ao menos dois acionamentos em velocidades diferentes.",
                _ => "Os acionamentos estão todos na mesma velocidade; a inclinação não pode ser calculada.",
            };
            FitSummaryText = Runs.Count == 0
                ? "Nenhum acionamento registrado."
                : $"{Runs.Count} acionamento(s); ajuste indisponível.";
            NotifyCommandAvailability();
            return;
        }

        HasFit = true;
        FitSlopeText = fit.Slope.ToString("F4", CultureInfo.CurrentCulture);
        FitInterceptText = fit.Intercept.ToString("F4", CultureInfo.CurrentCulture);
        FitRSquaredText = FormatR2(fit.RSquared);
        FitWarning = fit.Slope <= 0.0
            ? "A inclinação ajustada não é positiva; verifique os volumes informados."
            : null;
        FitSummaryText = $"Q = {FitSlopeText} · S + {FitInterceptText} (n = {fit.Count}, S de {fit.MinSpeed:F0} a {fit.MaxSpeed:F0})";
        NotifyCommandAvailability();
    }

    private static string FormatR2(double r2)
        => double.IsFinite(r2) ? r2.ToString("F4", CultureInfo.CurrentCulture) : "—";

    private void PersistRuns()
    {
        var points = Runs.Select(r => r.Point).ToArray();
        _settings.Update(s => s with { PumpControl = s.PumpControl with { CalibrationPoints = points } });
    }

    private DispatcherTimer EnsureRunTimer()
    {
        if (_runTimer is null)
        {
            _runTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RunTickInterval };
            _runTimer.Tick += (_, _) => Tick();
        }

        return _runTimer;
    }

    // ------------------------------------------------------------------
    // Receipt and telemetry
    // ------------------------------------------------------------------

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
                method = _fitBehindCoefficients is not null ? "volumetric_runs_least_squares" : "manual_coefficients",
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
                },
                fit = _fitBehindCoefficients is { } fit
                    ? new { slope = fit.Slope, intercept = fit.Intercept, rSquared = fit.RSquared, count = fit.Count, minSpeed = fit.MinSpeed, maxSpeed = fit.MaxSpeed }
                    : null,
                runs = Runs.Select(r => new
                {
                    speedUnits = r.SpeedUnits,
                    seconds = r.Seconds,
                    volumeMl = r.VolumeMl,
                    flowMlPerMin = r.FlowMlPerMin,
                    capturedAtUtc = r.Point.CapturedAtUtc,
                }).ToArray(),
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

        // Telemetry is the other clock: a frame arriving after the deadline ends the run even
        // if the dispatcher timer is late.
        Tick();

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
            if (IsRunning)
            {
                // No link, no stop frame. The node keeps S until someone tells it otherwise, so
                // the app owes it a stop the moment the link is back, and the operator has to
                // hear that the pump may still be turning.
                _runTimer?.Stop();
                IsRunning = false;
                _lastRun = null;
                HasPendingRun = false;
                RunSummaryText = "";
                RunCountdownText = "";
                _stopPendingOnReconnect = true;
                StatusText = "Conexão perdida durante o acionamento: a bomba pode continuar girando. " +
                             "A parada será enviada ao reconectar; se necessário, pare a bomba no próprio nó.";
            }

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
        }
        else if (_stopPendingOnReconnect)
        {
            _stopPendingOnReconnect = false;
            var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
            StatusText = result.Accepted
                ? "Reconectado: parada da bomba enviada após o acionamento interrompido."
                : "Reconectado, mas a parada da bomba foi recusada: " + DispatchRefusal.Describe(result);
        }

        NotifyCommandAvailability();
    }

    private void NotifyCommandAvailability()
    {
        OnPropertyChanged(nameof(IsAwaitingCalibration));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanResetVolume));
        OnPropertyChanged(nameof(CanStartRun));
        OnPropertyChanged(nameof(CanAbortRun));
        OnPropertyChanged(nameof(CanAddRunPoint));
        OnPropertyChanged(nameof(CanEditRuns));
        OnPropertyChanged(nameof(CanUseFit));
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ResetVolumeCommand.NotifyCanExecuteChanged();
        StartRunCommand.NotifyCanExecuteChanged();
        AbortRunCommand.NotifyCanExecuteChanged();
        AddRunPointCommand.NotifyCanExecuteChanged();
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
        UseFitCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (IsRunning)
        {
            // Best effort: the workspace is going away with the pump still at S.
            _runTimer?.Stop();
            IsRunning = false;
            _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        }

        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
    }
}

/// <summary>Ordinary least squares line <c>Q = Slope · S + Intercept</c> over volumetric runs.</summary>
public sealed record PumpLinearFit(double Slope, double Intercept, double RSquared, int Count, double MinSpeed, double MaxSpeed)
{
    public double Predict(double speedUnits) => (Slope * speedUnits) + Intercept;

    /// <summary>Null with fewer than two runs or when every run is at the same speed.</summary>
    public static PumpLinearFit? TryFit(IReadOnlyList<PumpCalibrationPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var n = points.Count;
        var meanS = points.Average(p => p.SpeedUnits);
        var meanQ = points.Average(p => p.FlowMlPerMin);
        var sxx = 0.0;
        var sxy = 0.0;
        var syy = 0.0;
        foreach (var p in points)
        {
            var dx = p.SpeedUnits - meanS;
            var dy = p.FlowMlPerMin - meanQ;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx <= 1e-9)
        {
            return null;
        }

        var slope = sxy / sxx;
        var intercept = meanQ - (slope * meanS);
        var r2 = syy <= 1e-12 ? 1.0 : (sxy * sxy) / (sxx * syy);
        if (!double.IsFinite(slope) || !double.IsFinite(intercept))
        {
            return null;
        }

        return new PumpLinearFit(slope, intercept, r2, n, points.Min(p => p.SpeedUnits), points.Max(p => p.SpeedUnits));
    }
}
