using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The ASDA-B2 servo drive: measured shaft telemetry, and the three commands that do not
/// touch the motor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only towards the drive, by design.</b> There is no speed control, no drive
/// enable and no Modbus write here. Agitation stays exclusively on CN1 through
/// <c>motorSetpoint</c>; a rotation control on this card would be a second command path to
/// the same physical quantity, over a link that carries no acknowledgement.
/// </para>
/// <para>
/// <b>Presence and routing are orthogonal.</b> All four combinations are legitimate and
/// only one is a failure - the node absent while routing is on. Routing off with the node
/// present is a configuration; both off is a module that has no servo, which is the bench
/// module's permanent and correct state. <see cref="ExternalDeviceStatus"/> carries that
/// vocabulary and is not re-implemented here.
/// </para>
/// <para>
/// <b>Nothing on this link is acknowledged.</b> The node polls a consume-on-read mailbox
/// every two seconds, so confirmation is observational: the energy falling to zero, or the
/// transaction counter changing pace. A full queue of eight takes sixteen seconds to drain,
/// which is why <see cref="QueueDepth"/> and pending state are surfaced rather than hidden
/// behind a spinner.
/// </para>
/// </remarks>
public sealed partial class ServoDriveViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long the Modbus error rate looks back.
    /// </summary>
    /// <remarks>
    /// The rate, never the total. The bench saw exactly one error in 256 reads, on the first
    /// transaction after boot: a total-based alarm would latch on that forever, while the
    /// rate returns to zero and only real noise keeps it up.
    /// </remarks>
    private const int ErrorWindowFrames = 30;

    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;

    /// <summary>Counter readings inside the window, oldest first, for the rate.</summary>
    private readonly Queue<(long Ok, long Err)> _counterWindow = new();

    private bool _revertingRouting;

    /// <summary>
    /// Guards the routing setter until the constructor has finished seeding the switch.
    /// </summary>
    /// <remarks>
    /// Without it, seeding the switch to its default position dispatches a command: building
    /// a view model would put a frame on the wire before the operator has touched anything.
    /// </remarks>
    private bool _initialised;

    public ServoDriveViewModel(
        IDeviceService device,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
        _dispatcher = dispatcher ?? new ManualDispatcher(device);

        Status = new ExternalDeviceStatus("Servo drive", "do servo drive", timeProvider);

        // Routing is born true on the Hub - the node has to come up on its own when
        // energised, without waiting for the PC - so the switch starts to match. Seeded,
        // not commanded: the Hub's own echo corrects this on the first frame if the two
        // ever disagree, and that disagreement is what the mismatch chip is for.
        Status.IsCommRequested = true;
        IsRoutingEnabled = true;

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _initialised = true;
    }

    /// <summary>Presence, routing and pending state of the node behind the Hub.</summary>
    public ExternalDeviceStatus Status { get; }

    /// <summary>Servo routing on the Hub — <c>servoComm</c>. Immediate, like the pump's.</summary>
    [ObservableProperty]
    public partial bool IsRoutingEnabled { get; set; }

    // ── Readings ─────────────────────────────────────────────────────────────
    //
    // Every one of these is an em dash when the frame carried no sample, never a zero.
    // Zero rpm, zero torque and zero power are all real readings from a stopped motor, and
    // showing one for missing data would claim a measurement that was never taken.

    [ObservableProperty]
    public partial string RpmText { get; set; } = "—";

    [ObservableProperty]
    public partial string TorquePercentText { get; set; } = "—";

    [ObservableProperty]
    public partial string TorqueNewtonMetreText { get; set; } = "—";

    [ObservableProperty]
    public partial string LoadPercentText { get; set; } = "—";

    /// <summary>
    /// Estimated mechanical shaft power, labelled as such wherever it appears.
    /// </summary>
    /// <remarks>
    /// It is <c>T·ω</c> at the shaft, derived from a torque percentage and the motor's
    /// nameplate rating. It is not electrical draw: it excludes drive losses, motor losses
    /// and the electronics. An operator reading it as consumption is out by orders of
    /// magnitude, so the unit string carries the qualifier rather than a tooltip.
    /// </remarks>
    [ObservableProperty]
    public partial string PowerWattText { get; set; } = "—";

    [ObservableProperty]
    public partial string EnergyWattHourText { get; set; } = "—";

    /// <summary>OFF / READY / SON / ALARM, in the operator's words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInAlarm))]
    public partial string StateText { get; set; } = "—";

    /// <summary>
    /// The drive's alarm code in panel form: <c>AL011</c> for <c>0x0011</c>.
    /// </summary>
    /// <remarks>
    /// The hexadecimal digits mirror the number on the drive's own display. Rendering the
    /// code as decimal gives 17, which matches nothing in the manual and sends whoever is
    /// looking it up to the wrong page. No cause table here: the code and the manual.
    /// </remarks>
    [ObservableProperty]
    public partial string AlarmText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool IsInAlarm { get; set; }

    /// <summary>Modbus errors as a share of transactions inside the window.</summary>
    [ObservableProperty]
    public partial string ErrorRateText { get; set; } = "—";

    /// <summary>Raw counters, for the detail pane only.</summary>
    [ObservableProperty]
    public partial string CounterText { get; set; } = "—";

    /// <summary>Commands queued on the Hub and not yet collected, 0-8.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueDepthText))]
    public partial int QueueDepth { get; set; } = -1;

    public string QueueDepthText => QueueDepth < 0
        ? "—"
        : QueueDepth.ToString(CultureInfo.CurrentCulture) + "/8";

    /// <summary>Sampling interval to send, in milliseconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPollIntervalValid))]
    [NotifyPropertyChangedFor(nameof(PollIntervalError))]
    public partial string PollIntervalText { get; set; } = "1000";

    public bool IsPollIntervalValid => TryParsePollInterval(out _);

    /// <summary>Why the entry is refused, or null while it is good.</summary>
    /// <remarks>
    /// Refused rather than clamped. The Hub refuses out-of-range values too, but it says so
    /// on its own serial port, which nobody is watching - clamping here would leave the
    /// operator with an interval they never chose and no sign that it happened.
    /// </remarks>
    public string? PollIntervalError => TryParsePollInterval(out _)
        ? null
        : $"Informe um número inteiro entre {CommandBuilders.ServoPollMinimumMs} e " +
          $"{CommandBuilders.ServoPollMaximumMs} ms.";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    // ── Commands ─────────────────────────────────────────────────────────────

    public bool CanResetEnergy => Status.CanSend && Status.IsOnline;

    /// <summary>
    /// Zeroes the node's energy accumulator.
    /// </summary>
    /// <remarks>
    /// A data command with no effect on the process, which is why it must never sit next to
    /// a stop control. Confirmation is the total falling to roughly zero a frame or two
    /// later; with a full queue that can take up to sixteen seconds.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanResetEnergy))]
    private void ResetEnergy()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.ResetServoEnergy());
        if (!result.Accepted)
        {
            StatusMessage = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        StatusMessage = "Zeragem da energia enviada. A confirmação é a queda do acumulado.";
    }

    public bool CanApplyPollInterval => Status.CanSend && IsPollIntervalValid;

    [RelayCommand(CanExecute = nameof(CanApplyPollInterval))]
    private void ApplyPollInterval()
    {
        if (!TryParsePollInterval(out var pollMs))
        {
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.ServoPollInterval(pollMs));
        if (!result.Accepted)
        {
            StatusMessage = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        StatusMessage = $"Intervalo de amostragem de {pollMs} ms enviado.";
    }

    partial void OnIsRoutingEnabledChanged(bool value)
    {
        if (!_initialised || _revertingRouting)
        {
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.ServoRouting(value));
        if (!result.Accepted)
        {
            _revertingRouting = true;
            IsRoutingEnabled = !value;
            _revertingRouting = false;
            StatusMessage = DispatchRefusal.Describe(result);
            return;
        }

        Status.IsCommRequested = value;
        Status.MarkCommandDispatched();

        // Deliberately not "servo desligado": the node keeps running and keeps pushing.
        // What stops is the Hub forwarding its values into the aggregate frame.
        StatusMessage = value
            ? "Roteamento do servo drive ligado."
            : "Roteamento desligado. O nó continua presente; os valores deixam de ser publicados.";
    }

    partial void OnPollIntervalTextChanged(string value)
        => ApplyPollIntervalCommand.NotifyCanExecuteChanged();

    // ── Telemetry ────────────────────────────────────────────────────────────

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Status.Update(
            snapshot.HasServoTelemetry,
            snapshot.ServoOnline,
            snapshot.ServoCommandPending,
            snapshot.ServoCommEnabled);

        QueueDepth = snapshot.ServoCommandQueueDepth;

        ResetEnergyCommand.NotifyCanExecuteChanged();
        ApplyPollIntervalCommand.NotifyCanExecuteChanged();

        if (!snapshot.HasServoSample)
        {
            ClearReadings();
            return;
        }

        RpmText = Format(snapshot.ServoRpm, 1);
        TorquePercentText = Format(snapshot.ServoTorquePct, 1);
        TorqueNewtonMetreText = Format(snapshot.ServoTorqueNm, 4);
        LoadPercentText = Format(snapshot.ServoLoadPct, 0);
        PowerWattText = Format(snapshot.ServoPowerW, 2);
        EnergyWattHourText = Format(snapshot.ServoEnergyWh, 4);

        StateText = snapshot.ServoState switch
        {
            0 => "Desligado",
            1 => "Pronto",
            2 => "Energizado",
            3 => "Alarme",
            _ => "—",
        };

        IsInAlarm = snapshot.ServoState == 3 || snapshot.ServoAlarm > 0;

        // Panel form: the hex digits are the number on the drive's display.
        AlarmText = snapshot.ServoAlarm switch
        {
            < 0 => "—",
            0 => "Nenhum",
            var code => "AL" + code.ToString("X3", CultureInfo.InvariantCulture),
        };

        UpdateCounters(snapshot.ServoCommOk, snapshot.ServoCommErr);
    }

    /// <summary>
    /// Turns the raw counters into a rate over a moving window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The totals grow for as long as the node stays up, so a threshold on them fires once
    /// and never clears. What distinguishes real noise from a boot hiccup is whether errors
    /// are <i>still</i> arriving, which is a rate.
    /// </para>
    /// <para>
    /// A counter that goes backwards means the node restarted and its counters began again.
    /// The window is dropped rather than producing a negative delta, which would otherwise
    /// read as a spectacularly healthy link.
    /// </para>
    /// </remarks>
    private void UpdateCounters(long ok, long err)
    {
        CounterText = ok < 0 || err < 0
            ? "—"
            : string.Format(CultureInfo.CurrentCulture, "{0} ok · {1} err", ok, err);

        if (ok < 0 || err < 0)
        {
            _counterWindow.Clear();
            ErrorRateText = "—";
            return;
        }

        if (_counterWindow.Count > 0)
        {
            var (oldestOk, oldestErr) = _counterWindow.Peek();
            if (ok < oldestOk || err < oldestErr)
            {
                _counterWindow.Clear();
            }
        }

        _counterWindow.Enqueue((ok, err));
        while (_counterWindow.Count > ErrorWindowFrames)
        {
            _counterWindow.Dequeue();
        }

        if (_counterWindow.Count < 2)
        {
            ErrorRateText = "—";
            return;
        }

        var (firstOk, firstErr) = _counterWindow.Peek();
        var deltaOk = ok - firstOk;
        var deltaErr = err - firstErr;
        var attempts = deltaOk + deltaErr;

        ErrorRateText = attempts <= 0
            ? "0,0 %"
            : (100.0 * deltaErr / attempts).ToString("F1", CultureInfo.CurrentCulture) + " %";
    }

    private void ClearReadings()
    {
        RpmText = "—";
        TorquePercentText = "—";
        TorqueNewtonMetreText = "—";
        LoadPercentText = "—";
        PowerWattText = "—";
        EnergyWattHourText = "—";
        StateText = "—";
        AlarmText = "—";
        IsInAlarm = false;
        ErrorRateText = "—";
        CounterText = "—";
        _counterWindow.Clear();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
            ClearReadings();
            QueueDepth = -1;
            ResetEnergyCommand.NotifyCanExecuteChanged();
            ApplyPollIntervalCommand.NotifyCanExecuteChanged();
        }
    }

    private static string Format(double value, int decimals)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    private bool TryParsePollInterval(out int pollMs)
        => int.TryParse(PollIntervalText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out pollMs) &&
           pollMs >= CommandBuilders.ServoPollMinimumMs &&
           pollMs <= CommandBuilders.ServoPollMaximumMs;

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
    }
}
