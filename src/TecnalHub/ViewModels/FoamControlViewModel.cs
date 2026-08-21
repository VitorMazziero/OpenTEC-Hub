using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// Level/foam sensor configuration (WP7): the sensor enable, its reference height and the
/// three timers that shape the automatic antifoam response.
/// </summary>
/// <remarks>
/// This is sensor and automation configuration, not a held actuator, so it sits outside the
/// command arbiter and the global safe-stop — a safe-stop stops the antifoam pump but must
/// not blind foam monitoring. The card has its own apply, independent of the bulk apply.
/// </remarks>
public sealed partial class FoamControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private FoamControlSettings _committed;

    public FoamControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.FoamControl;

        Load(_committed);
        AppliedSensorEnabled = false;
        _device.TelemetryReceived += OnTelemetryReceived;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    [ObservableProperty]
    public partial bool SensorEnabled { get; set; }

    [ObservableProperty]
    public partial string ReferenceMillimetresText { get; set; } = "100";

    [ObservableProperty]
    public partial string StartDelaySecondsText { get; set; } = "30";

    [ObservableProperty]
    public partial string PulseSecondsText { get; set; } = "2";

    [ObservableProperty]
    public partial string IntervalSecondsText { get; set; } = "30";

    [ObservableProperty]
    public partial bool AppliedSensorEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string LiveDistanceText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsValid;

    public string AppliedStateText => AppliedSensorEnabled ? "Sensor ativo" : "Sensor inativo";

    partial void OnSensorEnabledChanged(bool value) => ValidateAndRefresh();

    partial void OnReferenceMillimetresTextChanged(string value) => ValidateAndRefresh();

    partial void OnStartDelaySecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnPulseSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnIntervalSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnAppliedSensorEnabledChanged(bool value) => OnPropertyChanged(nameof(AppliedStateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryGetStagedSettings(out var staged))
        {
            StatusText = ValidationError ?? "Revise os parâmetros de espuma.";
            return;
        }

        _device.Send(CommandBuilders.FoamControl(
            staged.SensorEnabled,
            staged.ReferenceMillimetres,
            staged.StartDelaySeconds,
            staged.PulseSeconds,
            staged.IntervalSeconds));

        _committed = staged;
        _settings.Update(settings => settings with { FoamControl = staged });
        AppliedSensorEnabled = staged.SensorEnabled;
        HasPendingChange = false;
        StatusText = staged.SensorEnabled
            ? "Configuração do sensor de nível/espuma enviada."
            : "Sensor de nível/espuma desativado.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        HasPendingChange = false;
        StatusText = "Alterações não enviadas de espuma foram revertidas.";
    }

    public bool TryGetStagedSettings(out FoamControlSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseDouble(ReferenceMillimetresText, out var reference) || reference < 0.0 || reference > 999.0 ||
            !DosingInput.TryParseInteger(StartDelaySecondsText, out var startDelay) || startDelay is < 0 or > 3600 ||
            !DosingInput.TryParseInteger(PulseSecondsText, out var pulse) || pulse is < 1 or > 999 ||
            !DosingInput.TryParseInteger(IntervalSecondsText, out var interval) || interval is < 1 or > 999)
        {
            return false;
        }

        settings = new FoamControlSettings
        {
            SensorEnabled = SensorEnabled,
            ReferenceMillimetres = reference,
            StartDelaySeconds = startDelay,
            PulseSeconds = pulse,
            IntervalSeconds = interval,
        };
        return true;
    }

    private void Load(FoamControlSettings s)
    {
        SensorEnabled = s.SensorEnabled;
        ReferenceMillimetresText = DosingInput.Format(s.ReferenceMillimetres, 0);
        StartDelaySecondsText = DosingInput.FormatInt(s.StartDelaySeconds);
        PulseSecondsText = DosingInput.FormatInt(s.PulseSeconds);
        IntervalSecondsText = DosingInput.FormatInt(s.IntervalSeconds);
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
        if (!DosingInput.TryParseDouble(ReferenceMillimetresText, out var reference) || reference < 0.0 || reference > 999.0)
        {
            return "Referência: valor de 0 a 999 mm.";
        }

        if (!DosingInput.TryParseInteger(StartDelaySecondsText, out var startDelay) || startDelay is < 0 or > 3600)
        {
            return "Atraso inicial: inteiro de 0 a 3600 s.";
        }

        if (!DosingInput.TryParseInteger(PulseSecondsText, out var pulse) || pulse is < 1 or > 999)
        {
            return "Pulso: inteiro de 1 a 999 s.";
        }

        if (!DosingInput.TryParseInteger(IntervalSecondsText, out var interval) || interval is < 1 or > 999)
        {
            return "Intervalo: inteiro de 1 a 999 s.";
        }

        return null;
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        HasPendingChange = !TryGetStagedSettings(out var staged) || staged != _committed;
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
        => LiveDistanceText = snapshot.Distance > SensorReadings.NotReceived
            ? snapshot.Distance.ToString("F0", CultureInfo.CurrentCulture)
            : "—";

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
