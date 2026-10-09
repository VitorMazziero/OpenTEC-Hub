using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.Services.Persistence;

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
    public int Version { get; init; } = 2;

    public ConnectionSettings Connection { get; init; } = new();

    public CalibrationSettings Calibration { get; init; } = new();

    /// <summary>Staged pH dosing parameters. Restoring them never sends a command.</summary>
    public PHControlSettings PHControl { get; init; } = new();

    /// <summary>Staged nutrient dosing parameters (WP7). Restoring them never sends a command.</summary>
    public NutrientControlSettings NutrientControl { get; init; } = new();

    /// <summary>Staged antifoam dosing parameters (WP7). Restoring them never sends a command.</summary>
    public AntifoamControlSettings AntifoamControl { get; init; } = new();

    /// <summary>Staged level/foam sensor configuration (WP7). Restoring it never sends a command.</summary>
    public FoamControlSettings FoamControl { get; init; } = new();

    /// <summary>Staged flowmeter tuning parameters (Phase 1 / Hub 10.2). Restoring them never sends a command.</summary>
    public FlowControlSettings FlowControl { get; init; } = new();

    /// <summary>Staged flask-agitator parameters (WP7). Restoring them never sends a command.</summary>
    public FlaskAgitatorSettings FlaskAgitator { get; init; } = new();

    /// <summary>How the A/B/C gas valves are wired to the flowmeter's two outputs. Absent = the documented default.</summary>
    public GasRigSettings GasRig { get; init; } = new();

    /// <summary>Staged biomass thresholds (Phase 3 WP1). The sensor enable is never persisted.</summary>
    public BiomassControlSettings BiomassControl { get; init; } = new();

    /// <summary>Versioned external-pump profile and gas coupling (Phase 3 WP2). Restoring it never sends.</summary>
    public PumpControlSettings PumpControl { get; init; } = new();

    public FilterSettings Filters { get; init; } = new();

    /// <summary>Presentation units. Values on the wire remain in the protocol units.</summary>
    public UnitSettings Units { get; init; } = new();

    public SetpointSettings Setpoints { get; init; } = new();

    /// <summary>
    /// Named, operator-created core-loop configurations. Loading one only stages
    /// fields in the Controle page; it never sends a command by itself.
    /// </summary>
    public SetpointPreset[] SetpointPresets { get; init; } = [SetpointPreset.DefaultPreset];

    public LoggingSettings Logging { get; init; } = new();

    /// <summary>Oxygen-cascade tuning. Advisory in Phase 2 WP2; it does not actuate yet.</summary>
    public CascadeSettings Cascade { get; init; } = new();

    /// <summary>Conditional-OUR soft-sensor tuning (WP8). Observation only; it never actuates.</summary>
    public OurSettings Our { get; init; } = new();

    /// <summary>Versioned gain schedule for the oxygen cascade (WP8). Off by default.</summary>
    public GainScheduleSettings GainSchedule { get; init; } = new();

    /// <summary>Named, operator-saved cascade tunings. Loading one only stages the fields.</summary>
    public CascadeTuningPreset[] CascadeTuningPresets { get; init; } = [];

    /// <summary>Preferred external-bath route and staged cascade tuning.</summary>
    public ExternalBathSettings ExternalBath { get; init; } = new();

    /// <summary>Last chart layout. Channel ids are stable enum names, never list indices.</summary>
    public ChartSettings Charts { get; init; } = new();

    /// <summary>Persisted parameters for kLa determination tests. Which output carries which gas is <see cref="GasRig"/>.</summary>
    public KlaTestSettings KlaTest { get; init; } = new();

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

    /// <summary>
    /// Whether the operator's last session ended in a connected/connecting state.
    /// </summary>
    /// <remarks>
    /// Set true whenever a link is opened and false on an explicit disconnect, so a
    /// restart resumes the last state instead of always re-probing USB: a session left
    /// connected reconnects over <see cref="PreferredMedium"/> (Wi-Fi stays Wi-Fi, USB
    /// stays USB), while one the operator disconnected stays offline until they connect
    /// again. Gated by <see cref="AutoConnect"/>; a link that merely dropped is not a
    /// disconnect, so an unattended reboot still resumes.
    /// </remarks>
    public bool LastSessionConnected { get; init; } = true;

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

    /// <summary>Accepted raw frames required before pH is considered stable.</summary>
    public int PHStabilityWindow { get; init; } = 20;

    /// <summary>Maximum sample standard deviation in raw ADC counts.</summary>
    public double PHStabilityStandardDeviation { get; init; } = 5.0;

    /// <summary>Accepted raw frames averaged after stability is reached.</summary>
    public int PHAverageSamples { get; init; } = 20;

    /// <summary>Accepted raw frames required before Oxygen is considered stable.</summary>
    public int OxygenStabilityWindow { get; init; } = 20;

    /// <summary>Maximum sample standard deviation in raw ADC counts for Oxygen.</summary>
    public double OxygenStabilityStandardDeviation { get; init; } = 5.0;

    /// <summary>Accepted raw frames averaged after stability is reached for Oxygen.</summary>
    public int OxygenAverageSamples { get; init; } = 20;

    /// <summary>Distinct FlowVoltage frames averaged for one certified flow point.</summary>
    public int FlowCaptureSamples { get; init; } = 10;

    /// <summary>
    /// Operator-certified flow points. Coefficients are derived deterministically and
    /// reach the flowmeter only through an explicit send action.
    /// </summary>
    /// <remarks>
    /// Seeded with <see cref="CertifiedReferencePoints"/>, so the workspace opens on the same
    /// points and curve the flowmeter is actually running.
    /// </remarks>
    public FlowCalibrationPoint[] FlowCalibrationPoints { get; init; } = CertifiedReferencePoints;

    /// <summary>
    /// Transition voltage threshold (V) between low and high curve segments of the flowmeter.
    /// Default is 0.0545 V.
    /// </summary>
    public double FlowTransitionVoltage { get; init; } = FlowCalibrationCurve.DefaultTransitionVoltage;

    /// <summary>
    /// Provenance of the points above: the A/B/C wiring and the route the air took while they
    /// were captured (C by default; the reactor on request). Null on points captured before
    /// the rig, or on the shipped reference.
    /// </summary>
    public GasRigSettings? FlowCalibrationGasRig { get; init; }
    public GasRoute? FlowCalibrationRoute { get; init; }

    /// <summary>
    /// The certified bench run behind the calibration shipped in
    /// <c>flowmeter_OpenTECHUB_V05.ino</c>. Fitting these regenerates the firmware's own
    /// coefficients, so they double as the reference to fall back on and to compare against.
    /// </summary>
    public static FlowCalibrationPoint[] CertifiedReferencePoints =>
    [
        new() { FlowLitresPerMinute = 0.0, Voltage = 0.010330 },
        new() { FlowLitresPerMinute = 0.5, Voltage = 0.024090 },
        new() { FlowLitresPerMinute = 0.75, Voltage = 0.040640 },
        new() { FlowLitresPerMinute = 1.0, Voltage = 0.067500 },
        new() { FlowLitresPerMinute = 2.0, Voltage = 0.157940 },
        new() { FlowLitresPerMinute = 4.0, Voltage = 0.332030 },
        new() { FlowLitresPerMinute = 6.0, Voltage = 0.502310 },
        new() { FlowLitresPerMinute = 8.0, Voltage = 0.694720 },
        new() { FlowLitresPerMinute = 10.0, Voltage = 0.897150 },
        new() { FlowLitresPerMinute = 12.0, Voltage = 1.080840 },
        new() { FlowLitresPerMinute = 14.0, Voltage = 1.287900 },
    ];

    /// <summary>Decodes a raw oxygen count with these coefficients.</summary>
    public double DecodeOxygen(double raw) => Math.Max((OxygenA * raw) + OxygenB, 0.0);

    /// <summary>Decodes a raw pH count with these coefficients.</summary>
    public double DecodePH(double raw) => (PHSlope * raw) + PHIntercept;
}

/// <summary>A real-flow / measured-voltage pair used by the two-segment curve fit.</summary>
/// <summary>One volumetric run of the external pump: S held for a measured time, V collected.</summary>
public sealed record PumpCalibrationPoint
{
    /// <summary>Internal speed S (0..1000) the run was held at.</summary>
    public double SpeedUnits { get; init; }

    /// <summary>How long the pump actually ran, from the start frame to the stop frame, in seconds.</summary>
    public double Seconds { get; init; }

    /// <summary>Volume collected in the graduated vessel, in mL.</summary>
    public double VolumeMl { get; init; }

    /// <summary>The run's flow, mL/min. Derived, never typed.</summary>
    public double FlowMlPerMin => Seconds > 0.0 ? VolumeMl / (Seconds / 60.0) : 0.0;

    /// <summary>When the run was added, UTC ISO-8601; null on points imported without one.</summary>
    public string? CapturedAtUtc { get; init; }
}

public sealed record FlowCalibrationPoint
{
    public double FlowLitresPerMinute { get; init; }

    public double Voltage { get; init; }

    /// <summary>
    /// Where the voltage came from: averaged from telemetry, or typed by the operator (§O of the
    /// 2026-09-11 plan). Files written before this field exist carry <see cref="FlowVoltageSource.Captured"/>.
    /// The calibration report needs to tell a measurement from a transcription.
    /// </summary>
    public FlowVoltageSource Source { get; init; } = FlowVoltageSource.Captured;
}

public enum FlowVoltageSource
{
    Captured,
    Typed,
}

/// <summary>
/// Complete pH dosing state understood by the ESP32-S3 and sensor module.
/// </summary>
/// <remarks>
/// Defaults mirror the saved v.6 field configuration, not an assertion that dosing
/// should be enabled. The ViewModel always starts disabled and merely stages these
/// values for review.
/// </remarks>
public sealed record PHControlSettings
{
    public double Setpoint { get; init; } = 7.0;

    public double InactiveBand { get; init; } = 0.17;

    public int OperationSeconds { get; init; } = 3;

    public int MixSeconds { get; init; } = 10;

    public double PumpSpeedPercent { get; init; } = 99.0;
}

/// <summary>
/// Complete nutrient dosing state (WP7): operation/mix timing, the two cycle counts and
/// pump intensity.
/// </summary>
/// <remarks>
/// Defaults are a coherent starting point, not a field configuration. The ViewModel always
/// starts disabled and merely stages these values for review — nothing is sent on launch.
/// Intensity is the raw operator percent (0-99); unlike pH it is not multiplied by ten.
/// </remarks>
public sealed record NutrientControlSettings
{
    public int OperationSeconds { get; init; } = 999;

    public int MixSeconds { get; init; } = 1;

    public int OperationCycles { get; init; } = 500;

    public int MixCycles { get; init; } = 1;

    public double PumpSpeedPercent { get; init; } = 99.0;
}

/// <summary>
/// Complete antifoam dosing state (WP7): operation/mix timing and pump intensity.
/// </summary>
/// <remarks>
/// Defaults are a coherent starting point, not a field configuration; the ViewModel starts
/// disabled and only stages them. Operation accepts 0-999 s, mix 1-999 s and intensity the
/// raw 0-99 percent.
/// </remarks>
public sealed record AntifoamControlSettings
{
    public int OperationSeconds { get; init; } = 2;

    public int MixSeconds { get; init; } = 2;

    public double PumpSpeedPercent { get; init; } = 50.0;
}

/// <summary>
/// Level/foam sensor configuration (WP7): the sensor enable, its reference height and the
/// three timers of the automatic antifoam response.
/// </summary>
/// <remarks>
/// This is sensor and automation configuration rather than a held actuator, so it sits
/// outside the command arbiter and the global safe-stop. The ViewModel still only stages it.
/// </remarks>
public sealed record FoamControlSettings
{
    public bool SensorEnabled { get; init; }

    public double ReferenceMillimetres { get; init; } = 100.0;

    public int StartDelaySeconds { get; init; } = 30;

    public int PulseSeconds { get; init; } = 2;

    public int IntervalSeconds { get; init; } = 30;

    /// <summary>Staged distance sensor offset in mm (Hub 10.2 / Node v11). Restoring never sends.</summary>
    public double DistanceOffsetMm { get; init; } = 20.0;

    /// <summary>Staged distance sensor sampling period in ms (Hub 10.2 / Node v11).</summary>
    public int DistanceSamplePeriodMs { get; init; } = 1000;

    /// <summary>Staged distance sensor send period in ms (Hub 10.2 / Node v11).</summary>
    public int DistanceSendPeriodMs { get; init; } = 1000;
}

/// <summary>
/// Staged flowmeter controller tuning parameters (Hub 10.2 / Node v11).
/// </summary>
/// <remarks>
/// Restoring these parameters never sends a command automatically; the operator must explicitly apply.
/// </remarks>
/// <summary>
/// Wiring of the gas rig: which flowmeter output drives valve A; B and C share the other.
/// </summary>
/// <remarks>
/// The default is the physical document's (MOSFET 2 → A, MOSFET 1 → B+C), so a settings file
/// written before this record existed migrates to it silently — that <i>is</i> the bench's
/// wiring. Changing it is a Configurações decision, never a side effect of a command.
/// </remarks>
public sealed record GasRigSettings
{
    public GasInput AirInletInput { get; init; } = GasRigConfiguration.Default.AirInletInput;

    public GasRigConfiguration ToConfiguration() => new(AirInletInput);

    public static GasRigSettings From(GasRigConfiguration rig) => new() { AirInletInput = rig.AirInletInput };
}

/// <summary>
/// The wiring as a provenance string: what goes into manifests, receipts and sidecars, and the
/// phrase a reviewer sees on a file older than the rig. Same shape everywhere
/// (<c>A=2 B/C=1</c>), so grep finds it.
/// </summary>
public static class GasRigProvenance
{
    public const string Unknown = "desconhecido (anterior ao arranjo A/B/C)";

    public static string Describe(GasRigSettings? rig)
        => rig is null ? Unknown : Describe(rig.ToConfiguration());

    public static string Describe(GasRigConfiguration rig)
        => $"A={(int)rig.AirInletInput} B/C={(int)rig.VentAndNitrogenInput}";
}

public sealed record FlowControlSettings
{
    public double Kp { get; init; } = 0.8;

    public double Ki { get; init; } = 0.15;

    public double FfGain { get; init; } = 0.106;

    public double FfOffset { get; init; } = 0.01033;

    public double RampRate { get; init; } = 2.0;
}

/// <summary>
/// Flask-agitator state (WP7). A separate bench device, not the reactor impeller.
/// </summary>
/// <remarks>
/// The operator sets a magnitude and a direction; the ViewModel combines them into the
/// signed percent the command builder splits back onto the wire. The enabled ("ligado")
/// state is deliberately not persisted — the ViewModel starts stopped, exactly as pH does.
/// </remarks>
public sealed record FlaskAgitatorSettings
{
    public double MagnitudePercent { get; init; } = 50.0;

    /// <summary><c>true</c> for clockwise (<c>agitatorDir:1</c>), <c>false</c> for counter-clockwise.</summary>
    public bool Clockwise { get; init; } = true;

    public bool Automatic { get; init; }
}

/// <summary>
/// Staged biomass thresholds (WP1): the low/high/optimal integration-time bounds in raw counts.
/// </summary>
/// <remarks>
/// Defaults mirror v.6's biomass block (10000 / 40000 / 25000). The sensor enable is deliberately
/// not stored — like pH dosing, the ViewModel starts with the sensor off and only stages these
/// values for review. Raw ADC counts, not engineering units.
/// </remarks>
public sealed record BiomassControlSettings
{
    public int LowThreshold { get; init; } = 10000;

    public int HighThreshold { get; init; } = 40000;

    public int OptimalThreshold { get; init; } = 25000;

    /// <summary>Integration time in milliseconds (one of 25, 50, 100, 200, 400 or 800 ms).</summary>
    public int IntegrationTime { get; init; } = 100;

    /// <summary>LED PWM drive percent (0..100%).</summary>
    public double PwmPercent { get; init; } = 2.0;

    /// <summary>Combined optical gear (IT index * 8 + PWM index, 0..31).</summary>
    public int GainGear { get; init; }

    /// <summary>Exponential moving average smoothing factor (0.01..1.0).</summary>
    public double EmaFactor { get; init; } = 0.8;

    /// <summary>Acquisition probe period in milliseconds (100..3600000 ms; firmware may raise it to its thermal floor).</summary>
    public int ProbePeriodMs { get; init; } = 25000;
}

/// <summary>
/// A versioned external-pump profile (WP2): the active mode, its operating window and every
/// mode's parameters, plus the optional proportional-gas coupling.
/// </summary>
/// <remarks>
/// <para>
/// One record holds all five modes' last-entered values so switching mode in the UI never loses
/// a set; only the fields the active <see cref="Mode"/> needs are sent. Times are minutes, flows
/// mL/min. The pump enable is not persisted — the ViewModel starts with the pump off.
/// </para>
/// <para>
/// <see cref="Version"/> is bumped on each applied profile send, so a change is auditable — the
/// same discipline the gain schedule uses. Unlike v.6, the operating window is shared across
/// modes rather than stored per mode (a simplification recorded in <c>docs/DECISIONS.md</c>).
/// </para>
/// </remarks>
public sealed record PumpControlSettings
{
    /// <summary>Bumped on each applied profile send, so a persisted change is a versioned event.</summary>
    public int Version { get; init; } = 1;

    public PumpProfileMode Mode { get; init; } = PumpProfileMode.Constant;

    public double InitMinutes { get; init; }

    public double FinalMinutes { get; init; } = 60.0;

    public double LambdaConst { get; init; } = 1.0;

    public double LambdaLinear { get; init; } = 1.0;

    public double PhiLinear { get; init; }

    public double LambdaExp { get; init; } = 1.0;

    public double PhiExp { get; init; }

    public double[] PolynomialCoefficients { get; init; } = [1.0];

    public double[] PiecewiseTimes { get; init; } = [0.0, 60.0];

    public double[] PiecewiseFlows { get; init; } = [1.0, 1.0];

    /// <summary>When set, the pump volume drives the air flow: <c>Q_g = (V₀ + PumpVol/1000)·vvm</c>.</summary>
    public bool GasProportionalEnabled { get; init; }

    /// <summary>Initial working volume V₀ in litres, used by the proportional-gas coupling.</summary>
    public double InitialVolumeLitres { get; init; } = 1.0;

    /// <summary>Specific aeration rate vvm (L gas per L medium per min), for the coupling.</summary>
    public double Vvm { get; init; } = 0.5;

    /// <summary>Calibration slope: flow = slope * internal speed unit + intercept.</summary>
    public double CalibrationSlope { get; init; } = 0.0280188148;

    /// <summary>Calibration intercept: flow = slope * internal speed unit + intercept.</summary>
    public double CalibrationIntercept { get; init; } = 1.7601988934;

    /// <summary>Continuous dual-range calibration: transition flow Qt in mL/min.</summary>
    public double CalibrationQt { get; init; } = 15.7696062934;

    /// <summary>Continuous dual-range calibration: transition speed St in internal speed units (0..1000).</summary>
    public double CalibrationSt { get; init; } = 500.0;

    /// <summary>Continuous dual-range calibration: low slope m_baixo (mL/min per S unit).</summary>
    public double CalibrationMLow { get; init; } = 0.0280188148;

    /// <summary>Continuous dual-range calibration: high slope m_alto (mL/min per S unit).</summary>
    public double CalibrationMHigh { get; init; } = 0.0280188148;

    /// <summary>
    /// Volumetric calibration runs the operator has kept: one timed run at a fixed internal
    /// speed and the volume it delivered. The coefficients above are derived from these only
    /// through the explicit "usar ajuste" action, never on save.
    /// </summary>
    public PumpCalibrationPoint[] CalibrationPoints { get; init; } = [];

    /// <summary>Proportional gain staged for pump controller.</summary>
    public double PidKp { get; init; } = 0.5;

    /// <summary>Integral gain staged for pump controller.</summary>
    public double PidKi { get; init; } = 0.05;

    /// <summary>Derivative gain staged for pump controller.</summary>
    public double PidKd { get; init; } = 0.001;

    /// <summary>
    /// Currently active external pump hose calibration profile name, if any.
    /// </summary>
    public string? SelectedProfileName { get; init; } = null;
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

/// <summary>Pressure units available for operator-facing presentation.</summary>
public enum PressureUnitPreference
{
    KPa,
    MmHg,
    Bar,
}

/// <summary>Temperature units available for operator-facing presentation.</summary>
public enum TemperatureUnitPreference
{
    Celsius,
    Fahrenheit,
}

/// <summary>
/// Display-only unit preferences. OpenTEC's protocol remains Celsius and kPa.
/// </summary>
public sealed record UnitSettings
{
    public PressureUnitPreference Pressure { get; init; } = PressureUnitPreference.MmHg;

    public TemperatureUnitPreference Temperature { get; init; } = TemperatureUnitPreference.Celsius;

    /// <summary>Nominal working vessel volume; reserved for derived values in Phase 2.</summary>
    public double VesselVolumeLitres { get; init; } = 5.0;
}

/// <summary>Canonical/display conversions shared by readouts, controls and charts.</summary>
public static class UnitConversions
{
    private const double MillimetresMercuryPerKilopascal = 7.500616827;

    public static string PressureLabel(PressureUnitPreference unit) => unit switch
    {
        PressureUnitPreference.MmHg => "mmHg",
        PressureUnitPreference.Bar => "bar",
        _ => "kPa",
    };

    public static string TemperatureLabel(TemperatureUnitPreference unit) => unit switch
    {
        TemperatureUnitPreference.Fahrenheit => "°F",
        _ => "°C",
    };

    public static double PressureToDisplay(double kilopascal, PressureUnitPreference unit) => unit switch
    {
        PressureUnitPreference.MmHg => kilopascal * MillimetresMercuryPerKilopascal,
        PressureUnitPreference.Bar => kilopascal / 100.0,
        _ => kilopascal,
    };

    public static double PressureToCanonical(double display, PressureUnitPreference unit) => unit switch
    {
        PressureUnitPreference.MmHg => display / MillimetresMercuryPerKilopascal,
        PressureUnitPreference.Bar => display * 100.0,
        _ => display,
    };

    public static double TemperatureToDisplay(double celsius, TemperatureUnitPreference unit) => unit switch
    {
        TemperatureUnitPreference.Fahrenheit => (celsius * 9.0 / 5.0) + 32.0,
        _ => celsius,
    };

    public static double TemperatureToCanonical(double display, TemperatureUnitPreference unit) => unit switch
    {
        TemperatureUnitPreference.Fahrenheit => (display - 32.0) * 5.0 / 9.0,
        _ => display,
    };
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
    public bool TemperatureEnabled { get; init; }
    public int MotorRpm { get; init; } = 300;
    public bool MotorEnabled { get; init; }
    public double OxygenPercent { get; init; } = 40.0;
    public bool OxygenEnabled { get; init; }
    public double FlowLitresPerMinute { get; init; } = 1.0;
    public double MaxFlowLitresPerMinute { get; init; } = 50.0;
    public bool FlowEnabled { get; init; }
    public double PressureKilopascal { get; init; } = 100.0;
    public bool PressureEnabled { get; init; }
}

/// <summary>
/// A named snapshot of every Phase 1 command field.
/// </summary>
/// <remarks>
/// The enabled flags are stored separately from the setpoint values so a preset can
/// stage a useful value for a subsystem that should begin the run disabled. Presets
/// are data-entry aids, not recipes: loading one never reaches the wire.
/// </remarks>
public sealed record SetpointPreset
{
    public string Name { get; init; } = "";

    public double TemperatureCelsius { get; init; } = 30.0;
    public bool TemperatureEnabled { get; init; }

    public int MotorRpm { get; init; } = 300;
    public bool MotorEnabled { get; init; }

    public double OxygenPercent { get; init; } = 30.0;
    public bool OxygenEnabled { get; init; }
    public string OxygenMode { get; init; } = "Cascata";
    public CascadeSettings? Cascade { get; init; }

    public PHControlSettings PHControl { get; init; } = new();

    public bool PHControlEnabled { get; init; }

    public double FlowLitresPerMinute { get; init; } = 1.0;
    public double MaxFlowLitresPerMinute { get; init; } = 50.0;
    public bool FlowEnabled { get; init; }
    public bool Valve1Open { get; init; }
    public bool Valve2Open { get; init; }

    public double PressureKilopascal { get; init; } = 101.3;
    public bool PressureEnabled { get; init; }

    public static SetpointPreset DefaultPreset => new()
    {
        Name = "Padrão de Cultivo",
        TemperatureCelsius = 37.0,
        TemperatureEnabled = true,
        MotorRpm = 300,
        MotorEnabled = true,
        OxygenPercent = 30.0,
        OxygenEnabled = true,
        OxygenMode = "Cascata",
        FlowLitresPerMinute = 1.0,
        MaxFlowLitresPerMinute = 50.0,
        FlowEnabled = true,
        // A cultivation aerates the reactor: A, which is input 2 on the default A/B/C wiring.
        Valve1Open = false,
        Valve2Open = true,
        PressureKilopascal = 101.3,
        PressureEnabled = true,
        PHControl = new PHControlSettings
        {
            Setpoint = 7.0,
            InactiveBand = 0.17,
            OperationSeconds = 3,
            MixSeconds = 10,
            PumpSpeedPercent = 99.0
        },
        PHControlEnabled = true,
        Cascade = new CascadeSettings
        {
            OxygenSetpointPercent = 30.0,
            Mode = CascadeMode.DualCascade,
            AgitationMinRpm = 50,
            AgitationMaxRpm = 800,
            AgitationEffortStart = 0,
            AgitationEffortEnd = 90,
            AerationMinLpm = 0.5,
            AerationMaxLpm = 12.0,
            AerationEffortStart = 10,
            AerationEffortEnd = 100,
            CascadePid = new ModePidSettings
            {
                KDot = 0.075,
                Kp = 0.035,
                Ki = 0.0010,
                Kd = 1.500,
                TPred = 90.0,
                TauD = 25.0,
                IMin = -1.0,
                IMax = 1.0,
                MWindow = 2400,
                JAvg = 8,
                NPred = 20,
                IntervalSeconds = 3.0,
                FatorGanhoAeracao = 1.43,
                HabilitarGainScheduling = true,
            }
        }
    };

    public override string ToString() => Name;
}

/// <summary>
/// Persisted PID and rate-filter tuning for a specific oxygen control mode.
/// </summary>
public sealed record ModePidSettings
{
    public double KDot { get; init; } = 0.075;
    public double Kp { get; init; } = 0.035;
    public double Ki { get; init; } = 0.001;
    public double Kd { get; init; } = 1.50;
    public double TPred { get; init; } = 90.0;
    public double TauD { get; init; } = 25.0;
    public double IMin { get; init; } = -1.0;
    public double IMax { get; init; } = 1.0;
    public int MWindow { get; init; } = 2400;
    public int JAvg { get; init; } = 8;
    public int NPred { get; init; } = 20;
    public double IntervalSeconds { get; init; } = 3.0;
    public double FatorGanhoAeracao { get; init; } = 1.43;
    public bool HabilitarGainScheduling { get; init; } = true;
}

/// <summary>
/// Persisted oxygen-cascade tuning.
/// </summary>
public sealed record CascadeSettings
{
    public double OxygenSetpointPercent { get; init; } = 30.0;
    public CascadeMode Mode { get; init; } = CascadeMode.DualCascade;

    public double AgitationMinRpm { get; init; } = 50;
    public double AgitationMaxRpm { get; init; } = 800;
    public double AgitationEffortStart { get; init; } = 0;
    public double AgitationEffortEnd { get; init; } = 90;

    public double AerationMinLpm { get; init; } = 0.5;
    public double AerationMaxLpm { get; init; } = 12.0;

    /// <summary>
    /// Grid of the aeration setpoint the cascade sends to the flowmeter, in L/min (D-070): the command only
    /// changes in multiples of this step. Default 0.2; the operator edits it in the oxygen tuning.
    /// </summary>
    public double AerationStepLpm { get; init; } = 0.2;

    public double AerationEffortStart { get; init; } = 10;
    public double AerationEffortEnd { get; init; } = 100;

    public ModePidSettings AgitationPid { get; init; } = new()
    {
        KDot = 0.10,
        Kp = 0.10,
        Ki = 0.002,
        Kd = 0.75,
        TPred = 30.0,
        TauD = 40.0,
        IMin = -200.0,
        IMax = 200.0,
        MWindow = 180,
        JAvg = 10,
        NPred = 10,
        IntervalSeconds = 3.0,
        HabilitarGainScheduling = false,
    };

    public ModePidSettings AerationPid { get; init; } = new()
    {
        KDot = 0.10,
        Kp = 0.001,
        Ki = 0.0002,
        Kd = 0.0075,
        TPred = 30.0,
        TauD = 40.0,
        IMin = -200.0,
        IMax = 200.0,
        MWindow = 180,
        JAvg = 10,
        NPred = 10,
        IntervalSeconds = 3.0,
        HabilitarGainScheduling = false,
    };

    /// <summary>Author's bench defaults for the cascade mode (08/10/2026, D-068).</summary>
    public ModePidSettings CascadePid { get; init; } = new()
    {
        KDot = 0.075,
        Kp = 0.035,
        Ki = 0.0010,
        Kd = 1.500,
        TPred = 90.0,
        TauD = 25.0,
        IMin = -1.0,
        IMax = 1.0,
        MWindow = 2400,
        JAvg = 8,
        NPred = 20,
        IntervalSeconds = 3.0,
        FatorGanhoAeracao = 1.43,
        HabilitarGainScheduling = true,
    };

    public ModePidSettings MapPid { get; init; } = new()
    {
        KDot = 0.15,
        Kp = 0.75,
        Ki = 0.10,
        Kd = 0.50,
        TPred = 30.0,
        TauD = 40.0,
        IMin = -500.0,
        IMax = 500.0,
        MWindow = 60,
        JAvg = 5,
        NPred = 5,
        IntervalSeconds = 2.0,
        HabilitarGainScheduling = false,
    };

    // Legacy fields for backward compatibility
    [Obsolete("Migrated to CascadePid")]
    public double Kp
    {
        get => CascadePid.Kp;
        init
        {
            CascadePid = CascadePid with { Kp = value };
            MapPid = MapPid with { Kp = value };
            AgitationPid = AgitationPid with { Kp = value };
            AerationPid = AerationPid with { Kp = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double Ki
    {
        get => CascadePid.Ki;
        init
        {
            CascadePid = CascadePid with { Ki = value };
            MapPid = MapPid with { Ki = value };
            AgitationPid = AgitationPid with { Ki = value };
            AerationPid = AerationPid with { Ki = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double Kd
    {
        get => CascadePid.Kd;
        init
        {
            CascadePid = CascadePid with { Kd = value };
            MapPid = MapPid with { Kd = value };
            AgitationPid = AgitationPid with { Kd = value };
            AerationPid = AerationPid with { Kd = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double IntegralMin
    {
        get => CascadePid.IMin;
        init
        {
            CascadePid = CascadePid with { IMin = value };
            MapPid = MapPid with { IMin = value };
            AgitationPid = AgitationPid with { IMin = value };
            AerationPid = AerationPid with { IMin = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double IntegralMax
    {
        get => CascadePid.IMax;
        init
        {
            CascadePid = CascadePid with { IMax = value };
            MapPid = MapPid with { IMax = value };
            AgitationPid = AgitationPid with { IMax = value };
            AerationPid = AerationPid with { IMax = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double PredictionHorizonSeconds
    {
        get => CascadePid.TPred;
        init
        {
            CascadePid = CascadePid with { TPred = value };
            MapPid = MapPid with { TPred = value };
            AgitationPid = AgitationPid with { TPred = value };
            AerationPid = AerationPid with { TPred = value };
        }
    }

    [Obsolete("Migrated to CascadePid")]
    public double RateWindowSeconds { get; init; } = 25.0;

    [Obsolete("Migrated to CascadePid")]
    public double IntervalSeconds
    {
        get => CascadePid.IntervalSeconds;
        init
        {
            CascadePid = CascadePid with { IntervalSeconds = value };
            MapPid = MapPid with { IntervalSeconds = value };
            AgitationPid = AgitationPid with { IntervalSeconds = value };
            AerationPid = AerationPid with { IntervalSeconds = value };
        }
    }
}

/// <summary>A named cascade tuning. Loading one stages fields; it never actuates.</summary>
public sealed record CascadeTuningPreset
{
    public string Name { get; init; } = "";

    public CascadeSettings Settings { get; init; } = new();

    public override string ToString() => Name;
}

/// <summary>
/// Conditional-OUR soft-sensor tuning (WP8). Plain data; the mapping onto the sensor lives in
/// <c>OurSoftSensorService</c>, so this record does not depend on the control layer.
/// </summary>
/// <remarks>
/// Defaults are the manuscript's (<c>analysis/2_our_soft_sensor</c>), except the rate window,
/// which is causal here rather than the paper's centred offline derivative.
/// </remarks>
public sealed record OurSettings
{
    /// <summary>Dissolved-oxygen saturation C* in mmol L⁻¹ (0.21 at 37 °C).</summary>
    public double OxygenSaturationMmolPerL { get; init; } = 0.21;

    /// <summary>DOT stability half-band around the setpoint, in percentage points.</summary>
    public double SetpointTolerancePercentPoints { get; init; } = 5.0;

    /// <summary>Largest quasi-steady |dDOT/dt|, in percentage points per hour.</summary>
    public double RateLimitPointsPerHour { get; init; } = 10.0;

    /// <summary>Gate half-band DOT must first enter before evaluation begins, in percentage points.</summary>
    public double GateTolerancePercentPoints { get; init; } = 2.0;

    /// <summary>Causal least-squares window for dDOT/dt, in seconds.</summary>
    public double RateWindowSeconds { get; init; } = 900.0;
}

/// <summary>
/// A versioned gain schedule for the oxygen cascade (WP8): PID gains as a function of the control
/// effort, with a bounded transition rate. Plain data; the mapping onto the controller lives in
/// <c>CascadeService</c>.
/// </summary>
/// <remarks>
/// <b>Off by default</b> — the manuscript's single robust gain set is workable without scheduling,
/// so this is an opt-in refinement. The default breakpoints scale the gains up with effort (the
/// loop gain scales as 1/kLa and effort maps onto kLa), and are provisional simulator values, not a
/// field tuning. <see cref="Version"/> is bumped on every applied edit so a change is auditable.
/// </remarks>
public sealed record GainScheduleSettings
{
    /// <summary>Bumped on each applied edit, so a schedule change is a versioned, journalled event.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Whether the schedule drives the cascade gains. Off keeps the single base tuning.</summary>
    public bool Enabled { get; init; }

    /// <summary>Largest change in any gain per second — the bound on a transition.</summary>
    public double MaxGainSlewPerSecond { get; init; } = 0.05;

    /// <summary>Breakpoints of (effort %, Kp, Ki, Kd), ordered by effort.</summary>
    public GainScheduleBreakpointSettings[] Breakpoints { get; init; } =
    [
        new(0.0, 0.15, 0.012, 0.0),
        new(50.0, 0.25, 0.020, 0.0),
        new(100.0, 0.40, 0.032, 0.0),
    ];
}

/// <summary>One persisted gain-schedule breakpoint: gains pinned to a control-effort percent.</summary>
public sealed record GainScheduleBreakpointSettings(double EffortPercent, double Kp, double Ki, double Kd);

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

    /// <summary>
    /// The speaker button in the title bar (D-069): when true the application plays no sound at all,
    /// indefinitely. It sits above the timed silence of the alarm bar, which it neither starts nor ends.
    /// </summary>
    public bool SoundMuted { get; init; }

    /// <summary>Last valid rail destination, stored by stable id rather than index.</summary>
    public string LastPage { get; init; } = "dashboard";

    /// <summary>Last normal window bounds and whether the window was maximized.</summary>
    public WindowPlacementSettings Window { get; init; } = new();
}

/// <summary>Serializable WPF-window placement without any UI-framework types.</summary>
public sealed record WindowPlacementSettings
{
    public double? Left { get; init; }

    public double? Top { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public bool IsMaximized { get; init; }

    [JsonIgnore]
    public bool HasBounds =>
        Left is { } left && double.IsFinite(left) &&
        Top is { } top && double.IsFinite(top) &&
        Width is { } width && double.IsFinite(width) && width > 0 &&
        Height is { } height && double.IsFinite(height) && height > 0;
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

/// <summary>Persisted operator choices for the Hub-routed external bath.</summary>
public sealed record ExternalBathSettings
{
    public bool PreferExternalRoute { get; init; }
    public bool CommunicationEnabled { get; init; }
    public bool Automatic { get; init; } = true;
    public bool PanelExpanded { get; init; } = true;
    public double ReactorSetpoint { get; init; } = 30.0;
    public double CascadeKp { get; init; } = 0.5;
    public double CascadeTi { get; init; } = 600.0;
    public double CascadeBias { get; init; } = 0.6;
    public int CascadePeriodMs { get; init; } = 10_000;
    public double CascadeFilter { get; init; } = 20.0;
    public int CascadeMinCommand { get; init; } = 30_000;
    public double CascadeBand { get; init; } = 0.1;
    public double CascadeSlew { get; init; } = 0.5;
    public double CascadeOffsetHigh { get; init; } = 5.0;
    public double CascadeOffsetLow { get; init; } = 5.0;
    public double CascadeOutputMin { get; init; } = 5.0;
    public double CascadeOutputMax { get; init; } = 90.0;

    /// <summary>Recipe temperature blocks (bath route): reactor band around the target.</summary>
    public double ReactorSettleBandC { get; init; } = 0.5;

    /// <summary>Recipe temperature blocks (bath route): time the reactor must stay in the band.</summary>
    public double ReactorSettleHoldS { get; init; } = 30.0;
}

/// <summary>Persisted chart panel/window/channel selection.</summary>
public sealed record ChartSettings
{
    public int PanelCount { get; init; } = 2;
    public string Window { get; init; } = "30 min";
    public string LeftChannel { get; init; } = nameof(TelemetryChannel.Temperature);
    public string RightChannel { get; init; } = nameof(TelemetryChannel.Oxygen);
    public string BottomLeftChannel { get; init; } = nameof(TelemetryChannel.PH);
    public string BottomRightChannel { get; init; } = nameof(TelemetryChannel.Flow);
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

/// <summary>
/// The servo drive's session sidecar: a separate file, written beside the main log.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sidecar rather than new columns.</b> The main log's column set is frozen - v.6's
/// analysis scripts read it positionally, and inserting anything would shift every column
/// after it while the file still looked the same. The servo's twelve fields go in their
/// own file, aligned to the main one by <c>time_min</c>, so both can be read row for row
/// and neither constrains the other.
/// </para>
/// <para>
/// <b>Empty for a missing reading, never zero.</b> Zero rpm and zero power are real
/// readings from a stopped motor; a zero written for absent data would be indistinguishable
/// from one, and no later reader could undo the confusion.
/// </para>
/// </remarks>
public static class ServoSessionLogFormat
{
    /// <summary>Appended to the main log's name to derive the sidecar's.</summary>
    public const string FileSuffix = "-servo-power.tsv";

    /// <summary>
    /// Bumped whenever a column is added, removed or redefined.
    /// </summary>
    /// <remarks>
    /// Written into the header so a reader can refuse a file it does not understand rather
    /// than misparsing one. The main log has no such marker and cannot gain one; this file
    /// starts with it.
    /// </remarks>
    public const int ContractVersion = 1;

    public const string Header =
        "time_min\tservo_online\trpm\ttorque_pct\ttorque_nm\tload_pct\t" +
        "power_w\tenergy_wh\tstate\talarm\tcomm_ok\tcomm_err";

    /// <summary>Comment lines opening the file, so its numbers stay interpretable later.</summary>
    /// <remarks>
    /// <para>
    /// The Hub firmware build matters more than it looks: 9.1.0-dev inverts the CN1's
    /// calibration before commanding the module, so the same setpoint produces a different
    /// shaft speed than it did under 9.0.0-dev. Nothing else in either file would say which
    /// firmware a run was recorded under.
    /// </para>
    /// <para>
    /// The motor's rated torque is deliberately <b>not</b> asserted here. It is a firmware
    /// constant the app never receives, and writing a guess would be worse than writing
    /// nothing. It is recoverable from the file itself:
    /// <c>T_nominal = torque_nm ÷ (torque_pct ÷ 100)</c> on any row where torque is not
    /// zero, which is why both columns are kept even though one derives from the other.
    /// </para>
    /// </remarks>
    public static string BuildPreamble(
        string? hubFirmware,
        int hubProtocol,
        string appVersion,
        IReadOnlyDictionary<string, Communication.ExternalNodeProvenance>? nodes = null,
        GasRigSettings? gasRig = null)
        => string.Join(
            "\n",
            $"# opentec-servo-power v{ContractVersion.ToString(CultureInfo.InvariantCulture)}",
            $"# app: {appVersion}",
            $"# hub_firmware: {(string.IsNullOrWhiteSpace(hubFirmware) ? "desconhecido" : hubFirmware)}",
            $"# hub_protocol: {(hubProtocol < 0 ? "desconhecido" : hubProtocol.ToString(CultureInfo.InvariantCulture))}",
            $"# nodes: {Communication.ExternalNodeProvenance.Describe(nodes)}",
            $"# gas_rig: {GasRigProvenance.Describe(gasRig)}",
            "# torque_nm deriva de torque_pct e do torque nominal do motor;",
            "# T_nominal = torque_nm / (torque_pct / 100) em qualquer linha com torque nao nulo.",
            "# potencia e energia sao MECANICAS ESTIMADAS no eixo, nao consumo eletrico.",
            "# energia e integrada no no: zera no reboot dele e na zeragem comandada.",
            "# celula vazia significa sem leitura. Zero e uma leitura.");
}

/// <summary>External C404 cascade telemetry sidecar, kept separate from the frozen v.6 log.</summary>
public static class BathSessionLogFormat
{
    public const string FileSuffix = "-bath-cascade.tsv";
    // v2 (Hub 10.7): cascade_fine and cascade_slope_c_min appended.
    public const int ContractVersion = 2;

    public const string Header =
        "time_min\ttempval\ttemp_filtered\tcascade_error\tcascade_p\tcascade_i\t" +
        "command_setpoint\tcommand_confirmed\tbath_pv\tbath_sp\tbath_target\t" +
        "bath_mode\tguard\tcascade_saturated\tcascade_state\tpaused_reason\troute\t" +
        "cascade_fine\tcascade_slope_c_min";

    public static string BuildPreamble(
        string? hubFirmware,
        int hubProtocol,
        string appVersion,
        IReadOnlyDictionary<string, Communication.ExternalNodeProvenance>? nodes = null)
        => string.Join(
            "\n",
            $"# opentec-bath-cascade v{ContractVersion.ToString(CultureInfo.InvariantCulture)}",
            $"# app: {appVersion}",
            $"# hub_firmware: {(string.IsNullOrWhiteSpace(hubFirmware) ? "desconhecido" : hubFirmware)}",
            $"# hub_protocol: {(hubProtocol < 0 ? "desconhecido" : hubProtocol.ToString(CultureInfo.InvariantCulture))}",
            $"# nodes: {Communication.ExternalNodeProvenance.Describe(nodes)}",
            "# tempval e a temperatura real do reator, lida pelo modulo via UART do Hub.",
            "# celula vazia significa sem telemetria; zero e uma leitura.");

    public static string BuildRow(SensorSnapshot s)
    {
        var bath = s.HasBathTelemetry;
        var fields = new[]
        {
            Number(s.TimeMinutes, 2), bath ? Number(s.Temperature, 2) : "",
            bath ? Number(s.BathCascadePvFiltered, 2) : "",
            bath ? Number(s.BathCascadeError, 3) : "", bath ? Number(s.BathCascadeP, 3) : "",
            bath ? Number(s.BathCascadeI, 3) : "", bath ? Number(s.BathCommandSetpoint, 3) : "",
            bath ? Number(s.BathCommandConfirmed, 3) : "", bath ? Number(s.BathPv, 2) : "",
            bath ? Number(s.BathSp, 2) : "", bath ? Number(s.BathTarget, 2) : "",
            bath && s.BathMode is { } mode ? mode.ToString(CultureInfo.InvariantCulture) : "",
            bath ? Cell(s.BathGuard) : "", bath ? (s.BathCascadeSaturated ? "1" : "0") : "",
            bath ? Cell(s.BathCascadeState) : "", bath ? Cell(s.BathCascadePausedReason) : "",
            bath && s.TempControlViaBath is { } routed ? (routed ? "external" : "uart") : "",
            bath && s.BathCascadeFine is { } fine ? (fine ? "1" : "0") : "",
            bath ? Number(s.BathCascadeSlopeCMin, 3) : "",
        };
        return string.Join('\t', fields);
    }

    private static string Number(double? value, int decimals)
        => value is { } number && double.IsFinite(number)
            ? number.ToString($"F{decimals}", CultureInfo.InvariantCulture)
            : "";

    private static string Cell(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>Where the application keeps its files.</summary>
public static class AppPaths
{
    private const string AppDataFolderName = "OpenTEC-Hub";
    private const string WorkspaceConfigFileName = "workspace.txt";

    private static string? _customDataDirectory;

    public static string BootstrapDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName);

    public static string WorkspaceConfigFile =>
        Path.Combine(BootstrapDirectory, WorkspaceConfigFileName);

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppDataFolderName);

    /// <summary>Per-user application data directory (workspace root), created on demand.</summary>
    public static string DataDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customDataDirectory))
            {
                Directory.CreateDirectory(_customDataDirectory);
                return _customDataDirectory;
            }

            var configured = ReadConfiguredWorkspace();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _customDataDirectory = configured;
                Directory.CreateDirectory(_customDataDirectory);
                return _customDataDirectory;
            }

            _customDataDirectory = DefaultDataDirectory;
            Directory.CreateDirectory(_customDataDirectory);
            return _customDataDirectory;
        }
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _customDataDirectory = value;
                Directory.CreateDirectory(_customDataDirectory);
                SaveConfiguredWorkspace(value);
            }
        }
    }

    public static string ConfigDirectory => Path.Combine(DataDirectory, "Configuracoes");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Operator-created kLa experiments and published profiles.</summary>
    public static string KlaMappingDirectory => Path.Combine(DataDirectory, "Mapas");

    /// <summary>Gassing-out kLa determination campaigns, raw series and analyses.</summary>
    public static string KlaTestsDirectory => Path.Combine(DataDirectory, "Testes-kLa");

    /// <summary>Impeller power assays (ungassed Np, gassed P_G/flooding), self-contained per test.</summary>
    public static string PowerTestsDirectory => Path.Combine(DataDirectory, "Testes-Potencia");

    /// <summary>Power maps, 2D surfaces, multi-impeller benchmarks and kLa-P/V correlations.</summary>
    public static string PowerMapsDirectory => Path.Combine(DataDirectory, "Mapas-Potencia");

    /// <summary>Operator-authored recipes, saved as versioned JSON.</summary>
    public static string RecipesDirectory => Path.Combine(DataDirectory, "Receitas");

    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public static string SessionsDirectory => Path.Combine(DataDirectory, "Sessoes");

    public static string BackupsDirectory => Path.Combine(DataDirectory, "Backups");

    /// <summary>Peristaltic pump and sensor calibration receipts, saved as JSON.</summary>
    public static string CalibrationsDirectory => Path.Combine(DataDirectory, "Calibracoes");

    /// <summary>External peristaltic pump hose calibration profiles directory.</summary>
    public static string PumpProfilesDirectory => Path.Combine(CalibrationsDirectory, "BombaExterna", "Perfis");

    public static void InitializeWorkspace(string workspacePath, bool persist = true)
    {
        _customDataDirectory = workspacePath;
        Directory.CreateDirectory(_customDataDirectory);
        if (persist)
        {
            SaveConfiguredWorkspace(workspacePath);
        }
        EnsureDirectories();
    }

    public static IDisposable OverrideForTests(string tempWorkspacePath)
    {
        var previous = _customDataDirectory;
        InitializeWorkspace(tempWorkspacePath, persist: false);
        return new TestWorkspaceScope(previous);
    }

    private sealed class TestWorkspaceScope(string? previous) : IDisposable
    {
        public void Dispose()
        {
            _customDataDirectory = previous;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(KlaMappingDirectory);
        Directory.CreateDirectory(KlaTestsDirectory);
        Directory.CreateDirectory(PowerTestsDirectory);
        Directory.CreateDirectory(PowerMapsDirectory);
        Directory.CreateDirectory(RecipesDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CalibrationsDirectory);
        Directory.CreateDirectory(PumpProfilesDirectory);
    }

    public static string? ReadConfiguredWorkspace()
    {
        try
        {
            if (File.Exists(WorkspaceConfigFile))
            {
                var path = File.ReadAllText(WorkspaceConfigFile).Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }
        catch
        {
            // Ignore bootstrap load failure
        }
        return null;
    }

    public static void SaveConfiguredWorkspace(string workspacePath)
    {
        try
        {
            Directory.CreateDirectory(BootstrapDirectory);
            File.WriteAllText(WorkspaceConfigFile, workspacePath);
        }
        catch
        {
            // Ignore bootstrap write failure
        }
    }

    /// <summary>
    /// Formats a session file name ensuring it incorporates a timestamp suffix (_yyyy-MM-dd_HHmm)
    /// for clear chronological ordering.
    /// </summary>
    public static string FormatSessionFileName(string? rawName, DateTime? timestamp = null)
    {
        var time = timestamp ?? DateTime.Now;
        var timeSuffix = time.ToString("yyyy-MM-dd_HHmm");

        if (string.IsNullOrWhiteSpace(rawName))
        {
            return $"Ensaio_{timeSuffix}.txt";
        }

        var clean = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4];
        }
        else if (clean.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4];
        }

        if (string.IsNullOrWhiteSpace(clean))
        {
            return $"Ensaio_{timeSuffix}.txt";
        }

        if (Regex.IsMatch(clean, @"\d{4}-\d{2}-\d{2}_\d{4}"))
        {
            return $"{clean}.txt";
        }

        return $"{clean}_{timeSuffix}.txt";
    }
}
