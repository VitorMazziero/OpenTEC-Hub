using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Staged and observed state for the Phase 1 flowmeter valves and ceiling.
/// </summary>
/// <remarks>
/// Valve commands are always emitted as a complete desired flow state. A one-key
/// valve write would recreate the overwrite-and-consume failure mode of the legacy
/// hub: a later flow setpoint could silently restore stale valve values. The active-high
/// main shutoff <c>v_Flow</c> is independently staged with the same complete command.
/// </remarks>
public sealed partial class FlowControlViewModel : ObservableObject
{
    private IManualDispatcher? _dispatcher;
    private ISettingsService? _settings;
    private bool _initialised;
    private bool _suppressRefresh;
    private bool _telemetryInitialised;
    private bool _appliedValve1;
    private bool _appliedValve2;
    private bool _appliedMainValveClosed;
    private double _appliedMaxFlow;

    public FlowControlViewModel(
        double initialMaxFlow,
        IManualDispatcher? dispatcher = null,
        ISettingsService? settings = null,
        TimeProvider? timeProvider = null)
    {
        if (!double.IsFinite(initialMaxFlow) || initialMaxFlow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialMaxFlow));
        }

        _dispatcher = dispatcher;
        _settings = settings;
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
        }
        Status = new ExternalDeviceStatus("Fluxômetro", "do fluxômetro", timeProvider) { NodeKind = NodeFirmwareCatalog.Flowmeter };

        _appliedMaxFlow = initialMaxFlow;
        MaxFlowText = Format(initialMaxFlow);
        LoadTuning(_settings?.Current.FlowControl ?? new FlowControlSettings());
        Validate();
        RefreshTuningState();
        _initialised = true;
    }

    public void AttachDispatcher(IManualDispatcher dispatcher, ISettingsService? settings = null)
    {
        _dispatcher = dispatcher;
        if (settings is not null)
        {
            if (!ReferenceEquals(settings, _settings))
            {
                if (_settings is not null)
                {
                    _settings.Changed -= OnSettingsChanged;
                }
                _settings = settings;
                _settings.Changed += OnSettingsChanged;
            }
            LoadTuning(_settings.Current.FlowControl);
            RefreshTuningState();
            NotifyRigChanged();
        }
    }

    /// <summary>Configurações › Gás e válvulas may have moved A to the other input: re-read every derived text.</summary>
    private void OnSettingsChanged(AppSettings _) => NotifyRigChanged();

    /// <summary>The wiring changed in Configurações: every text derived from it re-reads.</summary>
    public void NotifyRigChanged()
    {
        OnPropertyChanged(nameof(RequestedRoute));
        OnPropertyChanged(nameof(RequestedRouteText));
        OnPropertyChanged(nameof(IsRouteClosed));
        OnPropertyChanged(nameof(IsRouteReactor));
        OnPropertyChanged(nameof(IsRouteVentAndNitrogen));
        OnPropertyChanged(nameof(Input1Label));
        OnPropertyChanged(nameof(Input2Label));
        OnPropertyChanged(nameof(Input1Choice));
        OnPropertyChanged(nameof(Input2Choice));
        OnPropertyChanged(nameof(RigDescription));
        NotifyObservedRoute();
    }

    /// <summary>
    /// The shared external-device status, so the flowmeter row renders the same chips as
    /// every other external device.
    /// </summary>
    /// <remarks>
    /// The flowmeter kept its own hand-rolled status properties because its Hub flags predate
    /// the shared contract and its bindings are pinned to those names. Those stay; this is
    /// added beside them, mirroring the same state, so the row is not the one place on the page
    /// that renders presence differently — and so it gains the routing chip, which it needs for
    /// exactly the reason every other device does.
    /// </remarks>
    public ExternalDeviceStatus Status { get; }

    /// <summary>
    /// Flowmeter input 1, as staged. On the A/B/C rig an input is a destination, not a gas:
    /// see <see cref="RequestedRoute"/> for what the pair means. Raw here for Avançado, which
    /// may stage any combination — free operation has no interlock (plan §3.3).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    [NotifyPropertyChangedFor(nameof(RequestedRoute))]
    [NotifyPropertyChangedFor(nameof(RequestedRouteText))]
    [NotifyPropertyChangedFor(nameof(IsRouteClosed))]
    [NotifyPropertyChangedFor(nameof(IsRouteReactor))]
    [NotifyPropertyChangedFor(nameof(IsRouteVentAndNitrogen))]
    [NotifyPropertyChangedFor(nameof(IsBothRequested))]
    [NotifyPropertyChangedFor(nameof(IsInput1Requested))]
    [NotifyPropertyChangedFor(nameof(IsInput2Requested))]
    public partial bool RequestedValve1 { get; set; }

    /// <summary>Flowmeter input 2, as staged. See <see cref="RequestedValve1"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    [NotifyPropertyChangedFor(nameof(RequestedRoute))]
    [NotifyPropertyChangedFor(nameof(RequestedRouteText))]
    [NotifyPropertyChangedFor(nameof(IsRouteClosed))]
    [NotifyPropertyChangedFor(nameof(IsRouteReactor))]
    [NotifyPropertyChangedFor(nameof(IsRouteVentAndNitrogen))]
    [NotifyPropertyChangedFor(nameof(IsBothRequested))]
    [NotifyPropertyChangedFor(nameof(IsInput1Requested))]
    [NotifyPropertyChangedFor(nameof(IsInput2Requested))]
    public partial bool RequestedValve2 { get; set; }

    // ── Gas destination (A / B+C / closed) over the two staged inputs ────────

    /// <summary>
    /// The destination the staged pair means on the configured wiring, or null when both
    /// inputs are staged open — a state the selector has no button for and Avançado can
    /// still send.
    /// </summary>
    public GasRoute? RequestedRoute => (RequestedValve1, RequestedValve2) switch
    {
        (false, false) => GasRoute.Closed,
        (true, true) => null,
        var (v1, v2) => GasRouting.Resolve(GasRoute.Reactor, Rig) == (v1, v2) ? GasRoute.Reactor : GasRoute.VentAndNitrogen,
    };

    /// <summary>The selector's text: the hardware's names, or the anomaly when both are staged.</summary>
    public string RequestedRouteText => RequestedRoute is { } route
        ? GasRouting.Describe(route)
        : GasRouting.Describe(ObservedGasRoute.BothOpen);

    public bool IsBothRequested => RequestedValve1 && RequestedValve2;

    public bool IsRouteClosed
    {
        get => RequestedRoute == GasRoute.Closed;
        set { if (value) { SelectRoute(GasRoute.Closed); } }
    }

    public bool IsRouteReactor
    {
        get => RequestedRoute == GasRoute.Reactor;
        set { if (value) { SelectRoute(GasRoute.Reactor); } }
    }

    public bool IsRouteVentAndNitrogen
    {
        get => RequestedRoute == GasRoute.VentAndNitrogen;
        set { if (value) { SelectRoute(GasRoute.VentAndNitrogen); } }
    }

    // The selector is worded by input, not by gas: the valves' roles never change (A = air to
    // the reactor, B = N₂ or nothing, C = air purge) — what the operator picks is which
    // flowmeter input is energised, 1 or 2, and the label says what hangs on it.

    /// <summary>Input 1 energised alone (whatever hangs on it on the configured wiring).</summary>
    public bool IsInput1Requested
    {
        get => RequestedValve1 && !RequestedValve2;
        set { if (value) { SelectInputs(true, false); } }
    }

    /// <summary>Input 2 energised alone.</summary>
    public bool IsInput2Requested
    {
        get => RequestedValve2 && !RequestedValve1;
        set { if (value) { SelectInputs(false, true); } }
    }

    private void SelectInputs(bool v1, bool v2)
    {
        _suppressRefresh = true;
        RequestedValve1 = v1;
        RequestedValve2 = v2;
        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>What each input drives, for the selector: "Entrada 2 · A (ar ao reator)".</summary>
    public string Input1Choice => "Entrada 1 · " + RoleOf(GasInput.Input1);
    public string Input2Choice => "Entrada 2 · " + RoleOf(GasInput.Input2);

    private string RoleOf(GasInput input) => Rig.AirInletInput == input
        ? "A (ar ao reator)"
        : "B + C (N₂ ou nada · purga de ar)";

    /// <summary>Stages a destination: the pair of inputs it resolves to on the configured wiring.</summary>
    public void SelectRoute(GasRoute route)
    {
        var (v1, v2) = GasRouting.Resolve(route, Rig);
        _suppressRefresh = true;
        RequestedValve1 = v1;
        RequestedValve2 = v2;
        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>
    /// Free operation sends anything; this is the word of warning beside it (plan §3.3). Null
    /// when the staged pair is a plain destination.
    /// </summary>
    public string? RouteWarningFor(bool flowEnabled, double setpoint)
    {
        if (IsBothRequested)
        {
            return "Entradas 1 e 2 acionadas ao mesmo tempo: A e B + C abertas juntas, o ar se divide entre o reator e a purga.";
        }

        if (RequestedRoute == GasRoute.Closed && flowEnabled && setpoint > 0.0 && !RequestedMainValveClosed)
        {
            return "Gás sem destino: setpoint acima de zero com as entradas 1 e 2 fechadas — a linha fica sem saída.";
        }

        return null;
    }

    /// <summary>Labels for the raw inputs in Avançado, derived from the wiring.</summary>
    public string Input1Label => Rig.AirInletInput == GasInput.Input1 ? "Entrada 1 (A)" : "Entrada 1 (B + C)";
    public string Input2Label => Rig.AirInletInput == GasInput.Input2 ? "Entrada 2 (A)" : "Entrada 2 (B + C)";

    /// <summary>The wiring in force, from Configurações › Gás e válvulas.</summary>
    public string RigDescription => $"Arranjo: {Rig.Describe()}";

    // ── Observed route, from the echo ────────────────────────────────────────

    private double _observedSetpoint;

    /// <summary>What the flowmeter reports it is doing, read as a destination; null before the first frame.</summary>
    public ObservedGasRoute? ObservedRoute => ActualValve1 is { } v1 && ActualValve2 is { } v2
        ? GasRouting.Interpret(v1, v2, _observedSetpoint, Rig)
        : null;

    public string ObservedRouteText => ObservedRoute is { } observed ? GasRouting.Describe(observed) : "—";

    /// <summary>Dead end or both open: the telemetry itself says something is wrong.</summary>
    public bool IsObservedRouteAnomalous => ObservedRoute is { } observed && !GasRouting.IsNominal(observed);

    /// <summary>The raw pair with its meaning, e.g. <c>valve_1=0 (B/C) · valve_2=1 (A)</c>.</summary>
    public string WireText => ActualValve1 is { } v1 && ActualValve2 is { } v2 ? GasRouting.DescribeWire(v1, v2, Rig) : "—";

    private void NotifyObservedRoute()
    {
        OnPropertyChanged(nameof(ObservedRoute));
        OnPropertyChanged(nameof(ObservedRouteText));
        OnPropertyChanged(nameof(IsObservedRouteAnomalous));
        OnPropertyChanged(nameof(WireText));
    }

    /// <summary>
    /// Main physical shutoff. Closing it stops gas without erasing the staged flow setpoint.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    public partial bool RequestedMainValveClosed { get; set; }

    /// <summary>Flowmeter ceiling staged in the Controle page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string MaxFlowText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Valve1ActualText))]
    public partial bool? ActualValve1 { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Valve2ActualText))]
    public partial bool? ActualValve2 { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainValveActualText))]
    public partial bool? ActualVentValve { get; set; }

    /// <summary>True while the Hub still owns an unacknowledged v05 command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingStatusText))]
    public partial bool IsFlowCommandPending { get; set; }

    /// <summary>
    /// UI lock set immediately on local dispatch and released by the next Hub frame that reports
    /// <c>FlowCommandPending=false</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    [NotifyPropertyChangedFor(nameof(PendingStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    public partial bool IsAwaitingAck { get; set; }

    /// <summary>State of the Hub-to-flowmeter wireless link, distinct from the app-to-Hub link.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowmeterOffline))]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    [NotifyPropertyChangedFor(nameof(FlowmeterStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    [NotifyPropertyChangedFor(nameof(TuningUnavailableText))]
    public partial bool IsFlowmeterOnline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowmeterOffline))]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    [NotifyPropertyChangedFor(nameof(FlowmeterStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    public partial bool HasFlowmeterTelemetry { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    [NotifyPropertyChangedFor(nameof(IsOwnedByOther))]
    [NotifyPropertyChangedFor(nameof(HasOwnerBadge))]
    [NotifyPropertyChangedFor(nameof(OwnerBadgeText))]
    [NotifyPropertyChangedFor(nameof(OwnerLockReason))]
    public partial CommandOwner CurrentOwner { get; set; } = CommandOwner.Manual;

    [ObservableProperty]
    public partial string KpText { get; set; } = "0.8";

    [ObservableProperty]
    public partial string KiText { get; set; } = "0.15";

    [ObservableProperty]
    public partial string FfGainText { get; set; } = "0.106";

    [ObservableProperty]
    public partial string FfOffsetText { get; set; } = "0.01033";

    [ObservableProperty]
    public partial string RampRateText { get; set; } = "2.0";

    [ObservableProperty]
    public partial string AppliedKpText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedKiText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedFfGainText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedFfOffsetText { get; set; } = "—";

    [ObservableProperty]
    public partial string AppliedRampRateText { get; set; } = "—";

    [ObservableProperty]
    public partial string FlowOutputText { get; set; } = "—";

    [ObservableProperty]
    public partial string FlowSetpointCorrectedText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    [NotifyPropertyChangedFor(nameof(TuningUnavailableText))]
    public partial bool CanEditTuning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTuningValid))]
    [NotifyPropertyChangedFor(nameof(CanSendTuning))]
    public partial string? TuningValidationError { get; set; }

    [ObservableProperty]
    public partial string? FlowPlausibilityWarning { get; set; }

    [ObservableProperty]
    public partial string TuningStatusText { get; set; } =
        "Sintonia restaurada para revisão; nenhum comando foi enviado.";

    public bool IsTuningValid => TuningValidationError is null;

    public bool CanSendTuning => CanSendFlowCommands && CanEditTuning && IsTuningValid;

    public string? TuningUnavailableText => !IsFlowmeterOnline
        ? FlowmeterStatusText
        : !CanEditTuning
            ? "Aguardando telemetria do nó (requer firmware v11+)"
            : null;

    partial void OnKpTextChanged(string value) => RefreshTuningState();
    partial void OnKiTextChanged(string value) => RefreshTuningState();
    partial void OnFfGainTextChanged(string value) => RefreshTuningState();
    partial void OnFfOffsetTextChanged(string value) => RefreshTuningState();
    partial void OnRampRateTextChanged(string value) => RefreshTuningState();

    partial void OnCurrentOwnerChanged(CommandOwner value)
    {
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAwaitingAckChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFlowmeterOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendTuning));
        OnPropertyChanged(nameof(TuningUnavailableText));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasFlowmeterTelemetryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    public bool IsOwnedByOther => CurrentOwner != CommandOwner.Manual;
    public bool HasOwnerBadge => IsOwnedByOther;
    public string? OwnerBadgeText => OwnershipUi.GetBadgeText(CurrentOwner);
    public string? OwnerLockReason => OwnershipUi.GetLockReason(CurrentOwner);

    public double AppliedMaxFlow => _appliedMaxFlow;

    /// <summary>Valid staged ceiling, or the last applied value while the field is invalid.</summary>
    public double MaximumForCommand => CommandMaximum;

    public bool IsValid => ValidationError is null;

    public bool IsFlowmeterOffline => HasFlowmeterTelemetry && !IsFlowmeterOnline;

    public bool CanSendFlowCommands => HasFlowmeterTelemetry && IsFlowmeterOnline && !IsAwaitingAck && !IsOwnedByOther;

    public bool HasFlowStatusAlert => HasFlowmeterTelemetry && (IsFlowmeterOffline || IsAwaitingAck);

    public bool ShowPendingChip => HasFlowmeterTelemetry && IsFlowmeterOnline && IsAwaitingAck;

    public string PendingStatusText => IsAwaitingAck
        ? "Aguardando confirmação do fluxômetro..."
        : "Nenhum comando de vazão pendente.";

    public string FlowmeterStatusText => !HasFlowmeterTelemetry
        ? "Aguardando telemetria do Hub"
        : IsFlowmeterOnline
            ? "Online"
            : "Fluxômetro Desconectado da Central";

    public string FlowStatusText => IsFlowmeterOffline
        ? FlowmeterStatusText
        : IsAwaitingAck
            ? PendingStatusText
            : "Fluxômetro online; comandos liberados.";

    public bool HasPendingChange
        => !TryGetStagedMaxFlow(out var maximum) ||
           Math.Abs(maximum - _appliedMaxFlow) > 1e-9 ||
           RequestedValve1 != _appliedValve1 ||
           RequestedValve2 != _appliedValve2 ||
           RequestedMainValveClosed != _appliedMainValveClosed;

    public string Valve1ActualText => StateText(ActualValve1);

    public string Valve2ActualText => StateText(ActualValve2);

    public string MainValveActualText => ActualVentValve switch
    {
        true => "Fechada",
        false => "Aberta",
        null => "—",
    };

    partial void OnRequestedValve1Changed(bool value) => RefreshDerivedState();

    partial void OnRequestedValve2Changed(bool value) => RefreshDerivedState();

    partial void OnRequestedMainValveClosedChanged(bool value) => RefreshDerivedState();

    partial void OnMaxFlowTextChanged(string value) => RefreshDerivedState();

    /// <summary>The A/B/C wiring; the documented default when no settings service is wired in.</summary>
    private GasRigConfiguration Rig => _settings?.Current.GasRig.ToConfiguration() ?? GasRigConfiguration.Default;

    /// <summary>
    /// Builds a normal flow setpoint while preserving the route the flowmeter is observed
    /// on. A setpoint edit in the detail pane must not change the gas destination merely
    /// because the page that owns routing is elsewhere — but it must never reproduce a
    /// dead-ended line either: an observed <c>Closed</c>/<c>DeadEnd</c> with a setpoint above
    /// zero goes to the reactor (A).
    /// </summary>
    public OpenTECCommand BuildSetpointPreservingRoute(double setpoint)
    {
        var v1 = ActualValve1 ?? _appliedValve1;
        var v2 = ActualValve2 ?? _appliedValve2;
        var route = GasRouting.Interpret(v1, v2, setpoint, Rig) switch
        {
            ObservedGasRoute.VentAndNitrogen => GasRoute.VentAndNitrogen,
            ObservedGasRoute.Reactor => GasRoute.Reactor,
            ObservedGasRoute.Closed => GasRoute.Closed,
            // DeadEnd, BothOpen: not a route anyone asked for — go to the reactor.
            _ => setpoint > 0.0 ? GasRoute.Reactor : GasRoute.Closed,
        };

        return CommandBuilders.FlowRoute(setpoint, CommandMaximum, route, Rig);
    }

    /// <summary>Builds the complete flow safe-stop using the staged valid ceiling.</summary>
    public OpenTECCommand BuildSafeStop() => CommandBuilders.FlowSafeStop(CommandMaximum);

    /// <summary>
    /// Builds the staged valve/flow state. Returns false for invalid or contradictory
    /// input rather than relying on the protocol builder's clamp.
    /// </summary>
    public bool TryBuildRequested(double setpoint, bool flowEnabled, out OpenTECCommand command)
    {
        if (!CanSendFlowCommands ||
            !TryGetStagedMaxFlow(out var maximum) ||
            (flowEnabled && setpoint > maximum) ||
            (flowEnabled && setpoint > 0.0 && setpoint < 0.10))
        {
            command = OpenTECCommand.Create();
            return false;
        }

        command = flowEnabled
            ? CommandBuilders.FlowSetpoint(
                setpoint, maximum, RequestedValve1, RequestedValve2, RequestedMainValveClosed)
            : CommandBuilders.FlowSafeStop(maximum);
        return true;
    }

    /// <summary>
    /// Commits the ceiling carried by a flow-row command. When the row is disabled the
    /// same command is also a safe-stop, so both requested valves become closed.
    /// </summary>
    public void CommitFromFlowSetpoint(bool flowEnabled)
    {
        CommitMaximum();
        if (!flowEnabled)
        {
            CommitClosedValves();
        }

        RefreshDerivedState();
    }

    /// <summary>Commits the full state after Controle sends it.</summary>
    public void CommitRequested(bool flowEnabled)
    {
        CommitMaximum();

        _suppressRefresh = true;
        if (flowEnabled)
        {
            _appliedValve1 = RequestedValve1;
            _appliedValve2 = RequestedValve2;
            _appliedMainValveClosed = RequestedMainValveClosed;
        }
        else
        {
            _appliedValve1 = false;
            _appliedValve2 = false;
            _appliedMainValveClosed = true;
            RequestedValve1 = false;
            RequestedValve2 = false;
            RequestedMainValveClosed = true;
        }

        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>Stages a preset without sending it.</summary>
    public void Stage(double maximum, bool valve1, bool valve2)
    {
        _suppressRefresh = true;
        MaxFlowText = Format(maximum);
        RequestedValve1 = valve1;
        RequestedValve2 = valve2;
        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>Restores the last commanded state.</summary>
    public void Revert()
    {
        _suppressRefresh = true;
        MaxFlowText = Format(_appliedMaxFlow);
        RequestedValve1 = _appliedValve1;
        RequestedValve2 = _appliedValve2;
        RequestedMainValveClosed = _appliedMainValveClosed;
        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>
    /// What the operator's Vazão de Ar switch says, for the routing comparison.
    /// </summary>
    /// <remarks>
    /// The Hub persists <c>flowComm</c> in NVS and the app persists this switch on the PC. After
    /// a Hub reboot the two can differ — and <c>FlowControlEnabled</c> is what the
    /// <c>Fluxômetro offline</c> alarm is conditioned on, so a silent divergence disables that
    /// alarm as well as the loop.
    /// </remarks>
    public bool IsLoopRequested
    {
        get => Status.IsCommRequested;
        set => Status.IsCommRequested = value;
    }

    /// <summary>Locks the local controls before the asynchronous transport returns.</summary>
    public void MarkCommandDispatched()
    {
        IsAwaitingAck = true;
        Status.MarkCommandDispatched();
    }

    /// <summary>Separates loss of the app-to-Hub link from an internal flowmeter outage.</summary>
    public void MarkHubUnavailable()
    {
        HasFlowmeterTelemetry = false;
        IsFlowmeterOnline = false;
        IsFlowCommandPending = false;
        CanEditTuning = false;
        AppliedKpText = "—";
        AppliedKiText = "—";
        AppliedFfGainText = "—";
        AppliedFfOffsetText = "—";
        AppliedRampRateText = "—";
        FlowOutputText = "—";
        FlowSetpointCorrectedText = "—";
        OnPropertyChanged(nameof(TuningUnavailableText));
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
        Status.MarkHubUnavailable();
    }

    /// <summary>Updates the read-only physical valve states and tuning echoes from telemetry.</summary>
    public void UpdateTelemetry(SensorSnapshot snapshot)
    {
        HasFlowmeterTelemetry = true;
        IsFlowmeterOnline = snapshot.FlowmeterOnline;
        IsFlowCommandPending = snapshot.FlowCommandPending;
        IsAwaitingAck = snapshot.FlowCommandPending;

        // The Hub has published FlowControlEnabled since v7, so a plain bool is honest here —
        // unlike the other devices, there is no "this Hub does not say" case to represent.
        Status.Update(
            hasTelemetry: true,
            snapshot.FlowmeterOnline,
            snapshot.FlowCommandPending,
            snapshot.FlowControlEnabled,
            snapshot.FlowmeterNode);
        _observedSetpoint = double.IsFinite(snapshot.FlowSetpoint) && snapshot.FlowSetpoint > 0 ? snapshot.FlowSetpoint : 0.0;
        ActualValve1 = ToState(snapshot.FlowValve1);
        ActualValve2 = ToState(snapshot.FlowValve2);
        ActualVentValve = ToState(snapshot.FlowValveMain);
        NotifyObservedRoute();

        AppliedKpText = snapshot.FlowKp is { } kp ? FormatTuning(kp) : "—";
        AppliedKiText = snapshot.FlowKi is { } ki ? FormatTuning(ki) : "—";
        AppliedFfGainText = snapshot.FlowFfGain is { } ffg ? FormatTuning(ffg) : "—";
        AppliedFfOffsetText = snapshot.FlowFfOffset is { } ffo ? FormatTuning(ffo) : "—";
        AppliedRampRateText = snapshot.FlowRampRate is { } ramp ? FormatTuning(ramp) : "—";
        FlowOutputText = snapshot.FlowOutput is { } vo ? vo.ToString("F2", CultureInfo.CurrentCulture) + " L/min" : "—";
        FlowSetpointCorrectedText = snapshot.FlowSetpointCorrected is { } spc ? spc.ToString("0.##", CultureInfo.CurrentCulture) + " L/min" : "—";

        // F16: Cross-check plausibility diagnostic
        if (snapshot.FlowmeterOnline)
        {
            bool cutActive = snapshot.FlowValveMain == 1;
            bool routeOpen = snapshot.FlowValve1 == 1 || snapshot.FlowValve2 == 1;
            double actualRate = snapshot.FlowRate;
            double target = snapshot.FlowSetpoint;

            if (!cutActive && routeOpen && target >= 2.0 && actualRate < 0.2)
            {
                FlowPlausibilityWarning = "Comando de vazão ativo, mas vazão nula detectada. Verifique alimentação elétrica das solenoides.";
            }
            else if ((cutActive || !routeOpen) && actualRate > 0.5)
            {
                FlowPlausibilityWarning = "Vazão detectada com válvulas comandadas fechadas. Verifique vedação mecânica das válvulas.";
            }
            else
            {
                FlowPlausibilityWarning = null;
            }
        }
        else
        {
            FlowPlausibilityWarning = null;
        }

        CanEditTuning = snapshot.FlowKp is not null;
        OnPropertyChanged(nameof(TuningUnavailableText));
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();

        // On first contact, start from what the equipment is actually doing. Do not
        // overwrite an edit the operator made while waiting for the first frame.
        if (!_telemetryInitialised && !HasPendingChange)
        {
            _suppressRefresh = true;
            if (ActualValve1 is { } valve1)
            {
                RequestedValve1 = valve1;
                _appliedValve1 = valve1;
            }

            if (ActualValve2 is { } valve2)
            {
                RequestedValve2 = valve2;
                _appliedValve2 = valve2;
            }

            if (ActualVentValve is { } mainClosed)
            {
                RequestedMainValveClosed = mainClosed;
                _appliedMainValveClosed = mainClosed;
            }

            _suppressRefresh = false;
            RefreshDerivedState();
        }

        _telemetryInitialised = true;
    }

    [RelayCommand(CanExecute = nameof(CanSendTuning))]
    private void SendTuning()
    {
        if (_dispatcher is null)
        {
            TuningStatusText = "Despachante não disponível.";
            return;
        }

        if (!TryGetStagedTuning(out var kp, out var ki, out var ffGain, out var ffOffset, out var rampRate))
        {
            TuningStatusText = TuningValidationError ?? "Revise os parâmetros de sintonia.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.FlowTuning(kp, ki, ffGain, ffOffset, rampRate));
        if (!result.Accepted)
        {
            TuningStatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }

        MarkCommandDispatched();
        _settings?.Update(settings => settings with
        {
            FlowControl = new FlowControlSettings
            {
                Kp = kp,
                Ki = ki,
                FfGain = ffGain,
                FfOffset = ffOffset,
                RampRate = rampRate,
            }
        });
        TuningStatusText = "Sintonia do fluxômetro enviada; aguardando confirmação.";
    }

    [RelayCommand]
    private void EnableWifiReconnect()
    {
        if (_dispatcher is null)
        {
            return;
        }

        _dispatcher.Dispatch(CommandBuilders.FlowmeterReconnectWifi(true));
    }

    public bool TryGetStagedTuning(out double kp, out double ki, out double ffGain, out double ffOffset, out double rampRate)
    {
        kp = 0.8;
        ki = 0.15;
        ffGain = 0.106;
        ffOffset = 0.01033;
        rampRate = 2.0;

        if (!TryParse(KpText, out kp) || kp <= 0 || kp > 100.0)
        {
            return false;
        }

        if (!TryParse(KiText, out ki) || ki < 0 || ki > 100.0)
        {
            return false;
        }

        if (!TryParse(FfGainText, out ffGain) || ffGain < 0 || ffGain > 10.0)
        {
            return false;
        }

        if (!TryParse(FfOffsetText, out ffOffset) || ffOffset < 0 || ffOffset > 5.0)
        {
            return false;
        }

        if (!TryParse(RampRateText, out rampRate) || rampRate <= 0 || rampRate > 100.0)
        {
            return false;
        }

        return true;
    }

    private void RefreshTuningState()
    {
        if (!_initialised)
        {
            return;
        }

        TuningValidationError = ValidateTuning();
        OnPropertyChanged(nameof(IsTuningValid));
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    private string? ValidateTuning()
    {
        if (!TryParse(KpText, out var kp) || kp <= 0 || kp > 100.0)
        {
            return "Kp: número maior que 0 e até 100.";
        }

        if (!TryParse(KiText, out var ki) || ki < 0 || ki > 100.0)
        {
            return "Ki: número de 0 a 100.";
        }

        if (!TryParse(FfGainText, out var ffGain) || ffGain < 0 || ffGain > 10.0)
        {
            return "Ganho FF: número de 0 a 10.";
        }

        if (!TryParse(FfOffsetText, out var ffOffset) || ffOffset < 0 || ffOffset > 5.0)
        {
            return "Offset FF: número de 0 a 5.";
        }

        if (!TryParse(RampRateText, out var rampRate) || rampRate <= 0 || rampRate > 100.0)
        {
            return "Taxa de rampa: número maior que 0 e até 100 L/min/s.";
        }

        return null;
    }

    private void LoadTuning(FlowControlSettings s)
    {
        KpText = FormatTuning(s.Kp);
        KiText = FormatTuning(s.Ki);
        FfGainText = FormatTuning(s.FfGain);
        FfOffsetText = FormatTuning(s.FfOffset);
        RampRateText = FormatTuning(s.RampRate);
    }

    private static string FormatTuning(double value)
        => value.ToString("0.#####", CultureInfo.CurrentCulture);

    public bool TryGetStagedMaxFlow(out double maximum)
        => TryParse(MaxFlowText, out maximum) && double.IsFinite(maximum) && maximum > 0;

    private double CommandMaximum
        => TryGetStagedMaxFlow(out var maximum) ? maximum : _appliedMaxFlow;

    private void CommitMaximum()
    {
        if (TryGetStagedMaxFlow(out var maximum))
        {
            _appliedMaxFlow = maximum;
            OnPropertyChanged(nameof(AppliedMaxFlow));
        }
    }

    private void CommitClosedValves()
    {
        _suppressRefresh = true;
        _appliedValve1 = false;
        _appliedValve2 = false;
        _appliedMainValveClosed = true;
        RequestedValve1 = false;
        RequestedValve2 = false;
        RequestedMainValveClosed = true;
        _suppressRefresh = false;
    }

    private void RefreshDerivedState()
    {
        if (!_initialised || _suppressRefresh)
        {
            return;
        }

        Validate();
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(CanSendTuning));
        SendTuningCommand.NotifyCanExecuteChanged();
    }

    private void Validate()
    {
        var text = MaxFlowText?.Trim() ?? "";
        if (text.Length == 0)
        {
            ValidationError = "Informe o limite de vazão.";
        }
        else if (!TryParse(text, out var maximum) || !double.IsFinite(maximum))
        {
            ValidationError = "Limite de vazão inválido.";
        }
        else if (maximum <= 0)
        {
            ValidationError = "O limite de vazão deve ser maior que zero.";
        }
        else
        {
            ValidationError = null;
        }
    }

    private static bool TryParse(string? text, out double value)
        => double.TryParse(
            (text ?? "").Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static string Format(double value)
        => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool? ToState(int value) => value < 0 ? null : value != 0;

    private static string StateText(bool? state) => state switch
    {
        true => "Aberta",
        false => "Fechada",
        null => "—",
    };
}
