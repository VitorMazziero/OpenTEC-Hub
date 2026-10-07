using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Complete pH dosing state from v.6. Calibration and dosing deliberately remain
/// separate: this ViewModel never changes probe coefficients.
/// </summary>
public sealed partial class PHControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private PHControlSettings _committed;
    public double AppliedInactiveBand => _committed.InactiveBand;

    public PHControlViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _committed = settings.Current.PHControl;

        Load(_committed);
        AppliedIsEnabled = false;
        _device.TelemetryReceived += OnTelemetryReceived;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    [ObservableProperty]
    public partial string SetpointText { get; set; } = "7.00";

    [ObservableProperty]
    public partial string InactiveBandText { get; set; } = "0.15";

    [ObservableProperty]
    public partial string OperationSecondsText { get; set; } = "1";

    [ObservableProperty]
    public partial string MixSecondsText { get; set; } = "60";

    [ObservableProperty]
    public partial string PumpSpeedPercentText { get; set; } = "80";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool AppliedIsEnabled { get; set; }

    [ObservableProperty]
    public partial double? AppliedSetpoint { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string LivePHText { get; set; } = "—";

    [ObservableProperty]
    public partial string LiveRawText { get; set; } = "—";

    private double? _livePHValue;

    public string FormattedDeviation
    {
        get
        {
            if (_livePHValue is not { } live || !TryParseDouble(SetpointText, out var setpoint))
            {
                return "—";
            }

            return (live - setpoint).ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture);
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsOwnedByOther))]
    [NotifyPropertyChangedFor(nameof(HasOwnerBadge))]
    [NotifyPropertyChangedFor(nameof(OwnerBadgeText))]
    [NotifyPropertyChangedFor(nameof(OwnerLockReason))]
    public partial CommandOwner CurrentOwner { get; set; } = CommandOwner.Manual;

    public bool IsOwnedByOther => CurrentOwner != CommandOwner.Manual;
    public bool HasOwnerBadge => IsOwnedByOther;
    public string? OwnerBadgeText => OwnershipUi.GetBadgeText(CurrentOwner);
    public string? OwnerLockReason => OwnershipUi.GetLockReason(CurrentOwner);

    public bool IsValid => ValidationError is null;

    /// <summary>A stop is never blocked by an invalid staged field; blocked if owned by another.</summary>
    public bool CanApply => (!IsEnabled || IsValid) && !IsOwnedByOther;

    public string StateText => IsEnabled ? "Ativo" : "Desligado";

    partial void OnSetpointTextChanged(string value)
    {
        ValidateAndRefresh();
        OnPropertyChanged(nameof(FormattedDeviation));
    }

    partial void OnInactiveBandTextChanged(string value) => ValidateAndRefresh();

    partial void OnOperationSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnMixSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnPumpSpeedPercentTextChanged(string value) => ValidateAndRefresh();

    partial void OnIsEnabledChanged(bool value)
    {
        if (_initialised && IsOwnedByOther && value != AppliedIsEnabled)
        {
            IsEnabled = AppliedIsEnabled;
            return;
        }

        ValidateAndRefresh();
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnAppliedIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));

    partial void OnAppliedSetpointChanged(double? value) => OnPropertyChanged(nameof(StateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Operação bloqueada pelo controlador atual.";
            return;
        }

        if (!TryBuildPendingCommand(out var command))
        {
            StatusText = ValidationError ?? "Revise os parâmetros de pH.";
            return;
        }

        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        CommitPendingCommand();
        StatusText = IsEnabled
            ? "Estado completo do controle de pH enviado."
            : "Controle de pH desligado; intensidade zero enviada.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        IsEnabled = AppliedIsEnabled;
        HasPendingChange = false;
        StatusText = "Alterações de pH não enviadas foram revertidas.";
    }

    /// <summary>Builds one atomic v.6 pH frame without sending it.</summary>
    public bool TryBuildPendingCommand(out OpenTECCommand command)
    {
        if (!IsEnabled)
        {
            command = BuildSafeStop();
            return true;
        }

        if (!TryGetStagedSettings(out var staged))
        {
            command = OpenTECCommand.Create();
            return false;
        }

        command = CommandBuilders.PHControl(
            staged.Setpoint,
            staged.InactiveBand,
            staged.OperationSeconds,
            staged.MixSeconds,
            staged.PumpSpeedPercent);
        return true;
    }

    /// <summary>Safe-stop using valid staged auxiliaries, else the last committed set.</summary>
    public OpenTECCommand BuildSafeStop()
    {
        var parameters = TryGetStagedSettings(out var staged) ? staged : _committed;
        return CommandBuilders.PHControlSafeStop(
            parameters.InactiveBand,
            parameters.OperationSeconds,
            parameters.MixSeconds);
    }

    /// <summary>Updates the acknowledged UI state after a caller queues the command.</summary>
    public void CommitPendingCommand()
    {
        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged;
            _settings.Update(settings => settings with { PHControl = staged });
        }
        else if (!IsEnabled)
        {
            // A stop remains available even when a staged field is malformed. Once
            // that stop is committed, put the last valid auxiliaries back on screen so
            // the UI does not claim to be clean while still displaying invalid text.
            Load(_committed);
        }

        AppliedIsEnabled = IsEnabled;
        AppliedSetpoint = IsEnabled ? _committed.Setpoint : 0.0;
        HasPendingChange = false;
    }

    /// <summary>
    /// Calibration interlock. A probe in a buffer outside the vessel must never cause
    /// the dosing pump to run.
    /// </summary>
    public void SuspendForCalibration()
    {
        var result = _dispatcher.Dispatch(BuildSafeStop());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        IsEnabled = false;
        AppliedIsEnabled = false;
        AppliedSetpoint = 0.0;
        HasPendingChange = false;
        StatusText = "Controle de pH suspenso pela calibração da sonda.";
    }

    /// <summary>Stages a saved preset. Nothing reaches the wire.</summary>
    public void Stage(PHControlSettings settings, bool enabled)
    {
        Load(settings);
        IsEnabled = enabled;
        RefreshPendingState();
    }

    public bool TryGetStagedSettings(out PHControlSettings settings)
    {
        settings = _committed;
        if (!TryParseDouble(SetpointText, out var setpoint) || setpoint < 1.0 || setpoint > 14.0 ||
            !TryParseDouble(InactiveBandText, out var inactiveBand) || inactiveBand <= 0.0 || inactiveBand >= 2.0 ||
            !TryParseInteger(OperationSecondsText, out var operation) || operation < 1 || operation > 999 ||
            !TryParseInteger(MixSecondsText, out var mix) || mix < 1 || mix > 999 ||
            !TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return false;
        }

        settings = new PHControlSettings
        {
            Setpoint = setpoint,
            InactiveBand = inactiveBand,
            OperationSeconds = operation,
            MixSeconds = mix,
            PumpSpeedPercent = speed,
        };
        return true;
    }

    private void Load(PHControlSettings settings)
    {
        SetpointText = Format(settings.Setpoint, 2);
        InactiveBandText = Format(settings.InactiveBand, 2);
        OperationSecondsText = settings.OperationSeconds.ToString(CultureInfo.CurrentCulture);
        MixSecondsText = settings.MixSeconds.ToString(CultureInfo.CurrentCulture);
        PumpSpeedPercentText = Format(settings.PumpSpeedPercent, 0);
    }

    private void ValidateAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        ValidationError = Validate();
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        RefreshPendingState();
    }

    private string? Validate()
    {
        if (!TryParseDouble(SetpointText, out var setpoint) || setpoint < 1.0 || setpoint > 14.0)
        {
            return "Setpoint: informe um valor entre 1,00 e 14,00 pH.";
        }

        if (!TryParseDouble(InactiveBandText, out var band) || band <= 0.0 || band >= 2.0)
        {
            return "Banda inativa: valor maior que 0 e menor que 2 pH.";
        }

        if (!TryParseInteger(OperationSecondsText, out var operation) || operation < 1 || operation > 999)
        {
            return "Tempo operando: inteiro de 1 a 999 s.";
        }

        if (!TryParseInteger(MixSecondsText, out var mix) || mix < 1 || mix > 999)
        {
            return "Tempo de repouso/mistura: inteiro de 1 a 999 s.";
        }

        if (!TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return "Velocidade da bomba: valor de 0 a 99%.";
        }

        return null;
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        if (IsEnabled != AppliedIsEnabled)
        {
            HasPendingChange = true;
            return;
        }

        HasPendingChange = !TryGetStagedSettings(out var staged) || staged != _committed;
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _livePHValue = snapshot.PHCalibrated > SensorReadings.NotReceived
            ? snapshot.PHCalibrated
            : null;
        LivePHText = snapshot.PHCalibrated > SensorReadings.NotReceived
            ? snapshot.PHCalibrated.ToString("F2", CultureInfo.CurrentCulture)
            : "—";
        LiveRawText = snapshot.PHRaw > SensorReadings.NotReceived
            ? snapshot.PHRaw.ToString("F1", CultureInfo.CurrentCulture)
            : "—";
        OnPropertyChanged(nameof(FormattedDeviation));
    }

    private static bool TryParseDouble(string? text, out double value)
        => double.TryParse(
               (text ?? "").Trim().Replace(',', '.'),
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out value) &&
           double.IsFinite(value);

    private static bool TryParseInteger(string? text, out int value)
    {
        value = default;
        return TryParseDouble(text, out var parsed) &&
               Math.Abs(parsed - Math.Round(parsed)) <= 1e-9 &&
               parsed is >= int.MinValue and <= int.MaxValue &&
               (value = (int)parsed) == parsed;
    }

    private static string Format(double value, int decimals)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
