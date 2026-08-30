using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Control;

/// <summary>
/// The live conditional-OUR soft sensor: observes dissolved oxygen, airflow and the commanded
/// agitation, reads the active published kLa map, and publishes an <see cref="OurSample"/> each frame.
/// </summary>
public interface IOurSoftSensor
{
    /// <summary>The most recent reading. <see cref="OurSample.Empty"/> before the first frame.</summary>
    OurSample Latest { get; }

    /// <summary>The published kLa map currently supplying kLa, or null when none is active.</summary>
    KlaPublishedProfile? ActiveMap { get; }

    /// <summary>Raised whenever a new reading is available.</summary>
    event Action? Updated;

    /// <summary>Zeroes the accepted-interval running total, keeping the live reading.</summary>
    void ResetTotals();
}

/// <inheritdoc cref="IOurSoftSensor"/>
public sealed class OurSoftSensorService : IOurSoftSensor, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ICascadeService _cascade;
    private readonly IKlaMappingEngine _engine;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;

    private OurSoftSensor _sensor;
    private double _commandedAgitationRpm;
    private DateTimeOffset? _epoch;

    private string? _surfaceFingerprint;
    private KlaSurface? _surface;
    private KlaDomain? _domain;

    public OurSoftSensorService(
        IDeviceService device,
        ICascadeService cascade,
        IKlaMappingEngine engine,
        ISettingsService settings,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(cascade);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);

        _device = device;
        _cascade = cascade;
        _engine = engine;
        _settings = settings;
        _time = time;
        _sensor = new OurSoftSensor(ToConfig(settings.Current.Our));
        // Best-effort starting point for the commanded agitation; refined by every motor command.
        _commandedAgitationRpm = settings.Current.Setpoints.MotorRpm;

        _device.TelemetryReceived += OnTelemetry;
        _device.CommandSent += OnCommandSent;
        settings.Changed += OnSettingsChanged;
    }

    public OurSample Latest { get; private set; } = OurSample.Empty;

    public KlaPublishedProfile? ActiveMap => _cascade.ActivePath;

    public event Action? Updated;

    public void ResetTotals()
    {
        _sensor.Reset();
        Latest = OurSample.Empty;
        Updated?.Invoke();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        // Re-tuning the sensor restarts its running total: the old integral was accumulated
        // under different acceptance criteria, so carrying it forward would be dishonest.
        _sensor = new OurSoftSensor(ToConfig(settings.Our));
        Latest = OurSample.Empty;
    }

    private void OnCommandSent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(CommandKeys.MotorSetpoint, out var motor) &&
                motor.TryGetDouble(out var rpm) &&
                rpm > 0)
            {
                _commandedAgitationRpm = rpm;
            }
        }
        catch (JsonException)
        {
            // A frame we cannot parse leaves the last known agitation in place.
        }
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        if (snapshot.OxygenCalibrated <= SensorReadings.NotReceived)
        {
            // No usable oxygen: hold the last reading rather than inventing one.
            return;
        }

        var now = _time.GetUtcNow();
        _epoch ??= now;
        var timeSeconds = (now - _epoch.Value).TotalSeconds;

        var kla = ResolveKla(snapshot.FlowRate);
        Latest = _sensor.Update(timeSeconds, snapshot.OxygenCalibrated, _cascade.OxygenSetpoint, kla);
        Updated?.Invoke();
    }

    /// <summary>
    /// Evaluates the active published surface at the live operating point. Null when no map is
    /// active, airflow is missing, or the operating point is outside the mapped domain — an
    /// off-map kLa would be an extrapolation the manuscript explicitly refuses.
    /// </summary>
    private double? ResolveKla(double airflowLpm)
    {
        var profile = _cascade.ActivePath;
        if (profile is null || airflowLpm <= SensorReadings.NotReceived)
        {
            _surfaceFingerprint = null;
            _surface = null;
            _domain = null;
            return null;
        }

        if (!ReferenceEquals(_surface, null) &&
            string.Equals(_surfaceFingerprint, profile.Payload.SurfaceFingerprint, StringComparison.Ordinal))
        {
            return EvaluateSurface(airflowLpm);
        }

        var payload = profile.Payload;
        var snapshot = new KlaExperimentSnapshot
        {
            Name = payload.Name,
            Domain = payload.Domain,
            Anchors = payload.Anchors,
            Algorithm = payload.Algorithm,
        };

        try
        {
            _surface = _engine.Reconstruct(snapshot);
            _domain = payload.Domain;
            _surfaceFingerprint = payload.SurfaceFingerprint;
        }
        catch (Exception)
        {
            // A map that cannot be reconstructed simply yields no kLa; the sensor reports NoKla.
            _surface = null;
            _domain = null;
            _surfaceFingerprint = null;
            return null;
        }

        return EvaluateSurface(airflowLpm);
    }

    private double? EvaluateSurface(double airflowLpm)
    {
        if (_surface is not { } surface || _domain is not { } domain)
        {
            return null;
        }

        var q = airflowLpm;
        var n = _commandedAgitationRpm;
        if (q < domain.AirflowMinimumLpm || q > domain.AirflowMaximumLpm ||
            n < domain.AgitationMinimumRpm || n > domain.AgitationMaximumRpm)
        {
            return null;
        }

        var value = surface.EvaluateNormalized(domain.NormalizeAirflow(q), domain.NormalizeAgitation(n)).Value;
        return value > 0 ? value : null;
    }

    /// <summary>Projects the persisted settings onto the pure sensor's configuration.</summary>
    public static OurSensorConfig ToConfig(OurSettings settings) => new()
    {
        OxygenSaturationMmolPerL = settings.OxygenSaturationMmolPerL,
        SetpointTolerancePercentPoints = settings.SetpointTolerancePercentPoints,
        RateLimitPointsPerHour = settings.RateLimitPointsPerHour,
        GateTolerancePercentPoints = settings.GateTolerancePercentPoints,
        RateWindowSeconds = settings.RateWindowSeconds,
    };

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetry;
        _device.CommandSent -= OnCommandSent;
        _settings.Changed -= OnSettingsChanged;
    }
}
