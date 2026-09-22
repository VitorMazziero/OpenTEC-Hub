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
/// display SP/PV.  The Hub owns the outer PI; the app only sends a reactor reference,
/// route/mode commands and explicit cascade tuning.
/// </remarks>
public sealed partial class ExternalBathViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private bool _initialised;
    private bool _syncingTelemetry;
    private readonly PropertyChangedEventHandler _statusChangedHandler;

    public ExternalBathViewModel(
        IDeviceService device,
        ISettingsService? settings = null,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
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
        _initialised = true;
        RefreshCommands();
    }

    public ExternalDeviceStatus Status { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    public partial bool IsTempControlViaBath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    public partial bool IsCommEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAutomatic { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplySetpoint))]
    public partial string ReactorSetpointText { get; set; } = "30.0";

    [ObservableProperty]
    public partial string BathSyncText { get; set; } = "30.0";

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
    [ObservableProperty] public partial string StatusText { get; set; } = "Aguardando telemetria do Hub.";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeKpText { get; set; } = "0.5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeTiText { get; set; } = "600";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeBiasText { get; set; } = "0.6";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadePeriodText { get; set; } = "10000";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeFilterText { get; set; } = "20";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeMinCommandText { get; set; } = "30000";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeBandText { get; set; } = "0.1";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeSlewText { get; set; } = "0.5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeOffsetHighText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeOffsetLowText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeOutputMinText { get; set; } = "5";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApplyTuning))]
    public partial string CascadeOutputMaxText { get; set; } = "90";

    public bool CanApplyNow => IsTempControlViaBath && IsCommEnabled && Status.HasTelemetry &&
                                Status.IsOnline && Status.CommEnabledOnHub == true &&
                                !Status.IsAwaitingAck && !_syncingTelemetry;
    public bool CanApplySetpoint => CanApplyNow && TryParse(ReactorSetpointText, out var value) && value is >= 0 and <= 100;
    public bool CanApplyTuning => CanApplyNow && TryParseTuning(out _);

    partial void OnIsTempControlViaBathChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        var result = _dispatcher.DispatchSeparateFrame(CommandBuilders.TemperatureRoute(value));
        if (!result.Accepted)
        {
            _syncingTelemetry = true;
            IsTempControlViaBath = !value;
            _syncingTelemetry = false;
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }
        Status.IsCommRequested = value;
        Status.MarkCommandDispatched();
        StatusText = value ? "Via externa solicitada; aguardando confirmação do Hub." : "Via do módulo UART solicitada.";
        RefreshCommands();
    }

    partial void OnIsCommEnabledChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        var result = _dispatcher.DispatchSeparateFrame(CommandBuilders.BathCommunication(value));
        if (!result.Accepted)
        {
            _syncingTelemetry = true;
            IsCommEnabled = !value;
            _syncingTelemetry = false;
            StatusText = DispatchRefusal.Describe(result, _dispatcher);
            return;
        }
        Status.IsCommRequested = value;
        Status.MarkCommandDispatched();
        StatusText = value ? "Comunicação do banho habilitada." : "Comunicação do banho desabilitada.";
        RefreshCommands();
    }

    partial void OnIsAutomaticChanged(bool value)
    {
        if (!_initialised || _syncingTelemetry) return;
        var result = _dispatcher.Dispatch(CommandBuilders.BathMode(value));
        if (!result.Accepted) StatusText = DispatchRefusal.Describe(result, _dispatcher);
        else StatusText = value ? "Cascata automática solicitada." : "Modo manual do banho solicitado.";
    }

    [RelayCommand(CanExecute = nameof(CanApplySetpoint))]
    private void ApplySetpoint()
    {
        if (!TryParse(ReactorSetpointText, out var value)) return;
        var result = _dispatcher.DispatchSeparateFrame(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, value));
        if (!result.Accepted) { StatusText = DispatchRefusal.Describe(result, _dispatcher); return; }
        Status.MarkCommandDispatched();
        StatusText = $"Referência do reator {value.ToString("F1", CultureInfo.CurrentCulture)} °C enviada à cascata.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private void Synchronize()
    {
        if (!TryParse(BathSyncText, out var value) || value is < 0 or > 100)
        {
            StatusText = "Sincronização deve estar entre 0 e 100 °C.";
            return;
        }
        Dispatch(CommandBuilders.BathSynchronize(value), "Setpoint do C404 sincronizado.");
    }

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private void Abort() => Dispatch(CommandBuilders.BathAbort(), "Sequência do banho abortada.");

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private void ResetFault() => Dispatch(CommandBuilders.BathCascadeReset(), "Reset da falha da cascata enviado.");

    [RelayCommand(CanExecute = nameof(CanApplyTuning))]
    private void ApplyTuning()
    {
        if (!TryParseTuning(out var command)) { StatusText = "Revise os parâmetros da cascata."; return; }
        Dispatch(command, "Sintonia da cascata aplicada.");
    }

    private void Dispatch(OpenTECCommand command, string success)
    {
        var result = _dispatcher.DispatchSeparateFrame(command);
        if (!result.Accepted) { StatusText = DispatchRefusal.Describe(result, _dispatcher); return; }
        Status.MarkCommandDispatched();
        StatusText = success;
    }

    private bool TryParseTuning(out OpenTECCommand command)
    {
        command = OpenTECCommand.Create();
        if (!TryParse(CascadeKpText, out var kp) || !TryParse(CascadeTiText, out var ti) ||
            !TryParse(CascadeBiasText, out var bias) || !int.TryParse(CascadePeriodText, out var period) ||
            !TryParse(CascadeFilterText, out var filter) || !int.TryParse(CascadeMinCommandText, out var min) ||
            !TryParse(CascadeBandText, out var band) || !TryParse(CascadeSlewText, out var slew) ||
            !TryParse(CascadeOffsetHighText, out var high) || !TryParse(CascadeOffsetLowText, out var low) ||
            !TryParse(CascadeOutputMinText, out var outputMin) || !TryParse(CascadeOutputMaxText, out var outputMax))
            return false;
        try
        {
            command = CommandBuilders.BathCascadeTuning(kp, ti, bias, period, filter, min, band, slew, high, low, outputMin, outputMax);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Status.Update(snapshot.HasBathTelemetry, snapshot.BathOnline, snapshot.BathCommandPending,
            snapshot.BathCommEnabled, snapshot.BathNode);
        _syncingTelemetry = true;
        if (snapshot.TempControlViaBath is { } via) IsTempControlViaBath = via;
        if (snapshot.BathCommEnabled is { } comm) IsCommEnabled = comm;
        if (snapshot.BathMode is { } mode) IsAutomatic = mode == 1;
        _syncingTelemetry = false;

        ReactorPvText = Format(snapshot.Temperature);
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
            ? $"P {p:F2} / I {i:F2}" : "—";
        CascadePauseText = EmptyDash(snapshot.BathCascadePausedReason);
        StatusText = snapshot.BathCascadePausedReason is { Length: > 0 } reason
            ? $"Cascata pausada: {reason}." : Status.StatusText;
        RefreshCommands();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State is ConnectionState.Disconnected or ConnectionState.Faulted)
            Status.MarkHubUnavailable();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ApplySetpointCommand.NotifyCanExecuteChanged();
        SynchronizeCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        ResetFaultCommand.NotifyCanExecuteChanged();
        ApplyTuningCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyNow));
        OnPropertyChanged(nameof(CanApplySetpoint));
        OnPropertyChanged(nameof(CanApplyTuning));
    }

    private static string Format(double? value)
        => value is { } number && double.IsFinite(number) ? number.ToString("F2", CultureInfo.CurrentCulture) : "—";

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= _statusChangedHandler;
    }
}
