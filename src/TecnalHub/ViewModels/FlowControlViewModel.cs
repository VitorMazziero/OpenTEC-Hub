using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TecnalHub.Protocol;

namespace TecnalHub.ViewModels;

/// <summary>
/// Staged and observed state for the Phase 1 flowmeter valves and ceiling.
/// </summary>
/// <remarks>
/// Valve commands are always emitted as a complete desired flow state. A one-key
/// valve write would recreate the overwrite-and-consume failure mode of the legacy
/// hub: a later flow setpoint could silently restore stale valve values. The vent
/// valve is never entered by the operator; <see cref="CommandBuilders.FlowSetpoint"/>
/// derives its inverted <c>v_Flow</c> value from the flow setpoint.
/// </remarks>
public sealed partial class FlowControlViewModel : ObservableObject
{
    private bool _initialised;
    private bool _suppressRefresh;
    private bool _telemetryInitialised;
    private bool _appliedValve1;
    private bool _appliedValve2;
    private double _appliedMaxFlow;

    public FlowControlViewModel(double initialMaxFlow)
    {
        if (!double.IsFinite(initialMaxFlow) || initialMaxFlow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialMaxFlow));
        }

        _appliedMaxFlow = initialMaxFlow;
        MaxFlowText = Format(initialMaxFlow);
        Validate();
        _initialised = true;
    }

    /// <summary>Auxiliary valve requested by the operator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    public partial bool RequestedValve1 { get; set; }

    /// <summary>Nitrogen valve requested by the operator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChange))]
    public partial bool RequestedValve2 { get; set; }

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
    [NotifyPropertyChangedFor(nameof(VentActualText))]
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
    [NotifyPropertyChangedFor(nameof(PendingStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    public partial bool IsAwaitingAck { get; set; }

    /// <summary>State of the Hub-to-flowmeter wireless link, distinct from the app-to-Hub link.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowmeterOffline))]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(FlowmeterStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    public partial bool IsFlowmeterOnline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowmeterOffline))]
    [NotifyPropertyChangedFor(nameof(CanSendFlowCommands))]
    [NotifyPropertyChangedFor(nameof(FlowmeterStatusText))]
    [NotifyPropertyChangedFor(nameof(FlowStatusText))]
    [NotifyPropertyChangedFor(nameof(HasFlowStatusAlert))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    public partial bool HasFlowmeterTelemetry { get; set; }

    public double AppliedMaxFlow => _appliedMaxFlow;

    /// <summary>Valid staged ceiling, or the last applied value while the field is invalid.</summary>
    public double MaximumForCommand => CommandMaximum;

    public bool IsValid => ValidationError is null;

    public bool IsFlowmeterOffline => HasFlowmeterTelemetry && !IsFlowmeterOnline;

    public bool CanSendFlowCommands => HasFlowmeterTelemetry && IsFlowmeterOnline && !IsAwaitingAck;

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
           RequestedValve2 != _appliedValve2;

    public string Valve1ActualText => StateText(ActualValve1);

    public string Valve2ActualText => StateText(ActualValve2);

    public string VentActualText => ActualVentValve switch
    {
        true => "Aberta · v_Flow = 1",
        false => "Fechada · v_Flow = 0",
        null => "— · sem telemetria",
    };

    /// <summary>
    /// Explains the inverted wire flag next to the read-only vent state.
    /// </summary>
    public string VentRuleText => "Derivada: v_Flow = 1 quando SP = 0; v_Flow = 0 quando SP > 0.";

    partial void OnRequestedValve1Changed(bool value) => RefreshDerivedState();

    partial void OnRequestedValve2Changed(bool value) => RefreshDerivedState();

    partial void OnMaxFlowTextChanged(string value) => RefreshDerivedState();

    /// <summary>
    /// Builds a normal flow setpoint while preserving the most recently observed
    /// physical valve states. A setpoint edit in the detail pane must not close a valve
    /// merely because the page that owns valve editing is elsewhere.
    /// </summary>
    public TecnalCommand BuildSetpointUsingObservedValves(double setpoint)
        => CommandBuilders.FlowSetpoint(
            setpoint,
            CommandMaximum,
            ActualValve1 ?? _appliedValve1,
            ActualValve2 ?? _appliedValve2);

    /// <summary>Builds the complete flow safe-stop using the staged valid ceiling.</summary>
    public TecnalCommand BuildSafeStop() => CommandBuilders.FlowSafeStop(CommandMaximum);

    /// <summary>
    /// Builds the staged valve/flow state. Returns false for invalid or contradictory
    /// input rather than relying on the protocol builder's clamp.
    /// </summary>
    public bool TryBuildRequested(double setpoint, bool flowEnabled, out TecnalCommand command)
    {
        if (!CanSendFlowCommands ||
            !TryGetStagedMaxFlow(out var maximum) ||
            (flowEnabled && setpoint > maximum))
        {
            command = TecnalCommand.Create();
            return false;
        }

        command = flowEnabled
            ? CommandBuilders.FlowSetpoint(setpoint, maximum, RequestedValve1, RequestedValve2)
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
        }
        else
        {
            _appliedValve1 = false;
            _appliedValve2 = false;
            RequestedValve1 = false;
            RequestedValve2 = false;
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
        _suppressRefresh = false;
        RefreshDerivedState();
    }

    /// <summary>Locks the local controls before the asynchronous transport returns.</summary>
    public void MarkCommandDispatched() => IsAwaitingAck = true;

    /// <summary>Separates loss of the app-to-Hub link from an internal flowmeter outage.</summary>
    public void MarkHubUnavailable()
    {
        HasFlowmeterTelemetry = false;
        IsFlowmeterOnline = false;
        IsFlowCommandPending = false;
    }

    /// <summary>Updates the read-only physical valve states from telemetry.</summary>
    public void UpdateTelemetry(SensorSnapshot snapshot)
    {
        HasFlowmeterTelemetry = true;
        IsFlowmeterOnline = snapshot.FlowmeterOnline;
        IsFlowCommandPending = snapshot.FlowCommandPending;
        IsAwaitingAck = snapshot.FlowCommandPending;
        ActualValve1 = ToState(snapshot.FlowValve1);
        ActualValve2 = ToState(snapshot.FlowValve2);
        ActualVentValve = ToState(snapshot.FlowValveMain);

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

            _suppressRefresh = false;
            RefreshDerivedState();
        }

        _telemetryInitialised = true;
    }

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
        RequestedValve1 = false;
        RequestedValve2 = false;
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
