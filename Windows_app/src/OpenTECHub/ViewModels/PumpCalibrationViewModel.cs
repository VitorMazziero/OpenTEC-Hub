using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>One volumetric run in the table: S held for a measured time, V collected, Q derived.</summary>
public sealed partial class PumpCalibrationRunViewModel : ObservableObject
{
    private readonly string? _capturedAtUtc;

    public PumpCalibrationRunViewModel(PumpCalibrationPoint? point = null)
    {
        _capturedAtUtc = point?.CapturedAtUtc;
        if (point is { } stored)
        {
            SpeedText = stored.SpeedUnits.ToString("F0", CultureInfo.CurrentCulture);
            SecondsText = stored.Seconds.ToString("F1", CultureInfo.CurrentCulture);
            VolumeText = stored.VolumeMl.ToString("F1", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The current table row projected back to the persisted point contract.</summary>
    public PumpCalibrationPoint Point => new()
    {
        SpeedUnits = SpeedUnits,
        Seconds = Seconds,
        VolumeMl = VolumeMl,
        CapturedAtUtc = _capturedAtUtc,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedUnits))]
    [NotifyPropertyChangedFor(nameof(Point))]
    [NotifyPropertyChangedFor(nameof(IsValidInput))]
    [NotifyPropertyChangedFor(nameof(SegmentLabel))]
    public partial string SpeedText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Seconds))]
    [NotifyPropertyChangedFor(nameof(FlowMlPerMin))]
    [NotifyPropertyChangedFor(nameof(FlowText))]
    [NotifyPropertyChangedFor(nameof(FlowValueText))]
    [NotifyPropertyChangedFor(nameof(Point))]
    [NotifyPropertyChangedFor(nameof(IsValidInput))]
    public partial string SecondsText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeMl))]
    [NotifyPropertyChangedFor(nameof(FlowMlPerMin))]
    [NotifyPropertyChangedFor(nameof(FlowText))]
    [NotifyPropertyChangedFor(nameof(FlowValueText))]
    [NotifyPropertyChangedFor(nameof(Point))]
    [NotifyPropertyChangedFor(nameof(IsValidInput))]
    public partial string VolumeText { get; set; } = "";

    public double SpeedUnits => Parse(SpeedText);

    public double Seconds => Parse(SecondsText);

    public double VolumeMl => Parse(VolumeText);

    public double FlowMlPerMin => double.IsFinite(Seconds) && Seconds > 0.0 &&
                                  double.IsFinite(VolumeMl) && VolumeMl >= 0.0
        ? VolumeMl / (Seconds / 60.0)
        : double.NaN;

    public bool IsValidInput => double.IsFinite(SpeedUnits) && SpeedUnits >= 1.0 && SpeedUnits <= 1000.0 &&
                                double.IsFinite(Seconds) && Seconds > 0.0 &&
                                double.IsFinite(VolumeMl) && VolumeMl >= 0.0 &&
                                double.IsFinite(FlowMlPerMin);

    public string FlowText => double.IsFinite(FlowMlPerMin)
        ? FlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture) + " mL/min"
        : "Vazão indisponível";

    public string FlowValueText => double.IsFinite(FlowMlPerMin)
        ? FlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture)
        : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResidualText))]
    public partial double? Residual { get; set; }

    public string ResidualText => Residual is { } r
        ? (r >= 0 ? "+" : "") + r.ToString("F2", CultureInfo.CurrentCulture)
        : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentLabel))]
    public partial bool IsLowSegment { get; set; }

    public string SegmentLabel => !double.IsFinite(SpeedUnits) ? "—" : IsLowSegment ? "Baixo" : "Alto";

    private static double Parse(string? text)
        => DosingInput.TryParseDouble(text, out var value) ? value : double.NaN;
}

/// <summary>
/// Polynomial calibration of the external peristaltic pump, using the same
/// quartic/quadratic C0+C1 curve family as the flowmeter and one local profile
/// for each hose.
/// </summary>
public sealed partial class PumpCalibrationViewModel : ObservableObject, IDisposable
{
    public const double MinRunSeconds = 5.0;
    public const double MaxRunSeconds = 600.0;
    public const double RunDeadlineMarginSeconds = 3.0;
    private static readonly TimeSpan RunTickInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string _calibrationsDirectory;
    private readonly IPumpCalibrationProfileStore _profileStore;
    private readonly IDialogService? _dialogs;
    private bool _initialised;
    private bool _suppressRecalculate;

    private DateTime? _resetVolumeRequestedAt;
    private bool _awaitingResetVolume;
    private DateTime? _calibrationRequestedAt;
    private PumpDualRangeCurve? _requestedCalibration;
    private bool _isPumpCommandPending;
    private bool _isPumpProfileActive;
    private bool _isPumpProfileWaiting;

    // ---- volumetric run state ----
    private DispatcherTimer? _runTimer;
    private DateTimeOffset _runStartedAt;
    private double _runSpeed;
    private double _runPlannedSeconds;
    private (double Speed, double Seconds)? _lastRun;
    private bool _stopPendingOnReconnect;

    // ---- dual range fit state ----
    private PumpFitResult? _fitResult;
    private PumpDualRangeCurve? _curve;

    public event Action? CurveChanged;

    public PumpCalibrationViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null,
        string? calibrationsDirectory = null,
        IPumpCalibrationProfileStore? profileStore = null,
        IDialogService? dialogs = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _calibrationsDirectory = calibrationsDirectory ?? AppPaths.CalibrationsDirectory;
        _profileStore = profileStore ?? new PumpCalibrationProfileStore(AppPaths.PumpProfilesDirectory);
        _dialogs = dialogs;

        RefreshProfiles();
        var activeName = _settings.Current.PumpControl.SelectedProfileName;
        if (!string.IsNullOrWhiteSpace(activeName) && _profileStore.ProfileExists(activeName))
        {
            LoadProfileData(activeName, saveAsSelected: false);
        }
        else if (Profiles.FirstOrDefault() is { } firstProfile)
        {
            LoadProfileData(firstProfile.Name, saveAsSelected: false);
        }

        IsConnected = _device.State == ConnectionState.Connected;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;

        _initialised = true;
        ValidateTransitionSpeedAndRecalculate();
        ValidateRunInputs();
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

    // ---- Dual-range curve inputs & outputs ----

    [ObservableProperty]
    public partial string TransitionSpeedInputText { get; set; } = "500.0";

    [ObservableProperty]
    public partial double TransitionSpeedInput { get; set; } = 500.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransitionSpeedInputValid))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string? TransitionSpeedInputError { get; set; }

    public bool IsTransitionSpeedInputValid => TransitionSpeedInputError is null;

    [ObservableProperty]
    public partial string TransitionSpeedText { get; set; } = "—";

    [ObservableProperty]
    public partial string TransitionFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string LowSlopeText { get; set; } = "—";

    [ObservableProperty]
    public partial string HighSlopeText { get; set; } = "—";

    [ObservableProperty]
    public partial string ContinuityText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitSummaryText { get; set; } = "Nenhum ponto volumétrico registrado.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string? FitWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool HasFit { get; set; }

    [ObservableProperty]
    public partial string FitRSquaredText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitRmseText { get; set; } = "—";

    [ObservableProperty]
    public partial string FitSseText { get; set; } = "—";

    [ObservableProperty]
    public partial string LowSegmentSummaryText { get; set; } = "—";

    [ObservableProperty]
    public partial string HighSegmentSummaryText { get; set; } = "—";

    [ObservableProperty]
    public partial int LowPointCount { get; set; }

    [ObservableProperty]
    public partial int HighPointCount { get; set; }

    [ObservableProperty]
    public partial string PointDistributionText { get; set; } = "0 pontos";

    // ---- Hardware echo readouts ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHardwareEchoText))]
    public partial string AppliedLowSlopeText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHardwareEchoText))]
    public partial string AppliedHighSlopeText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHardwareEchoText))]
    public partial string AppliedTransitionSpeedText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHardwareEchoText))]
    public partial string AppliedTransitionFlowText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHardwareEchoText))]
    public partial string AppliedCrcText { get; set; } = "—";

    public string AppliedHardwareEchoText =>
        $"Eco no nó: baixa {AppliedLowSlopeText} · alta {AppliedHighSlopeText} · " +
        $"St {AppliedTransitionSpeedText} · Qt {AppliedTransitionFlowText} · CRC {AppliedCrcText}";

    [ObservableProperty]
    public partial string CurrentFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentVolumeText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Registre os pontos da mangueira para calcular sua curva polinomial ou selecione um perfil salvo.";

    // ---- Hose Profile Library ----

    public ObservableCollection<PumpCalibrationProfileSummary> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadProfile))]
    [NotifyPropertyChangedFor(nameof(CanDeleteProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileStatusText))]
    public partial PumpCalibrationProfileSummary? SelectedProfileSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileStatusText))]
    public partial string ActiveProfileName { get; set; } = "Nova mangueira";

    [ObservableProperty]
    public partial string ActiveProfileId { get; set; } = "";

    [ObservableProperty]
    public partial PumpCalibrationProfile? ActiveProfile { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string ProfileNotes { get; set; } = "";

    public string AppliedStText => AppliedTransitionSpeedText;
    public string AppliedQtText => AppliedTransitionFlowText;

    public ObservableCollection<PumpCalibrationProfileSummary> AvailableProfiles => Profiles;

    public PumpCalibrationProfileSummary? SelectedProfile
    {
        get => SelectedProfileSummary;
        set => SelectedProfileSummary = value;
    }

    public string ProfileStatusText => IsDirty
        ? $"Perfil ativo: {ActiveProfileName} (alterações não salvas)"
        : $"Perfil ativo: {ActiveProfileName}";

    public bool IsCurrentProfileDirty => IsDirty;

    public IRelayCommand LoadSelectedProfileCommand => LoadProfileCommand;
    public IRelayCommand SaveCurrentProfileCommand => SaveProfileCommand;
    public IRelayCommand SaveCurrentProfileAsCommand => SaveProfileAsCommand;
    public IRelayCommand DeleteSelectedProfileCommand => DeleteProfileCommand;

    public bool CanLoadProfile => SelectedProfileSummary != null && !IsRunning && !IsManualRunning && !IsAwaitingCalibration;

    public bool CanDeleteProfile => SelectedProfileSummary != null && !IsRunning && !IsManualRunning && !IsAwaitingCalibration;

    // ---- Volumetric run: inputs ----

    [ObservableProperty]
    public partial string RunSpeedText { get; set; } = "500";

    [ObservableProperty]
    public partial string RunDurationText { get; set; } = "60";

    [ObservableProperty]
    public partial string MeasuredVolumeText { get; set; } = "";

    [ObservableProperty]
    public partial string ManualSpeedText { get; set; } = "250";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartManual))]
    [NotifyPropertyChangedFor(nameof(CanStopManual))]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanResetVolume))]
    public partial bool IsManualRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartManual))]
    public partial string? ManualValidationError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartRun))]
    public partial string? RunValidationError { get; set; }

    // ---- Volumetric run: progress ----

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddRunPoint))]
    public partial bool HasPendingRun { get; set; }

    [ObservableProperty]
    public partial string RunSummaryText { get; set; } = "";

    public ObservableCollection<PumpCalibrationRunViewModel> Runs { get; } = [];

    // ---- Operational guards ----

    public bool IsAwaitingCalibration => _requestedCalibration.HasValue;

    public bool CanEditCalibration => !IsAwaitingCalibration && !_isPumpCommandPending && !IsManualRunning && !IsRunning;

    public string? ValidationError
    {
        get
        {
            if (TransitionSpeedInputError != null)
            {
                return TransitionSpeedInputError;
            }
            if (Runs.Count > 0 && !HasFit)
            {
                return FitWarning ?? "Os pontos atuais não produzem um ajuste válido em duas faixas.";
            }
            if (_curve is not { } c)
            {
                return "Nenhuma curva de calibração definida.";
            }
            if (!NearlyEqual(c.TransitionSpeed, TransitionSpeedInput, 1e-9))
            {
                return "A velocidade de transição editada ainda não possui um ajuste válido.";
            }
            if (!c.Validate(out var err))
            {
                return err;
            }
            return null;
        }
    }

    public bool IsValid => ValidationError is null;

    public string Preview250Text => _curve is { } c ? c.FlowFromSpeed(250.0).ToString("F2", CultureInfo.CurrentCulture) + " mL/min" : "—";
    public string Preview500Text => _curve is { } c ? c.FlowFromSpeed(500.0).ToString("F2", CultureInfo.CurrentCulture) + " mL/min" : "—";
    public string Preview1000Text => _curve is { } c ? c.FlowFromSpeed(1000.0).ToString("F2", CultureInfo.CurrentCulture) + " mL/min" : "—";

    public bool CanApply => IsConnected && IsPumpOnline && IsValid &&
                            !_isPumpProfileActive && !_isPumpProfileWaiting && CanEditCalibration &&
                            !_awaitingResetVolume && !IsRunning && !IsManualRunning;

    public bool CanResetVolume => IsConnected && IsPumpOnline && !_awaitingResetVolume && !IsAwaitingCalibration &&
                                  !_isPumpCommandPending && !IsRunning && !IsManualRunning;

    public bool CanStartRun => IsConnected && IsPumpOnline && !IsRunning && !IsManualRunning && RunValidationError is null &&
                               !_isPumpCommandPending && !IsAwaitingCalibration && !_awaitingResetVolume;

    public bool CanAbortRun => IsRunning;

    public bool CanAddRunPoint => HasPendingRun && !IsRunning && TryParseVolume(out _);

    public bool CanEditRuns => !IsRunning;

    public bool CanStartManual => IsConnected && IsPumpOnline && !IsRunning && !IsManualRunning &&
                                  ManualValidationError is null && !_isPumpCommandPending &&
                                  !IsAwaitingCalibration && !_awaitingResetVolume;

    public bool CanStopManual => IsManualRunning;

    public PumpDualRangeCurve? Curve => _curve;

    public PumpFitResult? FitResult => _fitResult;

    public bool TryGetDisplayedCurve(out PumpDualRangeCurve curve)
    {
        if (_curve is { } c && c.Validate(out _))
        {
            curve = c;
            return true;
        }
        curve = default;
        return false;
    }

    public bool TryGetDisplayedCurve(out double lowSlope, out double highSlope, out double transitionSpeed, out double transitionFlow)
    {
        if (TryGetDisplayedCurve(out var c))
        {
            lowSlope = c.LowSlope;
            highSlope = c.HighSlope;
            transitionSpeed = c.TransitionSpeed;
            transitionFlow = c.TransitionFlow;
            return true;
        }
        lowSlope = highSlope = transitionSpeed = transitionFlow = 0.0;
        return false;
    }

    public bool TryGetDisplayedCurve(out double slope, out double intercept)
    {
        if (TryGetDisplayedCurve(out var c))
        {
            slope = c.LowSlope;
            intercept = c.TransitionFlow - (c.LowSlope * c.TransitionSpeed);
            return true;
        }
        slope = intercept = 0.0;
        return false;
    }

    partial void OnTransitionSpeedInputTextChanged(string value) => ValidateTransitionSpeedAndRecalculate();

    partial void OnRunSpeedTextChanged(string value) => ValidateRunInputs();

    partial void OnRunDurationTextChanged(string value) => ValidateRunInputs();

    partial void OnMeasuredVolumeTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanAddRunPoint));
        AddRunPointCommand.NotifyCanExecuteChanged();
    }

    partial void OnManualSpeedTextChanged(string value)
    {
        ManualValidationError = TryParseSpeed(value, out _)
            ? null
            : "Velocidade manual: inteiro entre 1 e 1000.";
        StartManualCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProfileSummaryChanged(PumpCalibrationProfileSummary? value)
    {
        NotifyCommandAvailability();
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRecalculate || e.PropertyName is not (nameof(PumpCalibrationRunViewModel.SpeedText) or
            nameof(PumpCalibrationRunViewModel.SecondsText) or nameof(PumpCalibrationRunViewModel.VolumeText)))
        {
            return;
        }

        IsDirty = true;
        RecomputeFit();
    }

    partial void OnProfileNotesChanged(string value)
    {
        if (_initialised)
        {
            IsDirty = true;
        }
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(ProfileStatusText));
        OnPropertyChanged(nameof(IsCurrentProfileDirty));
    }

    private void ValidateTransitionSpeedAndRecalculate()
    {
        if (!_initialised || _suppressRecalculate)
        {
            return;
        }

        if (!DosingInput.TryParseDouble(TransitionSpeedInputText, out var transitionSpeed) || transitionSpeed <= 0.0 || transitionSpeed >= 1000.0 || !double.IsFinite(transitionSpeed))
        {
            TransitionSpeedInputError = "Velocidade de transição (St): número real em (0, 1000).";
            RecomputeFit();
            return;
        }

        TransitionSpeedInputError = null;
        TransitionSpeedInput = transitionSpeed;
        IsDirty = true;
        RecomputeFit();
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

    private static bool TryParseSpeed(string text, out int speed)
    {
        speed = 0;
        if (!DosingInput.TryParseDouble(text, out var parsed) || parsed < 1.0 || parsed > 1000.0)
        {
            return false;
        }

        speed = (int)Math.Round(parsed);
        return true;
    }

    // ------------------------------------------------------------------
    // Profile Management
    // ------------------------------------------------------------------

    public void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var summary in _profileStore.ListProfiles())
        {
            Profiles.Add(summary);
        }
        SelectedProfileSummary = Profiles.FirstOrDefault(p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                                 ?? Profiles.FirstOrDefault();
        NotifyCommandAvailability();
    }

    private void AddRun(PumpCalibrationRunViewModel run)
    {
        run.PropertyChanged += OnRunChanged;
        Runs.Add(run);
    }

    private void ClearRunRows()
    {
        foreach (var run in Runs)
        {
            run.PropertyChanged -= OnRunChanged;
        }

        Runs.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    public void NewProfile()
    {
        ClearRunRows();
        SelectedProfileSummary = null;
        ActiveProfile = null;
        ActiveProfileId = "";
        ActiveProfileName = "Nova mangueira";
        ProfileNotes = "";
        IsDirty = false;
        RecomputeFit();
        StatusText = "Novo perfil iniciado. Adicione ou edite pontos volumétricos e salve como perfil quando o ajuste for válido.";
        NotifyCommandAvailability();
    }

    private void LoadProfileData(string name, bool saveAsSelected = true)
    {
        PumpCalibrationProfile? profile;
        try
        {
            profile = _profileStore.LoadProfile(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Não foi possível carregar o perfil '{name}': {ex.Message}";
            return;
        }
        if (profile is null)
        {
            StatusText = $"O perfil '{name}' não está disponível.";
            return;
        }

        var profileCurve = profile.ToCurve();
        if (!profileCurve.Validate(out var curveError))
        {
            StatusText = $"O perfil '{name}' contém uma curva inválida: {curveError}";
            return;
        }

        _suppressRecalculate = true;
        try
        {
            ActiveProfile = profile;
            ActiveProfileName = profile.Name;
            ActiveProfileId = profile.ProfileId;
            ProfileNotes = profile.OptionalNotes ?? "";
            TransitionSpeedInput = profile.TransitionSpeedUnits;
            TransitionSpeedInputError = null;
            TransitionSpeedInputText = DosingInput.Format(profile.TransitionSpeedUnits, 1);
            _curve = profileCurve;

            ClearRunRows();
            foreach (var pt in profile.CalibrationPoints)
            {
                AddRun(new PumpCalibrationRunViewModel(pt));
            }
        }
        finally
        {
            _suppressRecalculate = false;
        }

        if (saveAsSelected)
        {
            _settings.Update(s => s with
            {
                PumpControl = s.PumpControl with
                {
                    SelectedProfileName = profile.Name,
                    CalibrationPoints = profile.CalibrationPoints
                }
            });
        }

        RecomputeFit();
        IsDirty = false;
    }

    [RelayCommand(CanExecute = nameof(CanLoadProfile))]
    public void LoadProfile()
    {
        if (SelectedProfileSummary is not { } selected)
        {
            return;
        }

        if (IsDirty)
        {
            if (_dialogs is null)
            {
                StatusText = "Existem alterações não salvas; não foi possível confirmar o descarte sem o serviço de diálogo.";
                return;
            }
            var confirm = _dialogs.Confirm(
                "Descartar alterações",
                $"Existem alterações não salvas no perfil '{ActiveProfileName}'. Deseja descartá-las e carregar '{selected.Name}'?",
                confirmText: "Descartar e carregar",
                isDanger: true);
            if (!confirm)
            {
                return;
            }
        }

        LoadProfileData(selected.Name, saveAsSelected: true);
        StatusText = $"Perfil '{selected.Name}' carregado.";
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    public void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ActiveProfileName))
        {
            SaveProfileAs();
            return;
        }

        DoSaveProfile(ActiveProfileName);
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    public void SaveProfileAs()
    {
        if (_dialogs is null)
        {
            StatusText = "Serviço de diálogo indisponível para salvar como.";
            return;
        }

        if (!_dialogs.PromptInput("Salvar perfil de mangueira", "Nome do novo perfil:", out var newName, initialValue: ActiveProfileName))
        {
            return;
        }

        if (!PumpProfileFileContracts.ValidateProfileName(newName, out var error))
        {
            StatusText = $"Nome de perfil inválido: {error}";
            return;
        }

        newName = newName.Trim();
        if (_profileStore.ProfileExists(newName))
        {
            var confirm = _dialogs.Confirm(
                "Sobrescrever perfil",
                $"Já existe um perfil chamado '{newName}'. Deseja sobrescrevê-lo?",
                confirmText: "Sobrescrever",
                isDanger: true);
            if (!confirm)
            {
                return;
            }
        }

        DoSaveProfile(newName);
    }

    private void DoSaveProfile(string name)
    {
        string? curveError = null;
        if (_curve is not { } validCurve || !validCurve.Validate(out curveError))
        {
            StatusText = curveError ?? "Não há curva válida para salvar no perfil.";
            return;
        }

        if (Runs.Count > 0 && !HasFit)
        {
            StatusText = FitWarning ?? "Os pontos atuais não produzem um ajuste válido; o perfil não foi salvo.";
            return;
        }

        if (Runs.Any(run => !run.IsValidInput))
        {
            StatusText = "Há pontos incompletos ou inválidos. Preencha S, tempo e volume antes de salvar o perfil.";
            return;
        }

        var points = Runs.Select(r => r.Point).ToArray();
        var currentCurve = validCurve;

        PumpFitStatistics? fitStats = null;
        if (_fitResult is { IsValid: true })
        {
            fitStats = new PumpFitStatistics
            {
                RSquared = _fitResult.RSquared,
                RMSE = _fitResult.RMSE,
                SSE = _fitResult.SSE,
                LowRMSE = _fitResult.LowRMSE,
                LowSSE = _fitResult.LowSSE,
                HighRMSE = _fitResult.HighRMSE,
                HighSSE = _fitResult.HighSSE,
                LowPointCount = _fitResult.LowPointCount,
                HighPointCount = _fitResult.HighPointCount
            };
        }

        var existing = _profileStore.ProfileExists(name) ? _profileStore.LoadProfile(name) : null;
        var profile = new PumpCalibrationProfile
        {
            SchemaVersion = PumpCalibrationProfile.CurrentSchemaVersion,
            ProfileId = existing?.ProfileId ?? Guid.NewGuid().ToString("D"),
            Name = name.Trim(),
            CreatedUtc = existing?.CreatedUtc ?? _timeProvider.GetUtcNow(),
            ModifiedUtc = _timeProvider.GetUtcNow(),
            TransitionFlowMlMin = currentCurve.TransitionFlow,
            TransitionSpeedUnits = currentCurve.TransitionSpeed,
            LowSlope = currentCurve.LowSlope,
            HighSlope = currentCurve.HighSlope,
            LowA = currentCurve.LowSpeed.A,
            LowB = currentCurve.LowSpeed.B,
            LowK = currentCurve.LowSpeed.K,
            LowF = currentCurve.LowSpeed.F,
            LowC = currentCurve.LowSpeed.C,
            HighK = currentCurve.HighSpeed.K,
            HighF = currentCurve.HighSpeed.F,
            HighC = currentCurve.HighSpeed.C,
            CalibrationPoints = points,
            FitStatistics = fitStats,
            AlgorithmVersion = "quartic-quadratic-c1-v2",
            OptionalNotes = ProfileNotes,
            LastAppliedUtc = existing?.LastAppliedUtc,
            LastAppliedPumpFirmware = existing?.LastAppliedPumpFirmware
        };

        _profileStore.SaveProfile(profile, overwrite: true);
        ActiveProfile = profile;
        ActiveProfileName = profile.Name;
        ActiveProfileId = profile.ProfileId;
        IsDirty = false;
        _settings.Update(s => s with { PumpControl = s.PumpControl with { SelectedProfileName = profile.Name, CalibrationPoints = points } });

        RefreshProfiles();
        StatusText = $"Perfil '{profile.Name}' salvo com sucesso ({points.Length} pontos).";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    public void DeleteProfile()
    {
        if (SelectedProfileSummary is not { } selected)
        {
            return;
        }

        if (_dialogs is not null)
        {
            var confirm = _dialogs.Confirm(
                "Excluir perfil",
                $"Tem certeza de que deseja excluir o perfil '{selected.Name}'? A curva ativa no equipamento não será alterada.",
                confirmText: "Excluir",
                isDanger: true);
            if (!confirm)
            {
                return;
            }
        }

        var success = _profileStore.DeleteProfile(selected.Name);
        if (!success)
        {
            StatusText = $"Não foi possível excluir o perfil '{selected.Name}'.";
            return;
        }

        StatusText = $"Perfil '{selected.Name}' excluído da biblioteca local. A calibração do nó permanece inalterada.";
        RefreshProfiles();

        if (string.Equals(ActiveProfileName, selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            var next = Profiles.FirstOrDefault();
            if (next is not null)
            {
                LoadProfileData(next.Name, saveAsSelected: true);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    public void SavePoints()
    {
        SaveProfile();
        StatusText = $"Pontos e transição salvos localmente no perfil '{ActiveProfileName}'.";
    }

    [RelayCommand(CanExecute = nameof(CanEditCalibration))]
    public void Revert()
    {
        if (!string.IsNullOrWhiteSpace(ActiveProfileName) && _profileStore.ProfileExists(ActiveProfileName))
        {
            LoadProfileData(ActiveProfileName, saveAsSelected: false);
            StatusText = $"Valores restaurados do perfil '{ActiveProfileName}'.";
        }
        else
        {
            ClearRunRows();
            _curve = null;
            ActiveProfile = null;
            ActiveProfileId = "";
            ActiveProfileName = "Nova mangueira";
            RecomputeFit();
            StatusText = "Formulário reiniciado para uma nova mangueira.";
        }
    }

    // ------------------------------------------------------------------
    // Dual-range Fit
    // ------------------------------------------------------------------

    private void RecomputeFit()
    {
        var points = Runs.Select(r => r.Point).ToList();

        if (points.Count == 0)
        {
            _fitResult = null;
            _curve = null;
            HasFit = false;
            FitSummaryText = "Nenhum ponto volumétrico registrado.";
            FitWarning = null;
            TransitionSpeedText = "—";
            TransitionFlowText = "—";
            LowSlopeText = "—";
            HighSlopeText = "—";
            ContinuityText = "—";
            FitRSquaredText = "—";
            FitRmseText = "—";
            FitSseText = "—";
            LowSegmentSummaryText = "—";
            HighSegmentSummaryText = "—";
            PointDistributionText = "0 pontos";
            LowPointCount = 0;
            HighPointCount = 0;
            foreach (var run in Runs)
            {
                run.Residual = null;
                run.IsLowSegment = false;
            }
            NotifyCommandAvailability();
            CurveChanged?.Invoke();
            return;
        }

        if (!IsTransitionSpeedInputValid || TransitionSpeedInput <= 0.0)
        {
            FitWarning = TransitionSpeedInputError ?? "Velocidade de transição (St) inválida.";
            NotifyCommandAvailability();
            CurveChanged?.Invoke();
            return;
        }

        _fitResult = PumpDualRangeMath.FitDualRange(points, TransitionSpeedInput);

        if (!_fitResult.IsValid || _fitResult.Curve is not { } curve)
        {
            _curve = null;
            HasFit = false;
            FitWarning = _fitResult.Error ?? "Ajuste de calibração dupla indisponível para os pontos atuais.";
            FitSummaryText = $"{points.Count} acionamento(s); ajuste indisponível.";
            TransitionSpeedText = "—";
            TransitionFlowText = "—";
            LowSlopeText = "—";
            HighSlopeText = "—";
            ContinuityText = "—";
            FitRSquaredText = "—";
            FitRmseText = "—";
            FitSseText = "—";
            LowSegmentSummaryText = "—";
            HighSegmentSummaryText = "—";
            LowPointCount = points.Count(p => p.SpeedUnits <= TransitionSpeedInput);
            HighPointCount = points.Count(p => p.SpeedUnits > TransitionSpeedInput);
            PointDistributionText = $"Faixa baixa: {LowPointCount} pts | Faixa alta: {HighPointCount} pts";
            foreach (var run in Runs)
            {
                run.Residual = null;
                run.IsLowSegment = double.IsFinite(run.SpeedUnits) && run.SpeedUnits <= TransitionSpeedInput;
            }
            NotifyCommandAvailability();
            CurveChanged?.Invoke();
            return;
        }

        _curve = curve;
        HasFit = true;
        FitWarning = null;

        TransitionSpeedText = curve.TransitionSpeed.ToString("F1", CultureInfo.CurrentCulture) + " un";
        TransitionFlowText = curve.TransitionFlow.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";
        LowSlopeText = FormatLowEquation(curve.LowSpeed);
        HighSlopeText = FormatHighEquation(curve.HighSpeed);
        ContinuityText = $"C0+C1 em St = {curve.TransitionSpeed:F1} un (Qt = {curve.TransitionFlow:F2} mL/min)";
        FitSummaryText = $"S ≤ St: {FormatLowEquation(curve.LowSpeed)} | S > St: {FormatHighEquation(curve.HighSpeed)}";

        FitRSquaredText = double.IsFinite(_fitResult.RSquared) ? _fitResult.RSquared.ToString("F4", CultureInfo.CurrentCulture) : "—";
        FitRmseText = double.IsFinite(_fitResult.RMSE) ? _fitResult.RMSE.ToString("F3", CultureInfo.CurrentCulture) + " mL/min" : "—";
        FitSseText = double.IsFinite(_fitResult.SSE) ? _fitResult.SSE.ToString("F3", CultureInfo.CurrentCulture) + " mL/min" : "—";

        LowPointCount = _fitResult.LowPointCount;
        HighPointCount = _fitResult.HighPointCount;
        PointDistributionText = $"Faixa baixa: {LowPointCount} pts | Faixa alta: {HighPointCount} pts";

        LowSegmentSummaryText = $"Faixa baixa: n={LowPointCount}, RMSE={_fitResult.LowRMSE:F3}";
        HighSegmentSummaryText = $"Faixa alta: n={HighPointCount}, RMSE={_fitResult.HighRMSE:F3}";

        for (var i = 0; i < Runs.Count; i++)
        {
            var run = Runs[i];
            run.IsLowSegment = double.IsFinite(run.SpeedUnits) && run.SpeedUnits <= curve.TransitionSpeed;
            if (run.IsValidInput)
            {
                run.Residual = run.FlowMlPerMin - curve.FlowFromSpeed(run.SpeedUnits);
            }
            else
            {
                run.Residual = null;
            }
        }

        NotifyCommandAvailability();
        CurveChanged?.Invoke();
    }

    public PumpFitResult? Fit => _fitResult;

    public bool CanUseFit => HasFit && _fitResult?.Curve != null && CanEditCalibration;

    [RelayCommand(CanExecute = nameof(CanUseFit))]
    public void UseFit()
    {
        if (_fitResult?.Curve is not { } fitCurve)
        {
            return;
        }

        _suppressRecalculate = true;
        try
        {
            _curve = fitCurve;
            TransitionSpeedInput = fitCurve.TransitionSpeed;
            TransitionSpeedInputText = fitCurve.TransitionSpeed.ToString("F1", CultureInfo.InvariantCulture);
            LowSlopeText = FormatLowEquation(fitCurve.LowSpeed);
            HighSlopeText = FormatHighEquation(fitCurve.HighSpeed);
            TransitionSpeedText = fitCurve.TransitionSpeed.ToString("F1", CultureInfo.InvariantCulture);
            TransitionFlowText = fitCurve.TransitionFlow.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";
        }
        finally
        {
            _suppressRecalculate = false;
        }
        IsDirty = true;
        FitSummaryText = $"Ajuste copiado dos pontos: St={fitCurve.TransitionSpeed:F1}, Qt={fitCurve.TransitionFlow:F2} mL/min; {FormatLowEquation(fitCurve.LowSpeed)} | {FormatHighEquation(fitCurve.HighSpeed)}.";
        NotifyCommandAvailability();
        CurveChanged?.Invoke();
    }

    public void SetCurve(PumpDualRangeCurve curve)
    {
        _suppressRecalculate = true;
        try
        {
            _curve = curve;
            TransitionSpeedInput = curve.TransitionSpeed;
            TransitionSpeedInputError = null;
            TransitionSpeedInputText = curve.TransitionSpeed.ToString("F1", CultureInfo.InvariantCulture);
            LowSlopeText = FormatLowEquation(curve.LowSpeed);
            TransitionSpeedText = curve.TransitionSpeed.ToString("F1", CultureInfo.InvariantCulture);
            TransitionFlowText = curve.TransitionFlow.ToString("F2", CultureInfo.CurrentCulture) + " mL/min";
            HighSlopeText = FormatHighEquation(curve.HighSpeed);
            IsDirty = true;
        }
        finally
        {
            _suppressRecalculate = false;
        }
        NotifyCommandAvailability();
        CurveChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Apply (Send Dual-Range Curve)
    // ------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanApply))]
    public void Apply()
    {
        if (_isPumpProfileActive || _isPumpProfileWaiting)
        {
            StatusText = "Pare ou cancele o perfil operacional da bomba antes de alterar a calibração.";
            return;
        }

        if (_curve is null)
        {
            StatusText = "Nenhuma curva de calibração definida.";
            return;
        }

        var curve = _curve.Value;
        if (!curve.Validate(out var error))
        {
            StatusText = error ?? "Curva de calibração inválida para envio.";
            return;
        }

        var command = CommandBuilders.PumpDualRangeCalibration(
            curve.LowSpeed.A, curve.LowSpeed.B, curve.LowSpeed.K, curve.LowSpeed.F, curve.LowSpeed.C,
            curve.HighSpeed.K, curve.HighSpeed.F, curve.HighSpeed.C, curve.TransitionSpeed);

        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _requestedCalibration = curve;
        _calibrationRequestedAt = _timeProvider.GetUtcNow().UtcDateTime;
        NotifyCommandAvailability();
        StatusText = $"Calibração em duas faixas enviada (Qt: {curve.TransitionFlow:F2}, St: {curve.TransitionSpeed:F1}, " +
                     $"m_baixo: {curve.LowSlope:F4}, m_alto: {curve.HighSlope:F4}). Aguardando confirmação do nó...";
    }

    [RelayCommand(CanExecute = nameof(CanResetVolume))]
    public void ResetVolume()
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

    [RelayCommand(CanExecute = nameof(CanStartManual))]
    public void StartManual()
    {
        if (!TryParseSpeed(ManualSpeedText, out var speed))
        {
            ManualValidationError = "Velocidade manual: inteiro entre 1 e 1000.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(speed));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        IsManualRunning = true;
        StatusText = $"Controle manual ativo em S = {speed}. Pare assim que o líquido completar a mangueira.";
        NotifyCommandAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanStopManual))]
    public void StopManual()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        if (!result.Accepted)
        {
            StatusText = "PARADA RECUSADA - a bomba pode continuar girando: " + DispatchRefusal.Describe(result);
            return;
        }

        IsManualRunning = false;
        StatusText = "Controle manual encerrado; bomba parada.";
        NotifyCommandAvailability();
    }

    // ------------------------------------------------------------------
    // Volumetric Runs
    // ------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    public void StartRun()
    {
        if (!TryParseRunInputs(out var speed, out var seconds, out var error))
        {
            StatusText = error ?? "Parâmetros do acionamento inválidos.";
            return;
        }

        var deadlineMs = (int)Math.Ceiling((seconds + RunDeadlineMarginSeconds) * 1000.0);
        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed((int)speed, deadlineMs));
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
    public void AbortRun() => FinishRun(aborted: true);

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
    public void AddRunPoint()
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
        AddRun(new PumpCalibrationRunViewModel(point));
        _lastRun = null;
        HasPendingRun = false;
        RunSummaryText = "";
        MeasuredVolumeText = "";
        IsDirty = true;
        RecomputeFit();
        StatusText = $"Ponto registrado: S {point.SpeedUnits:F0} → {point.FlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture)} mL/min " +
                     $"({point.VolumeMl.ToString("F1", CultureInfo.CurrentCulture)} mL em {point.Seconds.ToString("F1", CultureInfo.CurrentCulture)} s).";
        NotifyCommandAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanEditRuns))]
    public void RemoveRun(PumpCalibrationRunViewModel? run)
    {
        if (run is null || !Runs.Remove(run))
        {
            return;
        }

        run.PropertyChanged -= OnRunChanged;
        IsDirty = true;
        RecomputeFit();
        StatusText = "Ponto removido.";
    }

    [RelayCommand(CanExecute = nameof(CanEditRuns))]
    public void AddEmptyRun()
    {
        var run = new PumpCalibrationRunViewModel();
        AddRun(run);
        IsDirty = true;
        RecomputeFit();
        StatusText = "Novo ponto criado. Informe velocidade S, tempo e volume coletado.";
        NotifyCommandAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanEditRuns))]
    public void ClearRuns()
    {
        if (Runs.Count == 0)
        {
            return;
        }

        ClearRunRows();
        IsDirty = true;
        RecomputeFit();
        StatusText = "Todos os acionamentos foram removidos.";
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
    // Telemetry and Receipts
    // ------------------------------------------------------------------

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        IsPumpOnline = snapshot.PumpOnline;
        _isPumpProfileActive = snapshot.PumpActive;
        _isPumpProfileWaiting = snapshot.PumpWaiting;
        if (snapshot.PumpCommandPending.HasValue)
        {
            _isPumpCommandPending = snapshot.PumpCommandPending.Value;
        }

        AppliedLowSlopeText = TryCurveFromSnapshot(snapshot, out var appliedCurve)
            ? FormatLowEquation(appliedCurve.LowSpeed) : "—";
        AppliedHighSlopeText = TryCurveFromSnapshot(snapshot, out appliedCurve)
            ? FormatHighEquation(appliedCurve.HighSpeed) : "—";
        AppliedTransitionSpeedText = snapshot.PumpTransitionSpeed is { } st && st > SensorReadings.NotReceived
            ? st.ToString("F1", CultureInfo.CurrentCulture) : "—";
        AppliedTransitionFlowText = TryCurveFromSnapshot(snapshot, out appliedCurve)
            ? appliedCurve.TransitionFlow.ToString("F2", CultureInfo.CurrentCulture) : "—";
        AppliedCrcText = snapshot.PumpCalCrc is { } crc
            ? $"0x{crc:X8}" : "—";

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

        Tick();
        NotifyCommandAvailability();
    }

    private void ConfirmCalibrationIfEchoed(SensorSnapshot snapshot)
    {
        if (_requestedCalibration is not { } requested)
        {
            return;
        }

        if (_calibrationRequestedAt.HasValue &&
            (_timeProvider.GetUtcNow().UtcDateTime - _calibrationRequestedAt.Value).TotalSeconds > 15.0)
        {
            _requestedCalibration = null;
            _calibrationRequestedAt = null;
            NotifyCommandAvailability();
            StatusText = "Aviso: a bomba não concluiu ACK, eco integral e CRC da calibração em 15 s; nenhum recibo foi gravado.";
            return;
        }

        // ACK without echo retains pending.
        if (!snapshot.PumpA1.HasValue && !snapshot.PumpK2.HasValue && !snapshot.PumpTransitionSpeed.HasValue)
        {
            return;
        }

        bool hasAllEchoes = TryCurveFromSnapshot(snapshot, out var appliedCurve) &&
                            snapshot.PumpCalCrc.HasValue && snapshot.PumpCommandPending == false;

        if (hasAllEchoes)
        {
            if (CurvesMatch(appliedCurve, requested))
            {
                _requestedCalibration = null;
                _calibrationRequestedAt = null;
                NotifyCommandAvailability();

                var now = _timeProvider.GetUtcNow();
                if (!string.IsNullOrWhiteSpace(ActiveProfileName) && _profileStore.ProfileExists(ActiveProfileName))
                {
                    var p = _profileStore.LoadProfile(ActiveProfileName);
                    if (p is not null && CurvesMatch(p.ToCurve(), requested))
                    {
                        var updated = p with
                        {
                            LastAppliedUtc = now,
                            LastAppliedPumpFirmware = snapshot.PumpNode.FirmwareVersion ?? "3.12"
                        };
                        _profileStore.SaveProfile(updated, overwrite: true);
                    }
                }

                _settings.Update(s => s with
                {
                    PumpControl = s.PumpControl with
                    {
                        CalibrationMLow = appliedCurve.LowSlope,
                        CalibrationMHigh = appliedCurve.HighSlope,
                        CalibrationSt = appliedCurve.TransitionSpeed,
                        CalibrationQt = appliedCurve.TransitionFlow,
                        CalibrationSlope = appliedCurve.LowSlope,
                        CalibrationPoints = Runs.Select(r => r.Point).ToArray(),
                        SelectedProfileName = ActiveProfileName
                    }
                });

                var receiptPath = WriteCalibrationReceipt(requested, appliedCurve, snapshot.PumpCalCrc, snapshot);

                var crcStr = snapshot.PumpCalCrc.HasValue ? $" (CRC: 0x{snapshot.PumpCalCrc.Value:X8})" : "";
                StatusText = receiptPath is not null
                    ? $"Calibração em duas faixas confirmada pelo nó{crcStr}. Recibo salvo em {Path.GetFileName(receiptPath)}."
                    : $"Calibração em duas faixas confirmada pelo nó{crcStr}.";

                RefreshProfiles();
                return;
            }
            else
            {
                StatusText = $"Aviso: o nó ecoou coeficientes divergentes dos solicitados " +
                             $"(St solicitado {requested.TransitionSpeed:F1}; ecoado {appliedCurve.TransitionSpeed:F1}).";
            }
        }

    }

    private static bool CurvesMatch(PumpDualRangeCurve curve, PumpDualRangeCurve requested) =>
        NearlyEqual(curve.LowSpeed.A, requested.LowSpeed.A, 1e-6) &&
        NearlyEqual(curve.LowSpeed.B, requested.LowSpeed.B, 1e-6) &&
        NearlyEqual(curve.LowSpeed.K, requested.LowSpeed.K, 1e-6) &&
        NearlyEqual(curve.LowSpeed.F, requested.LowSpeed.F, 1e-6) &&
        NearlyEqual(curve.LowSpeed.C, requested.LowSpeed.C, 1e-6) &&
        NearlyEqual(curve.HighSpeed.K, requested.HighSpeed.K, 1e-6) &&
        NearlyEqual(curve.HighSpeed.F, requested.HighSpeed.F, 1e-6) &&
        NearlyEqual(curve.HighSpeed.C, requested.HighSpeed.C, 1e-6) &&
        NearlyEqual(curve.TransitionSpeed, requested.TransitionSpeed, 0.05);

    private static bool TryCurveFromSnapshot(SensorSnapshot snapshot, out PumpDualRangeCurve curve)
    {
        curve = default;
        if (snapshot.PumpA1 is not { } a1 || snapshot.PumpB1 is not { } b1 ||
            snapshot.PumpK1 is not { } k1 || snapshot.PumpF1 is not { } f1 || snapshot.PumpC1 is not { } c1 ||
            snapshot.PumpK2 is not { } k2 || snapshot.PumpF2 is not { } f2 || snapshot.PumpC2 is not { } c2 ||
            snapshot.PumpTransitionSpeed is not { } st)
        {
            return false;
        }

        curve = new PumpDualRangeCurve(
            new PolynomialCalibration(k1, f1, c1) { A = a1, B = b1 },
            new PolynomialCalibration(k2, f2, c2), st);
        return curve.Validate(out _);
    }

    private static string FormatLowEquation(PolynomialCalibration curve) =>
        $"Q={curve.A:G5}S⁴ {curve.B:+0.#####;-0.#####;+0}S³ {curve.K:+0.#####;-0.#####;+0}S² {curve.F:+0.#####;-0.#####;+0}S {curve.C:+0.#####;-0.#####;+0}";

    private static string FormatHighEquation(PolynomialCalibration curve) =>
        $"Q={curve.K:G5}S² {curve.F:+0.#####;-0.#####;+0}S {curve.C:+0.#####;-0.#####;+0}";

    private string? WriteCalibrationReceipt(
        PumpDualRangeCurve requestedCurve,
        PumpDualRangeCurve appliedCurve,
        long? appliedCrc,
        SensorSnapshot snapshot)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var receipt = new
            {
                timestampUtc = now.ToString("o"),
                appVersion = typeof(PumpCalibrationViewModel).Assembly.GetName().Version?.ToString(),
                node = "external_pump",
                method = "quartic_quadratic_c0_c1",
                profile = new
                {
                    profileId = ActiveProfileId,
                    name = ActiveProfileName,
                    notes = ProfileNotes
                },
                requested = new
                {
                    a1 = requestedCurve.LowSpeed.A, b1 = requestedCurve.LowSpeed.B,
                    k1 = requestedCurve.LowSpeed.K, f1 = requestedCurve.LowSpeed.F, c1 = requestedCurve.LowSpeed.C,
                    k2 = requestedCurve.HighSpeed.K, f2 = requestedCurve.HighSpeed.F, c2 = requestedCurve.HighSpeed.C,
                    transitionSpeed = requestedCurve.TransitionSpeed,
                    transitionFlow = requestedCurve.TransitionFlow
                },
                applied = new
                {
                    a1 = appliedCurve.LowSpeed.A, b1 = appliedCurve.LowSpeed.B,
                    k1 = appliedCurve.LowSpeed.K, f1 = appliedCurve.LowSpeed.F, c1 = appliedCurve.LowSpeed.C,
                    k2 = appliedCurve.HighSpeed.K, f2 = appliedCurve.HighSpeed.F, c2 = appliedCurve.HighSpeed.C,
                    transitionSpeed = appliedCurve.TransitionSpeed,
                    transitionFlow = appliedCurve.TransitionFlow,
                    crc32 = appliedCrc.HasValue ? $"0x{appliedCrc.Value:X8}" : null,
                    crc32Decimal = appliedCrc
                },
                hubFirmwareVersion = snapshot.HubFirmwareVersion,
                pumpNode = new
                {
                    ip = snapshot.PumpNode.Ip,
                    mac = snapshot.PumpNode.Mac,
                    firmwareVersion = snapshot.PumpNode.FirmwareVersion
                },
                fit = _fitResult is { } fit ? new
                {
                    count = fit.TotalPoints,
                    rSquared = fit.RSquared,
                    rmse = fit.RMSE,
                    sse = fit.SSE,
                    lowRmse = fit.LowRMSE,
                    lowSse = fit.LowSSE,
                    highRmse = fit.HighRMSE,
                    highSse = fit.HighSSE,
                    totalPoints = fit.TotalPoints,
                    lowPointCount = fit.LowPointCount,
                    highPointCount = fit.HighPointCount
                } : null,
                runs = Runs.Select(r => new
                {
                    speedUnits = r.SpeedUnits,
                    seconds = r.Seconds,
                    volumeMl = r.VolumeMl,
                    flowMlPerMin = r.FlowMlPerMin,
                    capturedAtUtc = r.Point.CapturedAtUtc,
                    segment = r.SegmentLabel,
                    residual = r.Residual
                }).ToArray()
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

    private static bool NearlyEqual(double actual, double requested, double tolerance = 0.0001)
        => Math.Abs(actual - requested) <= tolerance;

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        IsConnected = change.State == ConnectionState.Connected;
        if (!IsConnected)
        {
            if (IsRunning || IsManualRunning)
            {
                _runTimer?.Stop();
                IsRunning = false;
                IsManualRunning = false;
                _lastRun = null;
                HasPendingRun = false;
                RunSummaryText = "";
                RunCountdownText = "";
                _stopPendingOnReconnect = true;
                StatusText = "Conexão perdida durante o acionamento: a bomba pode continuar girando. " +
                             "A parada será enviada ao reconectar; se necessário, pare a bomba no próprio nó.";
            }

            IsPumpOnline = false;
            AppliedLowSlopeText = "—";
            AppliedHighSlopeText = "—";
            AppliedTransitionSpeedText = "—";
            AppliedTransitionFlowText = "—";
            AppliedCrcText = "—";
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
        OnPropertyChanged(nameof(CanStartManual));
        OnPropertyChanged(nameof(CanStopManual));
        OnPropertyChanged(nameof(CanLoadProfile));
        OnPropertyChanged(nameof(CanDeleteProfile));

        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ResetVolumeCommand.NotifyCanExecuteChanged();
        SavePointsCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        SaveProfileAsCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        NewProfileCommand.NotifyCanExecuteChanged();
        StartRunCommand.NotifyCanExecuteChanged();
        AbortRunCommand.NotifyCanExecuteChanged();
        AddRunPointCommand.NotifyCanExecuteChanged();
        AddEmptyRunCommand.NotifyCanExecuteChanged();
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
        StartManualCommand.NotifyCanExecuteChanged();
        StopManualCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (IsRunning || IsManualRunning)
        {
            _runTimer?.Stop();
            IsRunning = false;
            IsManualRunning = false;
            _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        }

        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
    }
}

/// <summary>Ordinary least squares line over volumetric runs kept for auxiliary reference.</summary>
public sealed record PumpLinearFit(double Slope, double Intercept, double RSquared, int Count, double MinSpeed, double MaxSpeed)
{
    public double Predict(double speedUnits) => (Slope * speedUnits) + Intercept;

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
