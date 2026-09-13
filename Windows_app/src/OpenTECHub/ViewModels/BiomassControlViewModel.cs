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
    private static readonly int[] SupportedIntegrationTimesMs = [25, 50, 100, 200, 400, 800];

    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<OpenTECCommand> _acquisitionQueue = new();
    private bool _isSendingAcquisitionQueue;
    private int _acquisitionCommandsTotal;
    private int _acquisitionCommandsSent;
    private DateTime? _lastAcquisitionCommandSentAt;
    private BiomassControlSettings? _requestedAcquisitionSettings;
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatcher = dispatcher ?? new ManualDispatcher(device);
        _committed = settings.Current.BiomassControl;
        Status = new ExternalDeviceStatus("Sensor de biomassa", "do sensor de biomassa", timeProvider) { NodeKind = NodeFirmwareCatalog.Biomass };
        Status.PropertyChanged += OnStatusChanged;

        Load(_committed);
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _initialised = true;
        ValidateAndRefresh();
        SyncSelectorsFromText();
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
    public partial string AcquisitionIntegrationTimeText { get; set; } = "100";

    [ObservableProperty]
    public partial string AcquisitionPwmText { get; set; } = "2.0";

    [ObservableProperty]
    public partial string AcquisitionGainGearText { get; set; } = "0";

    [ObservableProperty]
    public partial string AcquisitionEmaFactorText { get; set; } = "0.80";

    [ObservableProperty]
    public partial string AcquisitionProbePeriodMsText { get; set; } = "25000";

    [ObservableProperty]
    public partial string? AcquisitionValidationError { get; set; }

    [ObservableProperty]
    public partial string AppliedGearText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedEmaText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedProbePeriodText { get; set; } = "—";

    public string AppliedITText => IntegrationTimeText;
    public string AppliedPwmText => PwmText;

    public bool IsAcquisitionValid => AcquisitionValidationError is null;

    public bool CanApplyAcquisition => IsEnabled && Status.IsOnline && Status.CanSend && !IsOwnedByOther && !_isSendingAcquisitionQueue && IsAcquisitionValid;

    public bool CanCancelAcquisition => _isSendingAcquisitionQueue;

    public bool CanEditAcquisition => !_isSendingAcquisitionQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanActuate))]
    [NotifyPropertyChangedFor(nameof(CanApplyAcquisition))]
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
        OnPropertyChanged(nameof(CanApplyAcquisition));
        BlankCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ApplyAcquisitionCommand.NotifyCanExecuteChanged();

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
            StatusText = "Aquisição interrompida, mas o sensor continua habilitado no Hub: " +
                         DispatchRefusal.Describe(disable);
            return;
        }

        Status.IsCommRequested = false;
        Status.MarkCommandDispatched();
        StatusText = "Sensor de biomassa desativado (aquisição parada e desabilitado no Hub).";
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

    partial void OnAcquisitionIntegrationTimeTextChanged(string value)
    {
        ValidateAcquisition();
        SyncSelectorsFromText();
    }

    partial void OnAcquisitionPwmTextChanged(string value) => ValidateAcquisition();

    partial void OnAcquisitionGainGearTextChanged(string value)
    {
        ValidateAcquisition();
        SyncSelectorsFromText();
        OnPropertyChanged(nameof(GearPreviewText));
    }

    // ---- Selectors over the text fields (PONTOS §7.2) --------------------------------
    // The wire wants an IT *code* and a linear gear index (IT slot × 8 + PWM slot); the
    // operator thinks in milliseconds and in "which slot". The selectors own the arithmetic
    // and keep the text properties as the single source the validator and tests read.

    private bool _syncingSelectors;

    /// <summary>Integration times the VEML7700 offers, in ms; each maps to a code 0..5 on the wire.</summary>
    public IReadOnlyList<int> IntegrationTimeOptions => SupportedIntegrationTimesMs;

    /// <summary>IT slots of the node's table (gear ÷ 8).</summary>
    public IReadOnlyList<int> GearItSlots { get; } = [0, 1, 2, 3];

    /// <summary>PWM slots of the node's table (gear mod 8).</summary>
    public IReadOnlyList<int> GearPwmSlots { get; } = [0, 1, 2, 3, 4, 5, 6, 7];

    /// <summary>Combo-bound view of <see cref="AcquisitionIntegrationTimeText"/>.</summary>
    [ObservableProperty]
    public partial int SelectedIntegrationTimeMs { get; set; } = 100;

    /// <summary>IT slot the gear selects (0..3); with <see cref="GearPwmSlot"/> it is the gear index.</summary>
    [ObservableProperty]
    public partial int GearItSlot { get; set; }

    /// <summary>PWM slot the gear selects (0..7).</summary>
    [ObservableProperty]
    public partial int GearPwmSlot { get; set; }

    /// <summary>"Marcha 13 = slot IT 1 × slot PWM 5", or a dash when the text is not a valid gear.</summary>
    public string GearPreviewText
        => DosingInput.TryParseInteger(AcquisitionGainGearText, out var gear) && gear is >= 0 and <= 31
            ? $"Marcha {gear} = slot IT {gear / 8} × slot PWM {gear % 8}"
            : "—";

    /// <summary>The node's echoed gear decoded the same way, so the two read side by side.</summary>
    public string AppliedGearDetailText
        => int.TryParse(AppliedGearText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var gear) && gear is >= 0 and <= 31
            ? $"slot IT {gear / 8} · slot PWM {gear % 8}"
            : "";

    partial void OnSelectedIntegrationTimeMsChanged(int value)
    {
        if (_syncingSelectors || Array.IndexOf(SupportedIntegrationTimesMs, value) < 0)
        {
            return;
        }

        AcquisitionIntegrationTimeText = value.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnGearItSlotChanged(int value) => PushGearFromSlots();

    partial void OnGearPwmSlotChanged(int value) => PushGearFromSlots();

    private void PushGearFromSlots()
    {
        if (_syncingSelectors)
        {
            return;
        }

        var it = Math.Clamp(GearItSlot, 0, 3);
        var pwm = Math.Clamp(GearPwmSlot, 0, 7);
        AcquisitionGainGearText = ((it * 8) + pwm).ToString(CultureInfo.InvariantCulture);
    }

    private void SyncSelectorsFromText()
    {
        if (_syncingSelectors)
        {
            return;
        }

        _syncingSelectors = true;
        try
        {
            if (DosingInput.TryParseInteger(AcquisitionIntegrationTimeText, out var it) &&
                Array.IndexOf(SupportedIntegrationTimesMs, it) >= 0)
            {
                SelectedIntegrationTimeMs = it;
            }

            if (DosingInput.TryParseInteger(AcquisitionGainGearText, out var gear) && gear is >= 0 and <= 31)
            {
                GearItSlot = gear / 8;
                GearPwmSlot = gear % 8;
            }
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    partial void OnAcquisitionEmaFactorTextChanged(string value) => ValidateAcquisition();

    partial void OnAcquisitionProbePeriodMsTextChanged(string value) => ValidateAcquisition();

    private void ValidateAcquisition()
    {
        if (!_initialised)
        {
            return;
        }

        if (!DosingInput.TryParseInteger(AcquisitionIntegrationTimeText, out var it) ||
            Array.IndexOf(SupportedIntegrationTimesMs, it) < 0)
        {
            AcquisitionValidationError = "Tempo de integração (IT): use 25, 50, 100, 200, 400 ou 800 ms.";
            NotifyAcquisitionAvailability();
            return;
        }

        if (!DosingInput.TryParseDouble(AcquisitionPwmText, out var pwm) || pwm is < 0.0 or > 100.0)
        {
            AcquisitionValidationError = "PWM do LED: 0.0 a 100.0 %.";
            NotifyAcquisitionAvailability();
            return;
        }

        if (!DosingInput.TryParseInteger(AcquisitionGainGearText, out var gear) || gear is < 0 or > 31)
        {
            AcquisitionValidationError = "Marcha óptica (Gear): inteiro de 0 a 31 (IT × 8 + PWM).";
            NotifyAcquisitionAvailability();
            return;
        }

        if (!DosingInput.TryParseDouble(AcquisitionEmaFactorText, out var ema) || ema is < 0.01 or > 1.0)
        {
            AcquisitionValidationError = "Fator EMA: 0.01 a 1.00.";
            NotifyAcquisitionAvailability();
            return;
        }

        if (!DosingInput.TryParseInteger(AcquisitionProbePeriodMsText, out var period) || period is < 100 or > 3_600_000)
        {
            AcquisitionValidationError = "Período de leitura: inteiro de 100 a 3600000 ms.";
            NotifyAcquisitionAvailability();
            return;
        }

        AcquisitionValidationError = null;
        NotifyAcquisitionAvailability();
    }

    private void NotifyAcquisitionAvailability()
    {
        OnPropertyChanged(nameof(IsAcquisitionValid));
        OnPropertyChanged(nameof(CanApplyAcquisition));
        OnPropertyChanged(nameof(CanCancelAcquisition));
        OnPropertyChanged(nameof(CanEditAcquisition));
        ApplyAcquisitionCommand.NotifyCanExecuteChanged();
        CancelAcquisitionCommand.NotifyCanExecuteChanged();
        RevertAcquisitionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyAcquisition))]
    private void ApplyAcquisition()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Sensor de biomassa sob controle de outro processo.";
            return;
        }

        ValidateAcquisition();
        if (AcquisitionValidationError is not null)
        {
            StatusText = AcquisitionValidationError;
            return;
        }

        DosingInput.TryParseInteger(AcquisitionIntegrationTimeText, out var it);
        DosingInput.TryParseDouble(AcquisitionPwmText, out var pwm);
        DosingInput.TryParseInteger(AcquisitionGainGearText, out var gear);
        DosingInput.TryParseDouble(AcquisitionEmaFactorText, out var ema);
        DosingInput.TryParseInteger(AcquisitionProbePeriodMsText, out var period);

        _requestedAcquisitionSettings = _committed with
        {
            IntegrationTime = it,
            PwmPercent = pwm,
            GainGear = gear,
            EmaFactor = ema,
            ProbePeriodMs = period
        };

        // The node reports IT in milliseconds, but set_it accepts the VEML7700 code
        // (0..5 => 25, 50, 100, 200, 400, 800 ms). Never put milliseconds on the wire.
        var integrationCode = Array.IndexOf(SupportedIntegrationTimesMs, it);
        var commands = CommandBuilders.BiomassTuning(integrationCode, pwm, gear, ema, period);
        _acquisitionQueue.Clear();
        foreach (var cmd in commands)
        {
            _acquisitionQueue.Enqueue(cmd);
        }

        _acquisitionCommandsTotal = commands.Count;
        _acquisitionCommandsSent = 0;
        _isSendingAcquisitionQueue = true;
        NotifyAcquisitionAvailability();

        SendNextAcquisitionCommand();
    }

    private void SendNextAcquisitionCommand()
    {
        if (_acquisitionQueue.Count == 0)
        {
            CompleteAcquisitionQueue();
            return;
        }

        var cmd = _acquisitionQueue.Dequeue();
        var result = _dispatcher.Dispatch(cmd);
        if (!result.Accepted)
        {
            _acquisitionQueue.Clear();
            _isSendingAcquisitionQueue = false;
            _requestedAcquisitionSettings = null;
            NotifyAcquisitionAvailability();
            StatusText = "Falha ao enviar parâmetro de aquisição: " + DispatchRefusal.Describe(result);
            return;
        }

        _acquisitionCommandsSent++;
        _lastAcquisitionCommandSentAt = _timeProvider.GetUtcNow().UtcDateTime;
        Status.MarkCommandDispatched();
        NotifyAcquisitionAvailability();
        StatusText = $"Enviando parâmetros de aquisição ({_acquisitionCommandsSent}/{_acquisitionCommandsTotal})...";
    }

    [RelayCommand(CanExecute = nameof(CanCancelAcquisition))]
    private void CancelAcquisition()
    {
        _acquisitionQueue.Clear();
        _isSendingAcquisitionQueue = false;
        _requestedAcquisitionSettings = null;
        NotifyAcquisitionAvailability();
        StatusText = "Fila de aquisição cancelada. O comando já em trânsito ainda pode ser aplicado pelo nó.";
    }

    [RelayCommand(CanExecute = nameof(CanEditAcquisition))]
    private void RevertAcquisition()
    {
        var s = _committed;
        AcquisitionIntegrationTimeText = DosingInput.FormatInt(s.IntegrationTime);
        AcquisitionPwmText = DosingInput.Format(s.PwmPercent, 1);
        AcquisitionGainGearText = DosingInput.FormatInt(s.GainGear);
        AcquisitionEmaFactorText = DosingInput.Format(s.EmaFactor, 2);
        AcquisitionProbePeriodMsText = DosingInput.FormatInt(s.ProbePeriodMs);
        ValidateAcquisition();
        StatusText = "Parâmetros de aquisição restaurados das configurações persistidas.";
    }

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
        LowThresholdText = DosingInput.FormatInt(_committed.LowThreshold);
        HighThresholdText = DosingInput.FormatInt(_committed.HighThreshold);
        OptimalThresholdText = DosingInput.FormatInt(_committed.OptimalThreshold);
        ValidateAndRefresh();
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
            IntegrationTime = _committed.IntegrationTime,
            PwmPercent = _committed.PwmPercent,
            GainGear = _committed.GainGear,
            EmaFactor = _committed.EmaFactor,
            ProbePeriodMs = _committed.ProbePeriodMs,
        };
        return true;
    }

    private void Load(BiomassControlSettings s)
    {
        LowThresholdText = DosingInput.FormatInt(s.LowThreshold);
        HighThresholdText = DosingInput.FormatInt(s.HighThreshold);
        OptimalThresholdText = DosingInput.FormatInt(s.OptimalThreshold);
        AcquisitionIntegrationTimeText = DosingInput.FormatInt(s.IntegrationTime);
        AcquisitionPwmText = DosingInput.Format(s.PwmPercent, 1);
        AcquisitionGainGearText = DosingInput.FormatInt(s.GainGear);
        AcquisitionEmaFactorText = DosingInput.Format(s.EmaFactor, 2);
        AcquisitionProbePeriodMsText = DosingInput.FormatInt(s.ProbePeriodMs);
        ValidateAcquisition();
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
        ValidateAcquisition();
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
        NotifyAcquisitionAvailability();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
            if (_isSendingAcquisitionQueue)
            {
                _acquisitionQueue.Clear();
                _isSendingAcquisitionQueue = false;
                _requestedAcquisitionSettings = null;
                NotifyAcquisitionAvailability();
                StatusText = "Conexão perdida; a fila restante de aquisição foi cancelada.";
            }
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
            AppliedGearText = AppliedEmaText = AppliedProbePeriodText = "—";
        }
        else
        {
            AbsorbanceText = snapshot.BiomassAbsorbance.ToString("F3", CultureInfo.CurrentCulture);
            RawText = snapshot.BiomassRaw.ToString(CultureInfo.CurrentCulture);
            IntegrationTimeText = snapshot.BiomassIntegrationTimeMs.ToString(CultureInfo.CurrentCulture);
            PwmText = snapshot.BiomassPwmPercent.ToString("F1", CultureInfo.CurrentCulture);
            AppliedGearText = snapshot.BiomassGear.HasValue && snapshot.BiomassGear.Value > SensorReadings.NotReceived
                ? snapshot.BiomassGear.Value.ToString(CultureInfo.CurrentCulture)
                : "—";
            OnPropertyChanged(nameof(AppliedGearDetailText));
            AppliedEmaText = snapshot.BiomassEma.HasValue && snapshot.BiomassEma.Value > SensorReadings.NotReceived
                ? snapshot.BiomassEma.Value.ToString("F2", CultureInfo.CurrentCulture)
                : "—";
            AppliedProbePeriodText = snapshot.BiomassProbePeriodMs.HasValue && snapshot.BiomassProbePeriodMs.Value > SensorReadings.NotReceived
                ? snapshot.BiomassProbePeriodMs.Value.ToString(CultureInfo.CurrentCulture)
                : "—";
        }

        OnPropertyChanged(nameof(AppliedITText));
        OnPropertyChanged(nameof(AppliedPwmText));

        if (_isSendingAcquisitionQueue)
        {
            if (snapshot.BiomassCommandPending == false)
            {
                if (_acquisitionQueue.Count > 0)
                {
                    SendNextAcquisitionCommand();
                }
                else
                {
                    CompleteAcquisitionQueue();
                }
            }
            else if (_lastAcquisitionCommandSentAt.HasValue &&
                     (_timeProvider.GetUtcNow().UtcDateTime - _lastAcquisitionCommandSentAt.Value).TotalSeconds > 10.0)
            {
                _acquisitionQueue.Clear();
                _isSendingAcquisitionQueue = false;
                _requestedAcquisitionSettings = null;
                NotifyAcquisitionAvailability();
                StatusText = "Aviso: tempo limite excedido (10 s) aguardando consumo do comando pelo nó de biomassa.";
            }
        }
    }

    private void CompleteAcquisitionQueue()
    {
        if (_requestedAcquisitionSettings is { } applied)
        {
            _committed = applied;
            _settings.Update(settings => settings with { BiomassControl = applied });
        }

        _requestedAcquisitionSettings = null;
        _isSendingAcquisitionQueue = false;
        NotifyAcquisitionAvailability();
        StatusText = "Todos os parâmetros de aquisição foram confirmados pelo nó e salvos no app.";
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= OnStatusChanged;
    }
}
