using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The biomass optical sensor (Phase 3 WP1): enable, the momentary blank/start/stop actions,
/// the atomic low/high/optimal integration thresholds and the live Abs/Raw/IT/PWM readouts.
/// </summary>
/// <remarks>
/// <para>
/// The enable is an immediate toggle exactly as v.6's checkbox is — it flips a mode on the hub,
/// and the firmware drops blank/start/stop/threshold sub-commands while the sensor is disabled,
/// so the momentary actions are only enabled once the sensor is on. The thresholds are staged and
/// applied together (raw ADC counts, ints).
/// </para>
/// <para>
/// Two firmware facts shape everything below. First, <b>disabling drops the same frame's
/// sub-commands</b>: the Hub parses <c>biomassComm</c> before it reaches the biomass block, so
/// <c>{"stop":1,"biomassComm":0}</c> clears routing and then discards its own stop, leaving the
/// node acquiring. Turning the sensor off is therefore two ordered frames. Second, the Hub's
/// biomass mailbox is <b>consume-on-read and overwritten</b>, and the node polls it every 2 s, so
/// a blank issued just before a start is silently replaced — the momentary actions are serialised
/// behind <see cref="ExternalDeviceStatus.CanSend"/> rather than all being live at once.
/// </para>
/// <para>
/// Biomass is a <b>measurement</b>: it is deliberately excluded from the operator safe-stop, so a
/// stop never blinds the reading — the same treatment the level/foam sensor gets. It is still an
/// owned arbiter actuator, so a future recipe cannot fight the operator over blank/thresholds.
/// </para>
/// </remarks>
public sealed partial class BiomassControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private bool _initialised;

    /// <summary>Guards the enable setter while a refused toggle is being rolled back.</summary>
    private bool _revertingEnable;

    private BiomassControlSettings _committed;

    public BiomassControlViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? new ManualDispatcher(device);
        _committed = settings.Current.BiomassControl;
        Status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa", timeProvider) { NodeKind = NodeFirmwareCatalog.Biomass };
        Status.PropertyChanged += OnStatusChanged;

        Load(_committed);
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    /// <summary>Presence, routing and pending state of the node behind the Hub.</summary>
    public ExternalDeviceStatus Status { get; }

    /// <summary>Sensor enabled — <c>biomassComm</c>. Sent immediately on change, like v.6.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string LowThresholdText { get; set; } = "10000";

    [ObservableProperty]
    public partial string HighThresholdText { get; set; } = "40000";

    [ObservableProperty]
    public partial string OptimalThresholdText { get; set; } = "25000";

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string AbsorbanceText { get; set; } = "—";

    [ObservableProperty]
    public partial string RawText { get; set; } = "—";

    [ObservableProperty]
    public partial string IntegrationTimeText { get; set; } = "—";

    [ObservableProperty]
    public partial string PwmText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanActuate))]
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

    /// <summary>
    /// Thresholds may be applied only when a frame can actually reach the node and not owned by another.
    /// </summary>
    public bool CanApply => IsValid && Status.IsOnline && Status.CanSend && !IsOwnedByOther;

    /// <summary>
    /// The momentary actions need the sensor on, a free mailbox, and not owned by another.
    /// </summary>
    public bool CanActuate => IsEnabled && Status.IsOnline && Status.CanSend && !IsOwnedByOther;

    public string StateText => IsEnabled ? "Ativo" : "Desligado";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanActuate));
        BlankCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        if (!_initialised || _revertingEnable)
        {
            return;
        }

        if (IsOwnedByOther)
        {
            RevertEnable(value);
            StatusText = OwnerLockReason ?? "Sensor de biomassa sob controle de outro processo.";
            return;
        }

        if (value)
        {
            // The enable is an immediate mode switch on the hub, not a staged field.
            var enable = _dispatcher.Dispatch(CommandBuilders.BiomassComm(true));
            if (!enable.Accepted)
            {
                RevertEnable(true);
                StatusText = DispatchRefusal.Describe(enable);
                return;
            }

            Status.IsCommRequested = true;
            StatusText = "Sensor de biomassa ativado.";
            return;
        }

        // Stop first, while the Hub is still routing to the node: the same frame carrying
        // biomassComm:0 would have its stop discarded, and the node would keep acquiring
        // with the operator looking at a switch that says otherwise.
        var stop = _dispatcher.Dispatch(CommandBuilders.BiomassStop());
        if (!stop.Accepted)
        {
            RevertEnable(false);
            StatusText = DispatchRefusal.Describe(stop);
            return;
        }

        // Its own frame, after the stop, or the buffer merges the two back together.
        var disable = _dispatcher.DispatchSeparateFrame(CommandBuilders.BiomassComm(false));
        if (!disable.Accepted)
        {
            // The acquisition did stop; only the routing switch did not land. Say so rather
            // than claiming either full success or full failure.
            StatusText = "Aquisição interrompida, mas o roteamento do Hub não foi desligado: " +
                         DispatchRefusal.Describe(disable);
            return;
        }

        Status.IsCommRequested = false;
        Status.MarkCommandDispatched();
        StatusText = "Sensor de biomassa desativado (aquisição parada e roteamento desligado).";
    }

    /// <summary>Puts the switch back after a refused toggle, without resending anything.</summary>
    private void RevertEnable(bool attempted)
    {
        _revertingEnable = true;
        IsEnabled = !attempted;
        _revertingEnable = false;
    }

    partial void OnLowThresholdTextChanged(string value) => ValidateAndRefresh();

    partial void OnHighThresholdTextChanged(string value) => ValidateAndRefresh();

    partial void OnOptimalThresholdTextChanged(string value) => ValidateAndRefresh();

    /// <summary>Captures the blank (zero-absorbance) reference. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(CanActuate))]
    private void Blank() => SendMomentary(CommandBuilders.BiomassBlank(), "branco");

    /// <summary>Starts the acquisition loop. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(CanActuate))]
    private void Start() => SendMomentary(CommandBuilders.BiomassStart(), "início");

    /// <summary>Stops the acquisition loop. Momentary; needs the sensor on.</summary>
    [RelayCommand(CanExecute = nameof(CanActuate))]
    private void Stop() => SendMomentary(CommandBuilders.BiomassStop(), "parada");

    /// <summary>
    /// Sends one momentary action and holds the others until it is confirmed.
    /// </summary>
    /// <remarks>
    /// The Hub keeps a single pending biomass command and the node polls it every 2 s, so two
    /// momentaries issued back to back deliver only the second. The pending lock is what makes
    /// the mailbox depth of one visible to the operator instead of losing their first click.
    /// </remarks>
    private void SendMomentary(OpenTECCommand command, string label)
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Sensor de biomassa sob controle de outro processo.";
            return;
        }

        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        StatusText = $"Comando de {label} enviado ao sensor de biomassa; aguardando o dispositivo.";
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyThresholds()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Sensor de biomassa sob controle de outro processo.";
            return;
        }

        if (!TryGetStagedSettings(out var staged))
        {
            StatusText = ValidationError ?? "Revise os limiares de biomassa.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.BiomassThresholds(
            staged.LowThreshold, staged.HighThreshold, staged.OptimalThreshold));

        if (!result.Accepted)
        {
            // Keep the staged values: the operator's entry is still the pending intent, and
            // persisting it now would record a calibration the sensor never received.
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _committed = staged;
        _settings.Update(settings => settings with { BiomassControl = staged });
        HasPendingChange = false;
        Status.MarkCommandDispatched();
        StatusText = "Limiares de biomassa (baixo/alto/ótimo) enviados.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        HasPendingChange = false;
        StatusText = "Alterações não enviadas dos limiares de biomassa foram revertidas.";
    }

    public bool TryGetStagedSettings(out BiomassControlSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseInteger(LowThresholdText, out var low) || low is < 0 or > 200_000 ||
            !DosingInput.TryParseInteger(HighThresholdText, out var high) || high is < 0 or > 200_000 ||
            !DosingInput.TryParseInteger(OptimalThresholdText, out var optimal) || optimal is < 0 or > 200_000 ||
            low >= high || optimal <= low || optimal >= high)
        {
            return false;
        }

        settings = new BiomassControlSettings
        {
            LowThreshold = low,
            HighThreshold = high,
            OptimalThreshold = optimal,
        };
        return true;
    }

    private void Load(BiomassControlSettings s)
    {
        LowThresholdText = DosingInput.FormatInt(s.LowThreshold);
        HighThresholdText = DosingInput.FormatInt(s.HighThreshold);
        OptimalThresholdText = DosingInput.FormatInt(s.OptimalThreshold);
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
        ApplyThresholdsCommand.NotifyCanExecuteChanged();
        RefreshPendingState();
    }

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ExternalDeviceStatus.CanSend) or null))
        {
            return;
        }

        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanActuate));
        ApplyThresholdsCommand.NotifyCanExecuteChanged();
        BlankCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
        }
    }

    private string? Validate()
    {
        if (!DosingInput.TryParseInteger(LowThresholdText, out var low) || low is < 0 or > 200_000)
        {
            return "Limiar baixo: inteiro de 0 a 200000 (contagens).";
        }

        if (!DosingInput.TryParseInteger(HighThresholdText, out var high) || high is < 0 or > 200_000)
        {
            return "Limiar alto: inteiro de 0 a 200000 (contagens).";
        }

        if (!DosingInput.TryParseInteger(OptimalThresholdText, out var optimal) || optimal is < 0 or > 200_000)
        {
            return "Limiar ótimo: inteiro de 0 a 200000 (contagens).";
        }

        if (low >= high)
        {
            return "O limiar baixo deve ser menor que o alto.";
        }

        if (optimal <= low || optimal >= high)
        {
            return "O limiar ótimo deve ser maior que o baixo e menor que o alto.";
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
    {
        Status.Update(
            snapshot.HasBiomassTelemetry,
            snapshot.BiomassOnline,
            snapshot.BiomassCommandPending,
            snapshot.BiomassCommEnabled,
            snapshot.BiomassNode);

        // The four biomass channels arrive together; Abs carries the not-received sentinel,
        // so gate the whole block on it rather than showing 0 counts before any frame. The
        // parser now clears it when the node goes absent, so a stale absorbance can no longer
        // sit here looking live.
        if (snapshot.BiomassAbsorbance <= SensorReadings.NotReceived)
        {
            AbsorbanceText = RawText = IntegrationTimeText = PwmText = "—";
            return;
        }

        AbsorbanceText = snapshot.BiomassAbsorbance.ToString("F3", CultureInfo.CurrentCulture);
        RawText = snapshot.BiomassRaw.ToString(CultureInfo.CurrentCulture);
        IntegrationTimeText = snapshot.BiomassIntegrationTimeMs.ToString(CultureInfo.CurrentCulture);
        PwmText = snapshot.BiomassPwmPercent.ToString("F1", CultureInfo.CurrentCulture);
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= OnStatusChanged;
    }
}
