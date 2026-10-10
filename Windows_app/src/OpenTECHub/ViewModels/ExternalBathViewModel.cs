using System.Globalization;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>Operator surface for the Hub-routed Contemp C404 external bath.</summary>
/// <remarks>
/// The view-model deliberately keeps the reactor reference/PV apart from the C404
/// display SP/PV.  The Hub owns the outer PI; the app only sends route/mode commands,
/// the stop and explicit cascade tuning.  The reactor reference itself is the setpoint of
/// the "1. Temperatura" row: there is exactly one control for it.
///
/// Route and communication toggles show what the Hub reports (Hub 10.6), not the saved
/// preference: a Hub left on the external route by NVS, another client or a reinstall must
/// never appear as "UART" here, or the bath alarms would stay silent.  Only while a toggle
/// the operator just flipped is awaiting its echo does the requested value win.
/// </remarks>
public sealed partial class ExternalBathViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a just-dispatched toggle may disagree with the Hub echo.</summary>
    public static readonly TimeSpan EchoConfirmWindow = TimeSpan.FromSeconds(5);

    private readonly IDeviceService _device;
    private readonly ISettingsService? _settings;
    private readonly IManualDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private bool _initialised;
    private bool _syncingTelemetry;
    private bool? _routeEnabledOnHub;
    private string _cascadeState = "";
    private bool? _cascadeActiveOnHub;
    private bool _hubSupportsDirectSetpoint;

    /// <summary>First Hub firmware that accepts <c>bathSetpoint</c>.</summary>
    private static readonly Version DirectSetpointFirmware = new(10, 8, 0);
    private (bool Value, DateTimeOffset At)? _routeRequest;
    private (bool Value, DateTimeOffset At)? _commRequest;
    private (bool Value, DateTimeOffset At)? _modeRequest;
    private BathCascadeTuning? _tuningEcho;
    private string _tuningConfigError = "";
    private bool _tuningAwaitingEcho;
    private readonly PropertyChangedEventHandler _statusChangedHandler;

    public ExternalBathViewModel(
        IDeviceService device,
        ISettingsService? settings = null,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher(device);
        _time = timeProvider ?? TimeProvider.System;
        Status = new ExternalDeviceStatus("Banho externo C404", "do banho externo", _time)
        {
            NodeKind = NodeFirmwareCatalog.Bath,
        };
        _statusChangedHandler = (_, _) => RefreshCommands();
        Status.PropertyChanged += _statusChangedHandler;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;

        // Restore operator preferences as staged state only.  The guards in the generated
        // property callbacks ensure startup never emits a command automatically, and the
        // first Hub frame replaces route/communication with what the Hub actually does.
        if (settings?.Current.ExternalBath is { } saved)
        {
            IsTempControlViaBath = saved.PreferExternalRoute;
            IsCommEnabled = saved.CommunicationEnabled;
            IsAutomatic = saved.Automatic;
            IsPanelExpanded = saved.PanelExpanded;
            ShowTuning(new BathCascadeTuning(saved.CascadeKp, saved.CascadeTi, saved.CascadeBias,
                saved.CascadePeriodMs, saved.CascadeFilter, saved.CascadeMinCommand, saved.CascadeBand,
                saved.CascadeSlew, saved.CascadeOffsetHigh, saved.CascadeOffsetLow,
                saved.CascadeOutputMin, saved.CascadeOutputMax));
        }
        Status.IsCommRequested = IsCommEnabled;
        _initialised = true;
        RefreshCommands();
    }

    public ExternalDeviceStatus Status { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    [NotifyPropertyChangedFor(nameof(CanChangeRoute))]
    public partial bool IsTempControlViaBath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    [NotifyPropertyChangedFor(nameof(CanChangeCommunication))]
    public partial bool IsCommEnabled { get; set; }

    /// <summary>The node's guard: in auto it reverts panel changes to the last commanded SP.</summary>
    [ObservableProperty]
    public partial bool IsAutomatic { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPanelExpanded { get; set; } = true;

    [ObservableProperty]
    public partial string BathSyncText { get; set; } = "30.0";

    /// <summary>One-off C404 setpoint, sent only while the cascade is stopped (Hub 10.8).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendDirectSetpoint))]
    [NotifyPropertyChangedFor(nameof(DirectSetpointHint))]
    [NotifyCanExecuteChangedFor(nameof(SendDirectSetpointCommand))]
    public partial string DirectSetpointText { get; set; } = 30.0.ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>One-word state of the cascade for the summary tile.</summary>
    [ObservableProperty] public partial string CascadeHeadline { get; set; } = "—";

    /// <summary>The start delta the Hub is using now, from its tuning echo.</summary>
    [ObservableProperty] public partial string BiasEchoText { get; set; } = "";

    /// <summary>Probe reading and correction behind the reactor value, when a correction is set.</summary>
    [ObservableProperty] public partial string ReactorCorrectionText { get; set; } = "";

    [ObservableProperty] public partial string ReactorPvText { get; set; } = "—";
    [ObservableProperty] public partial string BathPvText { get; set; } = "—";
    [ObservableProperty] public partial string BathSpText { get; set; } = "—";
    [ObservableProperty] public partial string BathTargetText { get; set; } = "—";
    [ObservableProperty] public partial string BathStateText { get; set; } = "—";
    [ObservableProperty] public partial string BathPhaseText { get; set; } = "—";
    [ObservableProperty] public partial string BathErrorText { get; set; } = "—";
    [ObservableProperty] public partial string BathGuardText { get; set; } = "—";
    [ObservableProperty] public partial string CascadeStateText { get; set; } = "—";
    [ObservableProperty] public partial string CascadePvFilteredText { get; set; } = "—";
    [ObservableProperty] public partial string CascadeErrorText { get; set; } = "—";
    [ObservableProperty] public partial string CascadeOutputText { get; set; } = "—";
    [ObservableProperty] public partial string CascadeConfirmationText { get; set; } = "—";
    [ObservableProperty] public partial string CascadePIText { get; set; } = "—";
    [ObservableProperty] public partial string CascadePauseText { get; set; } = "—";
    [ObservableProperty] public partial string CascadeFaultText { get; set; } = "";
    [ObservableProperty] public partial string OwnershipText { get; set; } = "";
    [ObservableProperty] public partial string PreferenceMismatchText { get; set; } = "";
    [ObservableProperty] public partial string TuningStatusText { get; set; } = "";

    /// <summary>What the Hub currently reports about the bath (refreshed every frame).</summary>
    [ObservableProperty] public partial string StatusText { get; set; } = "Aguardando telemetria do Hub.";

    /// <summary>Outcome of the operator's last action; survives telemetry frames.</summary>
    [ObservableProperty] public partial string LastActionText { get; set; } = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeKpText { get; set; } = "0.5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeTiText { get; set; } = "600";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeBiasText { get; set; } = "0.6";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadePeriodText { get; set; } = "10000";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeFilterText { get; set; } = "20";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeMinCommandText { get; set; } = "30000";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeBandText { get; set; } = "0.1";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeSlewText { get; set; } = "0.5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeOffsetHighText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeOffsetLowText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeOutputMinText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))] [NotifyCanExecuteChangedFor(nameof(ApplyTuningCommand))]
    public partial string CascadeOutputMaxText { get; set; } = "90";

    /// <summary>Route confirmed by the Hub, communication on and the node present.</summary>
    public bool CanApplyNow => IsTempControlViaBath && _routeEnabledOnHub == true &&
                                IsCommEnabled && Status.HasTelemetry &&
                                Status.IsOnline && Status.CommEnabledOnHub == true;
    private bool CanEnableCommunication => Status.HasTelemetry;
    public bool CanChangeRoute => IsTempControlViaBath || Status.HasTelemetry;
    public bool CanChangeCommunication => IsCommEnabled || CanEnableCommunication;
    public bool CanChangeMode => CanApplyNow;

    /// <summary>The stop never waits for a pending command: it has priority on the Hub.</summary>
    public bool CanStop => Status.HasTelemetry && _routeEnabledOnHub == true;
    public bool CanResetFault => Status.HasTelemetry &&
                                 string.Equals(_cascadeState, "fault", StringComparison.OrdinalIgnoreCase);
    public bool CanApplyTuning => Status.HasTelemetry && TryReadTuning(out var draft, out _) &&
                                  !draft.SameAs(_tuningEcho);

    /// <summary>
    /// Explains where the temperature-row setpoint goes on the route the Hub reports, the
    /// same way the agitation route box explains UART/CN1 versus Modbus.
    /// </summary>
    public string RouteText => !Status.HasTelemetry
        ? "Hub sem telemetria do banho externo: a temperatura segue pela placa original (UART)."
        : _routeEnabledOnHub == true
            ? "Banho externo: o setpoint da linha é a referência do reator; o Hub calcula o SP do C404 pela cascata. Desligar a linha para a cascata e deixa o C404 em manual no último SP."
            : IsCommEnabled && Status.IsOnline
                ? "Banho original: o Hub envia o setpoint à placa do módulo por UART. O banho externo está online; a troca de via assume a cascata térmica."
                : "Banho original: o Hub envia o setpoint à placa do módulo por UART. Ao selecionar banho externo, o Hub habilita a comunicação e a cascata térmica.";

    /// <summary>Show the cascade controls only while the Hub routes temperature to the bath.</summary>
    public bool ShowBathDetails => _routeEnabledOnHub == true;

    /// <summary>The C404 readings and the direct setpoint make sense whenever the Hub talks to the bath.</summary>
    public bool ShowBathReadouts => Status.HasTelemetry && (_routeEnabledOnHub == true || Status.CommEnabledOnHub == true);

    public bool CanSendDirectSetpoint => DirectSetpointBlocker() is null;

    /// <summary>Why the direct setpoint is unavailable, or how it behaves when it is.</summary>
    public string DirectSetpointHint => DirectSetpointBlocker() ??
        "Envia o SP ao C404 uma vez. Com a guarda automática, o banho passa a manter este SP.";

    private string? DirectSetpointBlocker()
    {
        if (!Status.HasTelemetry) return "Sem telemetria do Hub.";
        if (!_hubSupportsDirectSetpoint) return "Requer Hub 10.8 ou mais recente (grave o firmware novo).";
        if (Status.CommEnabledOnHub != true) return "Ative a comunicação com o banho.";
        if (!Status.IsOnline) return "Banho C404 offline.";
        if (_cascadeActiveOnHub == true) return "A cascata está controlando o banho. Use Parar banho para enviar um SP direto.";
        if (!TryParse(DirectSetpointText, out var sp) || sp is < 0 or > 100) return "O SP do banho deve estar entre 0 e 100 °C.";
        return null;
    }

    partial void OnIsTempControlViaBathChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        if (value && !Status.HasTelemetry)
        {
            RevertRoute(value);
            LastActionText = "A via externa exige telemetria de um Hub compatível.";
            return;
        }

        if (value)
        {
            if (!IsCommEnabled)
            {
                IsCommEnabled = true;
                if (!IsCommEnabled)
                {
                    RevertRoute(value);
                    return;
                }
            }
            else if (Status.CommEnabledOnHub != true)
            {
                var commResult = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathCommunication(true));
                if (!commResult.Accepted)
                {
                    RevertRoute(value);
                    LastActionText = DispatchRefusal.Describe(commResult, _dispatcher);
                    return;
                }
                _commRequest = (true, _time.GetUtcNow());
                Status.IsCommRequested = true;
            }

            var routeResult = _dispatcher.DispatchSeparateFrame(CommandBuilders.TemperatureRoute(true));
            if (!routeResult.Accepted)
            {
                RevertRoute(value);
                LastActionText = DispatchRefusal.Describe(routeResult, _dispatcher);
                return;
            }
            _routeRequest = (true, _time.GetUtcNow());
            Status.MarkCommandDispatched();
            PersistPreferences();
            LastActionText = "Via externa solicitada; envie um novo setpoint do reator depois da confirmação.";
        }
        else
        {
            var routeResult = _dispatcher.DispatchSeparateFrame(CommandBuilders.TemperatureRoute(false));
            if (!routeResult.Accepted)
            {
                RevertRoute(value);
                LastActionText = DispatchRefusal.Describe(routeResult, _dispatcher);
                return;
            }
            _routeRequest = (false, _time.GetUtcNow());

            if (IsCommEnabled)
            {
                IsCommEnabled = false;
            }
            else if (Status.CommEnabledOnHub == true)
            {
                var commResult = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathCommunication(false));
                if (commResult.Accepted)
                {
                    _commRequest = (false, _time.GetUtcNow());
                    Status.IsCommRequested = false;
                }
            }

            Status.MarkCommandDispatched();
            PersistPreferences();
            LastActionText = "Via do módulo UART solicitada; o Hub para o banho (manual, último SP).";
        }
        RefreshCommands();
    }

    partial void OnIsCommEnabledChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        if (value && !CanEnableCommunication)
        {
            RevertCommunication(value);
            LastActionText = "A comunicação do banho exige telemetria de um Hub compatível.";
            return;
        }
        var result = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathCommunication(value));
        if (!result.Accepted)
        {
            RevertCommunication(value);
            LastActionText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }
        _commRequest = (value, _time.GetUtcNow());
        Status.IsCommRequested = value;
        Status.MarkCommandDispatched();
        PersistPreferences();
        LastActionText = value ? "Comunicação do banho habilitada." : "Comunicação do banho desabilitada.";
        RefreshCommands();
    }

    partial void OnIsAutomaticChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        if (!CanChangeMode)
        {
            RevertMode(value);
            LastActionText = "A guarda do C404 só pode ser alterada com a via externa confirmada e o banho online.";
            return;
        }
        var result = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathMode(value));
        if (!result.Accepted)
        {
            RevertMode(value);
            LastActionText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }
        _modeRequest = (value, _time.GetUtcNow());
        Status.MarkCommandDispatched();
        PersistPreferences();
        LastActionText = value
            ? "Guarda automática do C404 solicitada (reverte mudanças do painel)."
            : "Guarda manual do C404 solicitada; a cascata aguarda enquanto o nó estiver em manual.";
    }

    partial void OnIsPanelExpandedChanged(bool value)
    {
        if (_initialised && !_syncingTelemetry)
        {
            PersistPreferences();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private void Synchronize()
    {
        if (!TryParse(BathSyncText, out var value) || value is < 0 or > 100)
        {
            LastActionText = "Sincronização deve estar entre 0 e 100 °C.";
            return;
        }
        Dispatch(CommandBuilders.BathSynchronize(value), "Setpoint do C404 sincronizado.");
    }

    [RelayCommand(CanExecute = nameof(CanSendDirectSetpoint))]
    private void SendDirectSetpoint()
    {
        if (DirectSetpointBlocker() is not null || !TryParse(DirectSetpointText, out var sp))
        {
            LastActionText = DirectSetpointBlocker() ?? "Revise o SP do banho.";
            return;
        }
        Dispatch(CommandBuilders.BathSetpoint(sp),
            string.Create(CultureInfo.CurrentCulture, $"SP {sp:0.0} °C enviado ao C404; acompanhe SP e alvo do nó."));
    }

    /// <summary>Bath stop (Hub 10.6): cascade off, node aborted and left in manual.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => Dispatch(CommandBuilders.BathAbort(),
        "Parada do banho enviada: cascata desligada, C404 em manual no último SP.");

    [RelayCommand(CanExecute = nameof(CanResetFault))]
    private void ResetFault() => Dispatch(CommandBuilders.BathCascadeReset(),
        "Reset da falha enviado; a guarda do C404 volta ao automático.");

    [RelayCommand(CanExecute = nameof(CanApplyTuning))]
    private void ApplyTuning()
    {
        if (!TryReadTuning(out var tuning, out var error))
        {
            LastActionText = error ?? "Revise os parâmetros da cascata.";
            return;
        }
        var result = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathCascadeTuning(tuning));
        if (!result.Accepted) { LastActionText = DispatchRefusal.Describe(result, _dispatcher); return; }
        Status.MarkCommandDispatched();
        PersistPreferences(includeTuning: true);
        _tuningAwaitingEcho = true;
        LastActionText = "Sintonia enviada; aguardando eco do Hub.";
        RefreshTuningStatus();
    }

    private void Dispatch(OpenTECCommand command, string success)
    {
        var result = _dispatcher.DispatchSeparateFrame(command);
        if (!result.Accepted) { LastActionText = DispatchRefusal.Describe(result, _dispatcher); return; }
        Status.MarkCommandDispatched();
        LastActionText = success;
    }

    private void PersistPreferences(bool includeTuning = false)
    {
        if (_settings is null)
        {
            return;
        }

        var tuning = includeTuning && TryReadTuning(out var draft, out _) ? draft : null;
        _settings.Update(current =>
        {
            var saved = current.ExternalBath ?? new ExternalBathSettings();
            return current with
            {
                ExternalBath = saved with
                {
                    PreferExternalRoute = IsTempControlViaBath,
                    CommunicationEnabled = IsCommEnabled,
                    Automatic = IsAutomatic,
                    PanelExpanded = IsPanelExpanded,
                    CascadeKp = tuning?.Kp ?? saved.CascadeKp,
                    CascadeTi = tuning?.TiS ?? saved.CascadeTi,
                    CascadeBias = tuning?.BiasC ?? saved.CascadeBias,
                    CascadePeriodMs = tuning?.PeriodMs ?? saved.CascadePeriodMs,
                    CascadeFilter = tuning?.FilterS ?? saved.CascadeFilter,
                    CascadeMinCommand = tuning?.CommandMinMs ?? saved.CascadeMinCommand,
                    CascadeBand = tuning?.CommandBandC ?? saved.CascadeBand,
                    CascadeSlew = tuning?.SlewCMin ?? saved.CascadeSlew,
                    CascadeOffsetHigh = tuning?.OffsetHighC ?? saved.CascadeOffsetHigh,
                    CascadeOffsetLow = tuning?.OffsetLowC ?? saved.CascadeOffsetLow,
                    CascadeOutputMin = tuning?.OutputMinC ?? saved.CascadeOutputMin,
                    CascadeOutputMax = tuning?.OutputMaxC ?? saved.CascadeOutputMax,
                },
            };
        });
    }

    /// <summary>Parses the draft and runs the Hub's own validation.</summary>
    private bool TryReadTuning(out BathCascadeTuning tuning, out string? error)
    {
        tuning = BathCascadeTuning.Defaults;
        error = null;
        if (!TryParse(CascadeKpText, out var kp) || !TryParse(CascadeTiText, out var ti) ||
            !TryParse(CascadeBiasText, out var bias) || !TryParseInt(CascadePeriodText, out var period) ||
            !TryParse(CascadeFilterText, out var filter) || !TryParseInt(CascadeMinCommandText, out var min) ||
            !TryParse(CascadeBandText, out var band) || !TryParse(CascadeSlewText, out var slew) ||
            !TryParse(CascadeOffsetHighText, out var high) || !TryParse(CascadeOffsetLowText, out var low) ||
            !TryParse(CascadeOutputMinText, out var outputMin) || !TryParse(CascadeOutputMaxText, out var outputMax))
        {
            error = "Todos os parâmetros da cascata precisam ser números.";
            return false;
        }

        tuning = new BathCascadeTuning(kp, ti, bias, period, filter, min, band, slew, high, low, outputMin, outputMax);
        error = tuning.Validate();
        return error is null;
    }

    private void ShowTuning(BathCascadeTuning tuning)
    {
        CascadeKpText = tuning.Kp.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeTiText = tuning.TiS.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeBiasText = tuning.BiasC.ToString("0.###", CultureInfo.CurrentCulture);
        CascadePeriodText = tuning.PeriodMs.ToString(CultureInfo.CurrentCulture);
        CascadeFilterText = tuning.FilterS.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeMinCommandText = tuning.CommandMinMs.ToString(CultureInfo.CurrentCulture);
        CascadeBandText = tuning.CommandBandC.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeSlewText = tuning.SlewCMin.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeOffsetHighText = tuning.OffsetHighC.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeOffsetLowText = tuning.OffsetLowC.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeOutputMinText = tuning.OutputMinC.ToString("0.###", CultureInfo.CurrentCulture);
        CascadeOutputMaxText = tuning.OutputMaxC.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static bool TryParse(string text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) ||
           int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>True while a just-sent toggle may still disagree with the Hub echo.</summary>
    private bool AwaitingEcho(ref (bool Value, DateTimeOffset At)? request, bool reported)
    {
        if (request is not { } pending) return false;
        if (pending.Value == reported || _time.GetUtcNow() - pending.At >= EchoConfirmWindow)
        {
            request = null;
            return false;
        }
        return true;
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Status.Update(snapshot.HasBathTelemetry, snapshot.BathOnline, snapshot.BathCommandPending,
            snapshot.BathCommEnabled, snapshot.BathNode);
        _routeEnabledOnHub = snapshot.TempControlViaBath;
        _cascadeState = snapshot.BathCascadeState;
        _cascadeActiveOnHub = snapshot.BathCascadeActive;
        _hubSupportsDirectSetpoint = HubAtLeast(snapshot.HubFirmwareVersion, DirectSetpointFirmware);

        _syncingTelemetry = true;
        var routeMismatchResolved = false;
        if (snapshot.TempControlViaBath is { } via && !AwaitingEcho(ref _routeRequest, via))
        {
            routeMismatchResolved = IsTempControlViaBath != via;
            IsTempControlViaBath = via;
        }
        if (snapshot.BathCommEnabled is { } comm && !AwaitingEcho(ref _commRequest, comm))
        {
            IsCommEnabled = comm;
            Status.IsCommRequested = comm;
        }
        if (snapshot.BathMode is { } mode && !AwaitingEcho(ref _modeRequest, mode == 1))
        {
            IsAutomatic = mode == 1;
        }
        _syncingTelemetry = false;
        if (routeMismatchResolved && !string.IsNullOrEmpty(LastActionText) &&
            LastActionText.Contains("solicitada", StringComparison.Ordinal))
        {
            LastActionText = "O Hub não confirmou a troca de via; a chave mostra a via real.";
        }

        var saved = _settings?.Current.ExternalBath;
        PreferenceMismatchText = saved is not null && snapshot.TempControlViaBath is { } hubVia &&
                                 saved.PreferExternalRoute != hubVia
            ? $"Preferência salva: {(saved.PreferExternalRoute ? "banho externo" : "módulo UART")}; " +
              $"o Hub está em {(hubVia ? "banho externo" : "módulo UART")}."
            : "";

        ReactorPvText = snapshot.TemperatureValid == false ? "—" : Format(snapshot.Temperature);
        var correction = snapshot.Temperature - snapshot.TemperatureRaw;
        ReactorCorrectionText = snapshot.TemperatureValid != false && Math.Abs(correction) > 0.005 &&
                                snapshot.TemperatureRaw > SensorReadings.NotReceived
            ? string.Create(CultureInfo.CurrentCulture, $"lida {snapshot.TemperatureRaw:F2} · correção {correction:+0.0#;−0.0#}")
            : "";
        BathPvText = Format(snapshot.BathPv);
        BathSpText = Format(snapshot.BathSp);
        BathTargetText = Format(snapshot.BathTarget);
        BathStateText = EmptyDash(snapshot.BathState);
        BathPhaseText = EmptyDash(snapshot.BathPhase);
        BathErrorText = EmptyDash(snapshot.BathError);
        BathGuardText = EmptyDash(snapshot.BathGuard);
        CascadeStateText = EmptyDash(snapshot.BathCascadeState);
        CascadePvFilteredText = Format(snapshot.BathCascadePvFiltered);
        CascadeErrorText = Format(snapshot.BathCascadeError);
        CascadeOutputText = Format(snapshot.BathCommandSetpoint);
        CascadeConfirmationText = Format(snapshot.BathCommandConfirmed);
        CascadePIText = snapshot.BathCascadeP is { } p && snapshot.BathCascadeI is { } i
            ? snapshot.BathCascadeFine == false ? $"PI parado / I {i:F2}" : $"P {p:F2} / I {i:F2}"
            : "—";
        CascadePauseText = EmptyDash(snapshot.BathCascadePausedReason);
        CascadeFaultText = string.Equals(snapshot.BathCascadeState, "fault", StringComparison.OrdinalIgnoreCase)
            ? $"Falha da cascata: {DescribeReason(FirstNonEmpty(snapshot.BathCascadeFaultReason, snapshot.BathCascadePausedReason))}. " +
              "Corrija a causa e use Reset falha, ou Parar banho."
            : "";
        OwnershipText = snapshot.BathOwned switch
        {
            true => "Hub controla o banho: comandos locais (celular, /ui) ficam bloqueados; só Abortar.",
            _ => "",
        };
        CascadeHeadline = snapshot.BathCascadeState switch
        {
            "controlling" => "Controlando (PI)",
            "approaching" => "Aproximação",
            "actuator_busy" => "Aguardando C404",
            "waiting_inputs" => "Aguardando",
            "paused" => "Pausada",
            "fault" => "Falha",
            "initializing" => "Iniciando",
            "off" => "Parada",
            _ => "—",
        };
        StatusText = snapshot.BathCascadeState switch
        {
            "waiting_inputs" => $"Cascata aguardando: {DescribeReason(snapshot.BathCascadePausedReason)}.",
            "paused" => $"Cascata pausada: {DescribeReason(snapshot.BathCascadePausedReason)}.",
            "fault" => "Cascata em falha.",
            "controlling" => "Cascata controlando o reator.",
            "approaching" => "Cascata em aproximação: banho em referência + delta inicial; o PI entra perto da " +
                             $"referência com o reator estável{DescribeSlope(snapshot.BathCascadeSlopeCMin)}.",
            "actuator_busy" => "Cascata aguardando o C404 concluir o comando.",
            "off" when snapshot.TempControlViaBath == true =>
                "Sem referência do reator: o C404 fica no último SP. Envie um setpoint do reator para a cascata assumir, ou use o SP direto.",
            _ => Status.StatusText,
        };

        UpdateTuningEcho(snapshot);
        RefreshCommands();
    }

    private void UpdateTuningEcho(SensorSnapshot snapshot)
    {
        _tuningConfigError = snapshot.BathCascadeConfigError ?? "";
        var echo = snapshot.BathCascadeConfig;
        BiasEchoText = echo is null
            ? "Hub sem eco da sintonia."
            : TryParse(CascadeBiasText, out var draftBias) && Math.Abs(draftBias - echo.BiasC) < 0.005
                ? "Em uso no Hub."
                : string.Create(CultureInfo.CurrentCulture, $"Hub usa {echo.BiasC:+0.0#;−0.0#;0.0} °C — clique Enviar delta.");
        if (echo is not null && !echo.SameAs(_tuningEcho))
        {
            // The Hub's values are the truth: follow them unless the operator is editing a
            // draft that differs from the previous echo (or is waiting for one just sent).
            var draftOk = TryReadTuning(out var draft, out _);
            var draftUntouched = _tuningEcho is null
                ? !_tuningAwaitingEcho
                : draftOk && draft.SameAs(_tuningEcho);
            var confirmsDraft = draftOk && draft.SameAs(echo);
            _tuningEcho = echo;
            if (draftUntouched || confirmsDraft)
            {
                _syncingTelemetry = true;
                ShowTuning(echo);
                _syncingTelemetry = false;
            }
            if (confirmsDraft && _tuningAwaitingEcho)
            {
                _tuningAwaitingEcho = false;
                LastActionText = "Sintonia confirmada pelo Hub.";
            }
        }
        if (_tuningAwaitingEcho && _tuningConfigError.Length > 0)
        {
            _tuningAwaitingEcho = false;
            LastActionText = $"O Hub recusou a sintonia ({DescribeReason(_tuningConfigError)}).";
        }
        RefreshTuningStatus();
    }

    private void RefreshTuningStatus()
    {
        var valid = TryReadTuning(out var draft, out var error);
        TuningStatusText = !valid
            ? error ?? ""
            : _tuningEcho is null
                ? "Hub sem eco da sintonia (firmware anterior a 10.6)."
                : draft.SameAs(_tuningEcho)
                    ? "Sintonia vigente no Hub."
                    : "Rascunho diferente da sintonia vigente no Hub.";
    }

    /// <summary>True when the Hub's version string (e.g. <c>10.8.0-dev</c>) is at least <paramref name="minimum"/>.</summary>
    internal static bool HubAtLeast(string? firmware, Version minimum)
    {
        if (string.IsNullOrWhiteSpace(firmware)) return false;
        var core = firmware.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var version) && version >= minimum;
    }

    private static string FirstNonEmpty(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? b ?? "" : a;

    /// <summary>pt-BR text for the Hub's reason codes; see <see cref="BathReasons"/>.</summary>
    public static string DescribeReason(string? code) => BathReasons.Describe(code);

    private static string DescribeSlope(double? slopeCMin)
        => slopeCMin is { } slope && double.IsFinite(slope)
            ? string.Create(CultureInfo.InvariantCulture, $" (reator variando {slope:+0.00;-0.00} °C/min)")
            : "";

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State is ConnectionState.Disconnected or ConnectionState.Faulted)
            Status.MarkHubUnavailable();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        SynchronizeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetFaultCommand.NotifyCanExecuteChanged();
        ApplyTuningCommand.NotifyCanExecuteChanged();
        SendDirectSetpointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendDirectSetpoint));
        OnPropertyChanged(nameof(DirectSetpointHint));
        OnPropertyChanged(nameof(ShowBathReadouts));
        OnPropertyChanged(nameof(CanApplyNow));
        OnPropertyChanged(nameof(CanChangeRoute));
        OnPropertyChanged(nameof(CanChangeCommunication));
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanResetFault));
        OnPropertyChanged(nameof(CanApplyTuning));
        OnPropertyChanged(nameof(RouteText));
        OnPropertyChanged(nameof(ShowBathDetails));
    }

    private static string Format(double? value)
        => value is { } number && double.IsFinite(number) ? number.ToString("F2", CultureInfo.CurrentCulture) : "—";

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void RevertRoute(bool attempted)
    {
        _syncingTelemetry = true;
        IsTempControlViaBath = !attempted;
        _syncingTelemetry = false;
        RefreshCommands();
    }

    private void RevertCommunication(bool attempted)
    {
        _syncingTelemetry = true;
        IsCommEnabled = !attempted;
        _syncingTelemetry = false;
        RefreshCommands();
    }

    private void RevertMode(bool attempted)
    {
        _syncingTelemetry = true;
        IsAutomatic = !attempted;
        _syncingTelemetry = false;
        RefreshCommands();
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= _statusChangedHandler;
    }
}
