using System.IO;
using TecnalHub.Protocol;

namespace TecnalHub.Services.Persistence;

/// <summary>Which colour theme the shell uses.</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows app theme.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Everything the application persists between runs.
/// </summary>
/// <remarks>
/// <para>
/// One typed record, serialised with <c>System.Text.Json</c>. This replaces v.6's
/// <c>collect_preferences</c> / <c>apply_preferences</c> pair - 370 lines of
/// hand-written field marshalling in two functions that had to be kept in sync by
/// hand, and the single largest source of "add a field, forget a line, silently lose
/// the setting". See <c>docs/MIGRATION.md</c>.
/// </para>
/// <para>
/// Adding a setting here is the whole job: no read site, no write site.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Schema version, so a future format change can migrate rather than reset.</summary>
    public int Version { get; init; } = 1;

    public ConnectionSettings Connection { get; init; } = new();

    public CalibrationSettings Calibration { get; init; } = new();

    public FilterSettings Filters { get; init; } = new();

    public SetpointSettings Setpoints { get; init; } = new();

    public LoggingSettings Logging { get; init; } = new();

    public UiSettings Ui { get; init; } = new();

    /// <summary>
    /// Colour theme. <b>Light by default, even when Windows is set to dark.</b>
    /// </summary>
    /// <remarks>
    /// Light is the primary designed theme: a laboratory runs eight-hour
    /// cultivations and needs the brighter workspace for graphs, numbers and the
    /// process diagram. Dark exists for low-light rooms and is chosen
    /// deliberately, not inherited from the desktop. <see cref="ThemePreference.System"/>
    /// remains available for anyone who wants the app to follow Windows.
    /// See <c>docs/UI_DESIGN.md</c> section 1.3.
    /// </remarks>
    public ThemePreference Theme { get; init; } = ThemePreference.Light;

    /// <summary>Projects calibration and filter settings onto the protocol layer.</summary>
    public ParserConfig ToParserConfig() => new()
    {
        OxygenCalibrationA = Calibration.OxygenA,
        OxygenCalibrationB = Calibration.OxygenB,
        PHSlope = Calibration.PHSlope,
        PHIntercept = Calibration.PHIntercept,
        PHFilter = new SpikeFilterConfig(
            Filters.PHAbsoluteThreshold, Filters.PHFollowTolerance, Filters.PHConfirmRuns),
        OxygenFilter = new SpikeFilterConfig(
            Filters.OxygenAbsoluteThreshold, Filters.OxygenFollowTolerance, Filters.OxygenConfirmRuns),
    };
}

/// <summary>How and where to reach the controller.</summary>
public sealed record ConnectionSettings
{
    /// <summary>Medium tried first on launch.</summary>
    public TransportMedium PreferredMedium { get; init; } = TransportMedium.Usb;

    /// <summary>
    /// Last COM port that completed a handshake. Tried first, which turns the common
    /// case into a single fast connect rather than a scan.
    /// </summary>
    public string? LastKnownPort { get; init; }

    public string IpAddress { get; init; } = "192.168.4.1";

    /// <summary>
    /// Connect on launch without being asked. The connect runs after the shell is on
    /// screen, never before - see <c>docs/UI_DESIGN.md</c>.
    /// </summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Cycle to the other medium automatically when a link drops.</summary>
    public bool BackupEnabled { get; init; } = true;

    /// <summary>Telemetry emission period requested from the device, in milliseconds.</summary>
    public int DataDelayMs { get; init; } = 2000;
}

/// <summary>
/// Probe calibration.
/// </summary>
/// <remarks>
/// Defaults match the values actually in the field (v.6 <c>preferences.json</c>).
/// v.6's hard-coded defaults disagreed with its own saved values, so any fallback
/// silently applied a different calibration - see <c>docs/MIGRATION.md</c> item 2.
/// </remarks>
public sealed record CalibrationSettings
{
    public double OxygenA { get; init; } = 0.0305473419314;
    public double OxygenB { get; init; } = -25.09136520919;
    public double PHSlope { get; init; } = 0.0005012405704;
    public double PHIntercept { get; init; } = -0.600385955239;

    /// <summary>Decodes a raw oxygen count with these coefficients.</summary>
    public double DecodeOxygen(double raw) => Math.Max((OxygenA * raw) + OxygenB, 0.0);

    /// <summary>Decodes a raw pH count with these coefficients.</summary>
    public double DecodePH(double raw) => (PHSlope * raw) + PHIntercept;
}

/// <summary>
/// Spike-filter tuning, per channel.
/// </summary>
/// <remarks>
/// <b>These thresholds are in raw ADC counts, not engineering units.</b> Re-calibrating
/// a channel therefore changes what the filter considers a spike. That coupling is
/// inherited from v.6 and is a known defect - see <c>docs/MIGRATION.md</c> item 4. It is
/// surfaced here rather than hidden so that anyone tuning one is at least aware of the
/// other.
/// </remarks>
public sealed record FilterSettings
{
    public double PHAbsoluteThreshold { get; init; } = 500.0;
    public double PHFollowTolerance { get; init; } = 200.0;
    public int PHConfirmRuns { get; init; } = 3;

    public double OxygenAbsoluteThreshold { get; init; } = 150.0;
    public double OxygenFollowTolerance { get; init; } = 50.0;
    public int OxygenConfirmRuns { get; init; } = 3;
}

/// <summary>
/// Last-entered setpoints for the Phase 1 core loop.
/// </summary>
/// <remarks>
/// Persisted so the operator does not retype them every launch. <b>Restoring a value
/// into a field is not the same as sending it</b> - nothing here is transmitted on
/// connect. The device keeps its own state, and silently re-asserting a stale
/// setpoint over it would be an unpleasant surprise.
/// </remarks>
public sealed record SetpointSettings
{
    public double TemperatureCelsius { get; init; } = 30.0;
    public int MotorRpm { get; init; } = 300;
    public double OxygenPercent { get; init; } = 40.0;
    public double FlowLitresPerMinute { get; init; } = 1.0;
    public double MaxFlowLitresPerMinute { get; init; } = 50.0;
    public double PressureKilopascal { get; init; } = 100.0;
}

/// <summary>
/// Shell layout choices the operator makes and expects to find again.
/// </summary>
/// <remarks>
/// These are preferences, not configuration: nothing here reaches the wire. They are
/// persisted because a KPI strip that forgets its tiles every launch is worse than one
/// that cannot be configured at all.
/// </remarks>
public sealed record UiSettings
{
    /// <summary>
    /// Variables shown in the KPI strip, in order. Empty means "use the default set".
    /// </summary>
    /// <remarks>
    /// Stored as ids rather than indices so adding a variable in a later phase cannot
    /// silently re-point an operator's pinned set at the wrong readings.
    /// </remarks>
    public string[] PinnedKpis { get; init; } = [];

    /// <summary>
    /// Option B's variable rail. Off by default: process-first, instrumentation-dense
    /// only when asked for. See <c>docs/UI_DESIGN.md</c> section 1.1.
    /// </summary>
    public bool ShowVariableRail { get; init; }
}

/// <summary>Where session data is written.</summary>
public sealed record LoggingSettings
{
    /// <summary>
    /// Tab-separated session log. Empty means logging is off.
    /// </summary>
    /// <remarks>
    /// Format is byte-compatible with v.6 so the existing analysis scripts keep
    /// working - see <see cref="SessionLogFormat"/>.
    /// </remarks>
    public string? SessionLogPath { get; init; }

    /// <summary>Append to an existing file rather than starting a new one per run.</summary>
    public bool AppendToExisting { get; init; } = true;
}

/// <summary>
/// The v.6 session-log format, kept byte-compatible.
/// </summary>
/// <remarks>
/// Tab-separated, UTF-8, <c>.txt</c>, header written only when the file is new.
/// Existing analysis scripts read this, so the column set, order and decimal places
/// are a contract - not a formatting preference.
/// <para>
/// Columns absent from the Phase 1 scope (pH, antifoam, distance, OUR, biomass, pump)
/// are still emitted, carrying the not-received sentinel, so the column count never
/// changes between versions of this app.
/// </para>
/// </remarks>
public static class SessionLogFormat
{
    /// <summary>Exact header line v.6 writes, including the accented final column.</summary>
    public const string Header =
        "Time (min)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t" +
        "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\tBiomass\tPump Volume\tPump Flow\tConexão";

    /// <summary>Decimal places per column, matching v.6 exactly.</summary>
    public static readonly int[] Decimals = [2, 2, 3, 2, 3, -1, 3, 3, 2, 5, 4, 3, 3];
}

/// <summary>Where the application keeps its files.</summary>
public static class AppPaths
{
    private const string FolderName = "TECNAL-Hub";

    /// <summary>Per-user application data directory, created on demand.</summary>
    public static string DataDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                FolderName);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory
    {
        get
        {
            var path = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
