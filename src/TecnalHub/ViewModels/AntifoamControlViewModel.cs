using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// Complete antifoam dosing state (WP7): operation/mix timing and pump intensity.
/// </summary>
/// <remarks>
/// The module reports an <c>Antifoam</c> telemetry figure with no documented unit, so it is
/// shown raw and unitless until the meaning is confirmed on hardware. This card controls the
/// antifoam pump; the level/foam sensor that triggers it lives in
/// <see cref="FoamControlViewModel"/>.
/// </remarks>
public sealed partial class AntifoamControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private AntifoamControlSettings _committed;

    public AntifoamControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.AntifoamControl;

        Load(_committed);
        AppliedIsEnabled = false;
        _device.TelemetryReceived += OnTelemetryReceived;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    [ObservableProperty]
    public partial string OperationSecondsText { get; set; } = "5";

    [ObservableProperty]
    public partial string MixSecondsText { get; set; } = "60";

    [ObservableProperty]
    public partial string PumpSpeedPercentText { get; set; } = "50";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool AppliedIsEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string LiveAntifoamText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool IsValid => ValidationError is null;

    public bool CanApply => !IsEnabled || IsValid;

    public string AppliedStateText => AppliedIsEnabled ? "Ativo" : "Desligado";

    partial void OnOperationSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnMixSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnPumpSpeedPercentTextChanged(string value) => ValidateAndRefresh();

    partial void OnIsEnabledChanged(bool value) => ValidateAndRefresh();

    partial void OnAppliedIsEnabledChanged(bool value) => OnPropertyChanged(nameof(AppliedStateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryBuildPendingCommand(out var command))
        {
            StatusText = ValidationError ?? "Revise os parâmetros do antiespumante.";
            return;
        }

        _device.Send(command);
        CommitPendingCommand();
        StatusText = IsEnabled
            ? "Estado completo da dosagem de antiespumante enviado."
            : "Bomba de antiespumante desligada; intensidade zero enviada.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        IsEnabled = AppliedIsEnabled;
        HasPendingChange = false;
        StatusText = "Alterações não enviadas do antiespumante foram revertidas.";
    }

    /// <summary>Builds one atomic antifoam frame without sending it, for the bulk apply.</summary>
    public bool TryBuildPendingCommand(out TecnalCommand command)
    {
        if (!IsEnabled)
        {
            command = BuildSafeStop();
            return true;
        }

        if (!TryGetStagedSettings(out var staged))
        {
            command = TecnalCommand.Create();
            return false;
        }

        command = CommandBuilders.AntifoamControl(
            staged.OperationSeconds, staged.MixSeconds, staged.PumpSpeedPercent);
        return true;
    }

    /// <summary>Stops the pump; the level/foam sensor is deliberately left running.</summary>
    public TecnalCommand BuildSafeStop()
    {
        var p = TryGetStagedSettings(out var staged) ? staged : _committed;
        return CommandBuilders.AntifoamControlSafeStop(p.MixSeconds);
    }

    public void CommitPendingCommand()
    {
        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged;
            _settings.Update(settings => settings with { AntifoamControl = staged });
        }
        else if (!IsEnabled)
        {
            Load(_committed);
        }

        AppliedIsEnabled = IsEnabled;
        HasPendingChange = false;
    }

    public bool TryGetStagedSettings(out AntifoamControlSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseInteger(OperationSecondsText, out var operation) || operation is < 0 or > 999 ||
            !DosingInput.TryParseInteger(MixSecondsText, out var mix) || mix is < 1 or > 999 ||
            !DosingInput.TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return false;
        }

        settings = new AntifoamControlSettings
        {
            OperationSeconds = operation,
            MixSeconds = mix,
            PumpSpeedPercent = speed,
        };
        return true;
    }

    private void Load(AntifoamControlSettings s)
    {
        OperationSecondsText = DosingInput.FormatInt(s.OperationSeconds);
        MixSecondsText = DosingInput.FormatInt(s.MixSeconds);
        PumpSpeedPercentText = DosingInput.Format(s.PumpSpeedPercent, 0);
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
        if (!DosingInput.TryParseInteger(OperationSecondsText, out var operation) || operation is < 0 or > 999)
        {
            return "Operação: inteiro de 0 a 999 s.";
        }

        if (!DosingInput.TryParseInteger(MixSecondsText, out var mix) || mix is < 1 or > 999)
        {
            return "Mistura: inteiro de 1 a 999 s.";
        }

        if (!DosingInput.TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return "Intensidade da bomba: valor de 0 a 99%.";
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
        => LiveAntifoamText = snapshot.Antifoam > SensorReadings.NotReceived
            ? snapshot.Antifoam.ToString("F2", CultureInfo.CurrentCulture)
            : "—";

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
