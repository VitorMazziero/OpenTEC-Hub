using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Level/foam sensor configuration (WP7): the sensor enable, its reference height and the
/// three timers that shape the automatic antifoam response.
/// </summary>
/// <remarks>
/// <para>
/// This is sensor and automation configuration, not a held actuator, so it sits outside the
/// command arbiter and the global safe-stop — a safe-stop stops the antifoam pump but must
/// not blind foam monitoring. The card has its own apply, independent of the bulk apply.
/// </para>
/// <para>
/// Its presence is reported like every other external device. The values configure the Hub's
/// automatic response, but the operator surface stays locked until the distance node is known
/// online. This prevents focus loss or Enter from dispatching a configuration while the page is
/// showing that the sensor is disconnected.
/// </para>
/// </remarks>
public sealed partial class FoamControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private bool _initialised;
    private FoamControlSettings _committed;

    public FoamControlViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        IDialogService? dialogs = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? new ManualDispatcher(device);
        _dialogs = dialogs ?? new DialogService();
        _committed = settings.Current.FoamControl;
        Status = new ExternalDeviceStatus("Sensor de distância", "do sensor de distância", timeProvider) { NodeKind = NodeFirmwareCatalog.Distance };
        _time = timeProvider ?? TimeProvider.System;
        Status.PropertyChanged += OnStatusChanged;

        Load(_committed);
        AppliedSensorEnabled = false;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _initialised = true;
        ValidateAndRefresh();
        ValidateNodeConfigAndRefresh();
        HasPendingChange = false;
    }

    /// <summary>Presence and routing state of the node behind the Hub, for display only.</summary>
    public ExternalDeviceStatus Status { get; }

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

    /// <summary>Last distance reference actually queued for the device.</summary>
    [ObservableProperty]
    public partial double? AppliedReferenceMillimetres { get; set; }

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

    public bool CanApply => IsValid && Status.IsOnline && Status.CanSend;

    public string StateText => SensorEnabled ? "Ativo" : "Desligado";

    [ObservableProperty]
    public partial string OffsetMmText { get; set; } = "20.0";

    [ObservableProperty]
    public partial string SamplePeriodMsText { get; set; } = "1000";

    [ObservableProperty]
    public partial string SendPeriodMsText { get; set; } = "1000";

    [ObservableProperty]
    public partial string AppliedOffsetText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedSamplePeriodMsText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedSendPeriodMsText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendNodeConfig))]
    [NotifyPropertyChangedFor(nameof(CanResetNodeConfig))]
    [NotifyPropertyChangedFor(nameof(NodeConfigUnavailableText))]
    public partial bool CanEditNodeConfig { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValidNodeConfig))]
    [NotifyPropertyChangedFor(nameof(CanSendNodeConfig))]
    public partial string? NodeConfigValidationError { get; set; }

    [ObservableProperty]
    public partial string NodeConfigStatusText { get; set; } =
        "Configurações do nó restauradas para revisão; nenhum comando foi enviado.";

    /// <summary>
    /// The node was told to write its NVS and the frame has not echoed the new values yet
    /// (PONTOS §6.1). The Hub's ack says the node <i>received</i> the command; only the echo
    /// says it <i>applied</i> it, and a reset makes the node reboot in between.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeConfigSyncText))]
    public partial bool IsNodeConfigSyncing { get; set; }

    public string NodeConfigSyncText => IsNodeConfigSyncing ? "Gravando na NVS do nó… aguardando o eco na telemetria" : "";

    /// <summary>How long the echo may take before the operator is told it did not come. Covers the node's 15 s backoff.</summary>
    public static readonly TimeSpan NodeConfigEchoTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time;
    private (double Offset, int Sample, int Send)? _expectedNodeConfig;
    private DateTimeOffset _nodeConfigSentAt;

    public bool IsValidNodeConfig => NodeConfigValidationError is null;

    public bool CanSendNodeConfig => Status.IsOnline && Status.CanSend && CanEditNodeConfig && IsValidNodeConfig;

    public bool CanResetNodeConfig => Status.IsOnline && Status.CanSend && CanEditNodeConfig;

    public string? NodeConfigUnavailableText => !Status.IsOnline
        ? Status.PresenceText
        : !CanEditNodeConfig
            ? "Aguardando eco do nó (requer firmware v11+)"
            : null;

    partial void OnOffsetMmTextChanged(string value) => ValidateNodeConfigAndRefresh();

    partial void OnSamplePeriodMsTextChanged(string value) => ValidateNodeConfigAndRefresh();

    partial void OnSendPeriodMsTextChanged(string value) => ValidateNodeConfigAndRefresh();

    partial void OnSensorEnabledChanged(bool value)
    {
        ValidateAndRefresh();
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnReferenceMillimetresTextChanged(string value) => ValidateAndRefresh();

    partial void OnStartDelaySecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnPulseSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnIntervalSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnAppliedSensorEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryGetStagedSettings(out var staged))
        {
            StatusText = ValidationError ?? "Revise os parâmetros de espuma.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.FoamControl(
            staged.SensorEnabled,
            staged.ReferenceMillimetres,
            staged.StartDelaySeconds,
            staged.PulseSeconds,
            staged.IntervalSeconds));

        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.IsCommRequested = staged.SensorEnabled;
        _committed = staged;
        _settings.Update(settings => settings with { FoamControl = staged });
        AppliedSensorEnabled = staged.SensorEnabled;
        AppliedReferenceMillimetres = staged.ReferenceMillimetres;
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

    [RelayCommand(CanExecute = nameof(CanSendNodeConfig))]
    private void SendNodeConfig()
    {
        if (!TryGetStagedNodeConfig(out var offset, out var samplePeriod, out var sendPeriod))
        {
            NodeConfigStatusText = NodeConfigValidationError ?? "Revise a configuração do nó.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.DistanceConfig(offset, samplePeriod, sendPeriod));
        if (!result.Accepted)
        {
            NodeConfigStatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        _committed = _committed with
        {
            DistanceOffsetMm = offset,
            DistanceSamplePeriodMs = samplePeriod,
            DistanceSendPeriodMs = sendPeriod,
        };
        _settings.Update(settings => settings with { FoamControl = _committed });
        BeginNodeConfigSync((offset, samplePeriod, sendPeriod));
        NodeConfigStatusText = "Configuração do sensor de distância enviada; aguardando confirmação.";
    }

    [RelayCommand(CanExecute = nameof(CanResetNodeConfig))]
    private void ResetNodeConfig()
    {
        var confirmed = _dialogs.ConfirmDestructive(
            "Restaurar padrões do sensor",
            "restaura offset 20 mm e períodos de fábrica no sensor",
            "Restaurar");

        if (!confirmed)
        {
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.DistanceResetNvs());
        if (!result.Accepted)
        {
            NodeConfigStatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        // Factory values the node restores (ConfigCodec.cpp reset_nvs): offset 20 mm, 1000/1000 ms.
        BeginNodeConfigSync((20.0, 1000, 1000));
        NodeConfigStatusText = "Restauração de padrões do sensor enviada; aguardando confirmação.";
    }

    private void BeginNodeConfigSync((double Offset, int Sample, int Send) expected)
    {
        _expectedNodeConfig = expected;
        _nodeConfigSentAt = _time.GetUtcNow();
        IsNodeConfigSyncing = true;
    }

    private void TrackNodeConfigEcho(SensorSnapshot snapshot)
    {
        if (_expectedNodeConfig is not { } expected)
        {
            return;
        }

        if (snapshot.DistanceOffsetMm is { } offset && snapshot.DistanceSamplePeriodMs is { } sample &&
            snapshot.DistanceSendPeriodMs is { } send &&
            Math.Abs(offset - expected.Offset) < 0.005 && sample == expected.Sample && send == expected.Send)
        {
            _expectedNodeConfig = null;
            IsNodeConfigSyncing = false;
            NodeConfigStatusText = $"Nó confirmou e gravou: offset {offset.ToString("F1", CultureInfo.CurrentCulture)} mm, " +
                                   $"amostragem {sample} ms, envio {send} ms.";
            return;
        }

        if (_time.GetUtcNow() - _nodeConfigSentAt > NodeConfigEchoTimeout)
        {
            _expectedNodeConfig = null;
            IsNodeConfigSyncing = false;
            NodeConfigStatusText = $"Aviso: o nó não ecoou a configuração pedida em {NodeConfigEchoTimeout.TotalSeconds:F0} s. " +
                                   "Compare os valores aplicados acima com o pedido antes de reenviar.";
        }
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
            DistanceOffsetMm = _committed.DistanceOffsetMm,
            DistanceSamplePeriodMs = _committed.DistanceSamplePeriodMs,
            DistanceSendPeriodMs = _committed.DistanceSendPeriodMs,
        };
        return true;
    }

    public bool TryGetStagedNodeConfig(out double offsetMm, out int samplePeriodMs, out int sendPeriodMs)
    {
        offsetMm = 20.0;
        samplePeriodMs = 1000;
        sendPeriodMs = 1000;

        if (!DosingInput.TryParseDouble(OffsetMmText, out offsetMm) || offsetMm is < -50.0 or > 200.0)
        {
            return false;
        }

        if (!DosingInput.TryParseInteger(SamplePeriodMsText, out samplePeriodMs) || samplePeriodMs is < 100 or > 60000)
        {
            return false;
        }

        if (!DosingInput.TryParseInteger(SendPeriodMsText, out sendPeriodMs) || sendPeriodMs is < 100 or > 60000)
        {
            return false;
        }

        return true;
    }

    private void Load(FoamControlSettings s)
    {
        SensorEnabled = s.SensorEnabled;
        ReferenceMillimetresText = DosingInput.Format(s.ReferenceMillimetres, 0);
        StartDelaySecondsText = DosingInput.FormatInt(s.StartDelaySeconds);
        PulseSecondsText = DosingInput.FormatInt(s.PulseSeconds);
        IntervalSecondsText = DosingInput.FormatInt(s.IntervalSeconds);
        OffsetMmText = DosingInput.Format(s.DistanceOffsetMm, 1);
        SamplePeriodMsText = DosingInput.FormatInt(s.DistanceSamplePeriodMs);
        SendPeriodMsText = DosingInput.FormatInt(s.DistanceSendPeriodMs);
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

    private void ValidateNodeConfigAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        NodeConfigValidationError = ValidateNodeConfig();
        OnPropertyChanged(nameof(IsValidNodeConfig));
        OnPropertyChanged(nameof(CanSendNodeConfig));
        SendNodeConfigCommand.NotifyCanExecuteChanged();
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

    private string? ValidateNodeConfig()
    {
        if (!DosingInput.TryParseDouble(OffsetMmText, out var offset) || offset is < -50.0 or > 200.0)
        {
            return "Offset: valor de -50 a 200 mm.";
        }

        if (!DosingInput.TryParseInteger(SamplePeriodMsText, out var sample) || sample is < 100 or > 60000)
        {
            return "Amostragem: inteiro de 100 a 60000 ms.";
        }

        if (!DosingInput.TryParseInteger(SendPeriodMsText, out var send) || send is < 100 or > 60000)
        {
            return "Envio: inteiro de 100 a 60000 ms.";
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
            snapshot.HasDistanceTelemetry,
            snapshot.DistanceOnline,
            pending: snapshot.DistanceCommandPending,
            snapshot.DistanceCommEnabled,
            snapshot.DistanceNode);

        LiveDistanceText = snapshot.Distance > SensorReadings.NotReceived
            ? snapshot.Distance.ToString("F0", CultureInfo.CurrentCulture)
            : "—";

        AppliedOffsetText = snapshot.DistanceOffsetMm is { } offset
            ? offset.ToString("F1", CultureInfo.CurrentCulture) + " mm"
            : "—";
        AppliedSamplePeriodMsText = snapshot.DistanceSamplePeriodMs is { } sample
            ? sample.ToString(CultureInfo.CurrentCulture) + " ms"
            : "—";
        AppliedSendPeriodMsText = snapshot.DistanceSendPeriodMs is { } send
            ? send.ToString(CultureInfo.CurrentCulture) + " ms"
            : "—";

        CanEditNodeConfig = snapshot.DistanceOffsetMm is not null;
        TrackNodeConfigEcho(snapshot);
        OnPropertyChanged(nameof(NodeConfigUnavailableText));
        SendNodeConfigCommand.NotifyCanExecuteChanged();
        ResetNodeConfigCommand.NotifyCanExecuteChanged();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
            CanEditNodeConfig = false;
            AppliedOffsetText = "—";
            AppliedSamplePeriodMsText = "—";
            AppliedSendPeriodMsText = "—";
            OnPropertyChanged(nameof(NodeConfigUnavailableText));
            SendNodeConfigCommand.NotifyCanExecuteChanged();
            ResetNodeConfigCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ExternalDeviceStatus.IsOnline) or nameof(ExternalDeviceStatus.CanSend) or null))
        {
            return;
        }

        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanSendNodeConfig));
        OnPropertyChanged(nameof(CanResetNodeConfig));
        OnPropertyChanged(nameof(NodeConfigUnavailableText));
        ApplyCommand.NotifyCanExecuteChanged();
        SendNodeConfigCommand.NotifyCanExecuteChanged();
        ResetNodeConfigCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= OnStatusChanged;
    }
}
