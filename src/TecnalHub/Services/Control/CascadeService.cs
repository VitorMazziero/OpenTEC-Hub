using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Control;

/// <summary>
/// The application-side runtime for the oxygen cascade.
/// </summary>
/// <remarks>
/// <para>
/// Owns a <see cref="CascadeController"/>, drives it from live dissolved-oxygen telemetry,
/// and exposes the result for the tuning workspace to display. It is the bridge between the
/// pure control math and the app.
/// </para>
/// <para>
/// <b>It never sends.</b> This WP runs the cascade in an advisory role — it computes what it
/// <i>would</i> command so the operator can watch it track and tune it against real
/// telemetry, with no actuation risk. Live actuation waits for command ownership
/// (<c>Automático</c> mode) and the bioreactor, a later Phase 2 WP. That is why nothing here
/// touches <see cref="IDeviceService.Send"/>.
/// </para>
/// </remarks>
public interface ICascadeService
{
    /// <summary>True while the advisory loop is computing on each telemetry frame.</summary>
    bool IsArmed { get; }

    /// <summary>The persisted configuration currently loaded into the controller.</summary>
    CascadeSettings Configuration { get; }

    /// <summary>The active tuning, as the controller sees it.</summary>
    CascadeTuning Tuning { get; }

    /// <summary>The most recent decomposed evaluation, or <see cref="CascadeTerms.Empty"/>.</summary>
    CascadeTerms Terms { get; }

    /// <summary>The most recent advisory actuation, or null when not armed.</summary>
    CascadeActuationResult? LastActuation { get; }

    /// <summary>The dissolved-oxygen target, in percent.</summary>
    double OxygenSetpoint { get; }

    /// <summary>The configured actuator windows, for the allocation bar.</summary>
    IReadOnlyList<ActuatorWindow> Windows { get; }

    /// <summary>The latest valid dissolved-oxygen reading, or null before the first frame.</summary>
    double? LatestOxygen { get; }

    /// <summary>Raised on the UI thread after each processed frame or state change.</summary>
    event Action? Updated;

    /// <summary>Begins advisory computation, resetting loop state.</summary>
    void Arm();

    /// <summary>Stops advisory computation and clears the live terms.</summary>
    void Disarm();

    /// <summary>Loads a new configuration, preserving probe history for a gains-only change.</summary>
    void Configure(CascadeSettings settings);
}

/// <inheritdoc cref="ICascadeService"/>
public sealed class CascadeService : ICascadeService, IDisposable
{
    private readonly IDeviceService _device;
    private readonly TimeProvider _time;
    private readonly double _nominalStepSeconds;

    private CascadeController _controller;
    private CascadeSettings _configuration;
    private DateTimeOffset? _lastStepAt;

    public CascadeService(IDeviceService device, ISettingsService settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);

        _device = device;
        _time = time;
        _configuration = settings.Current.Cascade;
        // A first armed frame has no previous step to measure against; fall back to the
        // configured emission period rather than integrating against a zero interval.
        _nominalStepSeconds = Math.Max(settings.Current.Connection.DataDelayMs, 250) / 1000.0;
        _controller = Build(_configuration);

        _device.TelemetryReceived += OnTelemetry;
    }

    public bool IsArmed { get; private set; }

    public CascadeSettings Configuration => _configuration;

    public CascadeTuning Tuning => _controller.Tuning;

    public CascadeTerms Terms { get; private set; } = CascadeTerms.Empty;

    public CascadeActuationResult? LastActuation { get; private set; }

    public double OxygenSetpoint => _controller.OxygenSetpoint;

    public IReadOnlyList<ActuatorWindow> Windows => _controller.Windows;

    public double? LatestOxygen { get; private set; }

    public event Action? Updated;

    public void Arm()
    {
        if (IsArmed)
        {
            return;
        }

        _controller.Reset();
        _controller.OxygenSetpoint = _configuration.OxygenSetpointPercent;
        _lastStepAt = null;
        Terms = CascadeTerms.Empty;
        LastActuation = null;
        IsArmed = true;
        Updated?.Invoke();
    }

    public void Disarm()
    {
        if (!IsArmed)
        {
            return;
        }

        IsArmed = false;
        Terms = CascadeTerms.Empty;
        LastActuation = null;
        Updated?.Invoke();
    }

    public void Configure(CascadeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var windowsChanged = !SameWindows(_configuration, settings);
        _configuration = settings;

        if (windowsChanged)
        {
            // A new actuator split needs a new allocator; rebuild and re-arm cleanly.
            _controller = Build(settings);
            if (IsArmed)
            {
                _lastStepAt = null;
            }
        }
        else
        {
            // Gains, limits, horizon or setpoint only: keep the probe history the
            // prediction depends on.
            _controller.Retune(ToTuning(settings));
            _controller.OxygenSetpoint = settings.OxygenSetpointPercent;
        }

        Updated?.Invoke();
    }

    /// <summary>Projects persisted settings onto the controller's tuning record.</summary>
    public static CascadeTuning ToTuning(CascadeSettings c) => new()
    {
        Kp = c.Kp,
        Ki = c.Ki,
        Kd = c.Kd,
        IntegralMin = c.IntegralMin,
        IntegralMax = c.IntegralMax,
        // The effort window is fixed 0-100 %: the allocator maps it onto real actuators.
        OutputMin = 0,
        OutputMax = 100,
        PredictionHorizonSeconds = c.PredictionHorizonSeconds,
        RateWindowSeconds = c.RateWindowSeconds,
        IntervalSeconds = c.IntervalSeconds,
    };

    private static CascadeController Build(CascadeSettings c) => new(
        ToTuning(c),
        new ActuatorWindow(
            CascadeController.AgitationActuator,
            c.AgitationMinRpm, c.AgitationMaxRpm, c.AgitationEffortStart, c.AgitationEffortEnd),
        new ActuatorWindow(
            CascadeController.AerationActuator,
            c.AerationMinLpm, c.AerationMaxLpm, c.AerationEffortStart, c.AerationEffortEnd),
        c.OxygenSetpointPercent);

    private static bool SameWindows(CascadeSettings a, CascadeSettings b) =>
        a.AgitationMinRpm == b.AgitationMinRpm &&
        a.AgitationMaxRpm == b.AgitationMaxRpm &&
        a.AgitationEffortStart == b.AgitationEffortStart &&
        a.AgitationEffortEnd == b.AgitationEffortEnd &&
        a.AerationMinLpm == b.AerationMinLpm &&
        a.AerationMaxLpm == b.AerationMaxLpm &&
        a.AerationEffortStart == b.AerationEffortStart &&
        a.AerationEffortEnd == b.AerationEffortEnd &&
        // The rate window changing rebuilds the estimator, so treat it like a window change.
        a.RateWindowSeconds == b.RateWindowSeconds;

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        LatestOxygen = snapshot.OxygenCalibrated > SensorReadings.NotReceived
            ? snapshot.OxygenCalibrated
            : null;

        if (!IsArmed || LatestOxygen is not { } oxygen)
        {
            // Still surface the reading so the workspace can show the current DO, but do
            // not step the loop with no target or while disarmed.
            Updated?.Invoke();
            return;
        }

        var now = _time.GetUtcNow();
        var dt = _lastStepAt is { } last ? (now - last).TotalSeconds : _nominalStepSeconds;
        _lastStepAt = now;
        if (dt <= 0)
        {
            dt = _nominalStepSeconds;
        }

        LastActuation = _controller.Update(oxygen, dt);
        Terms = LastActuation.Terms;
        Updated?.Invoke();
    }

    public void Dispose() => _device.TelemetryReceived -= OnTelemetry;
}
