using OpenTECHub.Protocol;
namespace OpenTECHub.Simulator;

/// <summary>Fault-injection modes, switchable while running.</summary>
public enum Scenario
{
    /// <summary>Healthy process, every sensor reporting.</summary>
    Normal,

    /// <summary>Sensor module offline: probes at sentinel, <c>SensorCommOK:false</c>.</summary>
    NoModule,

    /// <summary>
    /// Telemetry frozen while the link stays up.
    /// </summary>
    /// <remarks>
    /// The failure the Wi-Fi silence timeout exists to catch: <c>/ping</c> keeps
    /// answering and <c>/readData</c> keeps returning 304, so the app would happily
    /// show stale readings behind a healthy indicator.
    /// </remarks>
    Stall,

    /// <summary>Link disappears entirely, to exercise reconnect.</summary>
    Dropout,

    /// <summary>Single-sample outliers, to prove the spike filter holds its value.</summary>
    Spikes,

    /// <summary>Heavy measurement noise on every channel.</summary>
    Noise,

    /// <summary>Slow calibration drift, for long-run chart behaviour.</summary>
    Drift,

    /// <summary>Malformed lines, to exercise the parse-failure path.</summary>
    Garbage,

    /// <summary>
    /// Every external Wi-Fi node stops answering the Hub while the Hub itself stays up.
    /// </summary>
    /// <remarks>
    /// The failure the presence keys exist to make visible. Without them the app cannot separate
    /// this from "the operator switched the device off" or "this Hub is too old to say", and the
    /// pump in particular used to have no staleness window at all — a dead node's last sample was
    /// republished forever.
    /// </remarks>
    NodeDropout,

    /// <summary>
    /// The Hub's persisted routing flags disagree with what the app last commanded.
    /// </summary>
    /// <remarks>
    /// What a Hub reboot produces: the Hub reloads its flags from NVS while the app reloads the
    /// operator's switches from disk. From then on the Hub drops that device's sub-commands with
    /// no reply at all, so nothing but the echo can notice.
    /// </remarks>
    RoutingDrift,

    /// <summary>
    /// A Hub from before the servo contract: not one <c>Servo*</c> key in the frame.
    /// </summary>
    /// <remarks>
    /// The app has to keep working against it, and - the part that is easy to get wrong -
    /// must render the servo as <i>awaiting telemetry</i> rather than <i>offline</i>. A Hub
    /// that has said nothing has not reported a failure, and an operator told a device
    /// failed will go looking for hardware that is fine.
    /// </remarks>
    LegacyHub,

    /// <summary>The drive raises <c>AL011</c>, encoder error, and the servo stops reporting motion.</summary>
    /// <remarks>
    /// The alarm the bench actually produced on 2026-09-02, by powering a drive with the
    /// motor disconnected. <c>ServoAlarm</c> carries <c>0x0011</c>, whose hex digits mirror
    /// the number on the drive's panel - reading it as decimal 17 finds nothing in the manual.
    /// </remarks>
    ServoAlarm,

    /// <summary>
    /// The Hub's DHCP hands every node a new address every twenty seconds of uptime.
    /// </summary>
    /// <remarks>
    /// What a node reboot or a Link Watchdog reassociation produces on the real SoftAP:
    /// the node comes back under a different <c>192.168.4.x</c>. Exercises the identity
    /// tracker and the Eventos entries without touching a board.
    /// </remarks>
    NodeRenumber,

    /// <summary>Bath node stops reporting while the Hub remains reachable.</summary>
    BathOffline,

    /// <summary>The bath reports an invalid C404 PV.</summary>
    BathPvInvalid,

    /// <summary>The outer loop reaches its configured output limit.</summary>
    BathSaturation,

    /// <summary>The bath enters a latched error state.</summary>
    BathError,

    /// <summary>The bath sequence is aborted by the operator.</summary>
    BathAbort,

    /// <summary>The bath guard suspends cascade control.</summary>
    BathGuardSuspended,

    /// <summary>
    /// The operator forgot the nitrogen at the source: B is on the same output as C, so every
    /// vent (C) opening also strips oxygen. What the power assay's N₂ guard exists to catch.
    /// </summary>
    NitrogenLeftOpen,
}

/// <summary>
/// The simulated bioreactor and its controller state.
/// </summary>
/// <remarks>
/// Supports realistic kLa surfaces (published mapping receipts), biological cultivation
/// kinetics with phase transitions, configurable polarographic probe dead time and quantisation,
/// and accelerated headless simulation.
/// </remarks>
public sealed class DeviceModel
{
    private readonly ISimulatorClock _clock;
    private readonly Random _random;
    private readonly DateTimeOffset _bootedAt;
    private readonly ServoPowerModelOptions _servoPowerModel;

    /// <summary>Delayed DO samples, so the reported value lags the true one.</summary>
    private readonly Queue<(DateTimeOffset At, double Value)> _oxygenDelayLine = new();

    private DateTimeOffset _lastTick;
    private double _elapsedSimulationSeconds;

    // ---- true process state ------------------------------------------

    private double _temperature = 24.0;      // degC, starts at ambient
    private double _bathTemperature = 30.0;  // C404 bath PV
    private double _flow;                    // L/min
    private double _oxygenTrue = 95.0;       // % saturation, starts near-saturated
    private double _oxygenReported = 95.0;   // what the probe says, i.e. delayed
    private double _ph = 7.0;
    private double _biomass = 0.15;          // AU
    private double _pressure;                // kPa
    private double _calibrationDrift;        // Drift scenario only

    public DeviceModel(
        ISimulatorClock? clock = null,
        IKlaSource? klaSource = null,
        CultivationProfile? profile = null,
        TimeSpan? probeDeadTime = null,
        double oxygenQuantisation = 0.0,
        int randomSeed = 20260819,
        ServoPowerModelOptions? servoPowerModel = null)
    {
        _clock = clock ?? new WallClock();
        _bootedAt = _clock.Now;
        _lastTick = _clock.Now;
        _random = new Random(randomSeed);
        _servoPowerModel = servoPowerModel ?? ServoPowerModelOptions.Default;
        ValidateServoPowerModel(_servoPowerModel);

        KlaSource = klaSource ?? new PowerLawKla();
        Profile = profile ?? CultivationProfile.Default;
        OxygenProbeDeadTime = probeDeadTime ?? TimeSpan.FromSeconds(25);
        OxygenQuantisation = Math.Max(0.0, oxygenQuantisation);
        CurrentPhase = Profile.GetPhase(0);
    }

    // ---- dynamic & scientific configuration ---------------------------

    public IKlaSource KlaSource { get; set; }

    public CultivationProfile Profile { get; set; }

    public TimeSpan OxygenProbeDeadTime { get; set; }

    public double OxygenQuantisation { get; set; }

    public double TrueOxygen => _oxygenTrue;

    public double ReportedOxygen => _oxygenReported;

    public double CurrentKLa { get; private set; }

    public double CurrentOur { get; private set; }

    public CultivationPhase CurrentPhase { get; private set; }

    public double ElapsedSimulationSeconds => _elapsedSimulationSeconds;

    // ---- commanded state ---------------------------------------------

    public Scenario Scenario { get; set; } = Scenario.Normal;

    public double TemperatureSetpoint { get; set; }

    /// <summary>True when the Hub routes the temperature loop to the external C404 bath.</summary>
    public bool TempControlViaBath { get; set; }
    public bool BathCommEnabled { get; set; }
    public bool BathModeAuto { get; set; } = true;
    public double BathCommandSetpoint { get; private set; } = 30.0;
    public double BathCommandConfirmed { get; private set; } = 30.0;
    public double BathSetpoint { get; internal set; } = 30.0;
    public double BathTarget { get; internal set; } = 30.0;
    public double BathPv => Scenario == Scenario.BathPvInvalid ? double.NaN : _bathTemperature;
    public double BathDeviation => BathPv - BathSetpoint;
    public string BathState { get; internal set; } = "idle";
    public string BathPhase { get; private set; } = "idle";
    public string BathError { get; internal set; } = "";
    public string BathGuard { get; private set; } = "off";
    public int BathSpSource { get; private set; } = 1;
    public bool BathCascadeSaturated { get; private set; }
    public string BathCascadeState { get; internal set; } = "off";
    public double BathCascadeError { get; private set; }
    public double BathCascadePvFiltered { get; private set; }
    public double BathCascadeP { get; private set; }
    public double BathCascadeI { get; private set; }
    public string BathCascadePausedReason { get; internal set; } = "";
    public long BathCommandId { get; private set; }
    public long BathCommandAck { get; private set; }
    public bool BathCommandPending { get; private set; }
    public bool BathCommandCompletionPending { get; private set; }
    public long BathCommandLastSentId { get; private set; }
    public long BathCommandLastDoneId { get; private set; }
    public int BathCommandCompletionAgeMs { get; private set; }
    public bool BathCommandLatestWins { get; private set; }
    public long BathCascadeLastUpdateMs { get; private set; }
    public double BathCascadeKp { get; set; } = 0.5;
    public double BathCascadeTiS { get; set; } = 600.0;
    public double BathCascadeBiasC { get; set; } = 0.6;
    public int BathCascadePeriodMs { get; set; } = 10000;
    public double BathCascadeFilterS { get; set; } = 20.0;
    public int BathCascadeCommandMinMs { get; set; } = 30000;
    public double BathCascadeCommandBandC { get; set; } = 0.1;
    public double BathCascadeSlewCMin { get; set; } = 0.5;
    public double BathCascadeOffsetHighC { get; set; } = 5.0;
    public double BathCascadeOffsetLowC { get; set; } = 5.0;
    public double BathCascadeOutputMinC { get; set; } = 5.0;
    public double BathCascadeOutputMaxC { get; set; } = 90.0;

    /// <summary>Commanded CN1 speed reference. <see cref="ServoRpm"/> is the measured response.</summary>
    public int MotorRpm { get; set; }

    /// <summary>True for direct Modbus; false for the original UART/CN1 path.</summary>
    public bool MotorControlViaModbus { get; set; } = true;

    public double OxygenSetpoint { get; set; }

    public double FlowSetpoint { get; set; }

    public double MaxFlow { get; set; } = 50.0;

    public double PressureReference { get; set; }

    public double PHSetpoint { get; set; }

    public double PHInactiveBand { get; set; } = 0.17;

    public int PHOperationSeconds { get; set; } = 5;

    public int PHMixSeconds { get; set; } = 20;

    /// <summary>Firmware-scale intensity, 0-990 (operator percent times ten).</summary>
    public double PHIntensity { get; set; }

    /// <summary>Last app-calibrated pH value echoed for the module display.</summary>
    public double PHDisplayValue { get; set; }

    public double? FlowK1 { get; set; }
    public double? FlowF1 { get; set; }
    public double? FlowC1 { get; set; }
    public double? FlowK2 { get; set; }
    public double? FlowF2 { get; set; }
    public double? FlowC2 { get; set; }

    /// <summary>The Hub's persisted flow-loop preference, republished as FlowControlEnabled.</summary>
    public bool FlowmeterEnabled { get; set; }

    /// <summary>The Hub's persisted routing flags for the three commandable external nodes.</summary>
    public bool BiomassEnabled { get; set; }

    public bool PumpEnabled { get; set; }

    public bool DistanceSensorEnabled { get; set; }
    public double DistanceOffsetMm { get; set; } = 20.0;
    public int DistanceSamplePeriodMs { get; set; } = 500;
    public int DistanceSendPeriodMs { get; set; } = 1000;
    private bool _distanceCommandPending;
    public bool DistanceCommandPending
    {
        get => _distanceCommandPending;
        set => _distanceCommandPending = value;
    }
    public bool ConsumeDistanceCommandPending()
    {
        var pending = _distanceCommandPending;
        _distanceCommandPending = false;
        return pending;
    }

    private bool _biomassCommandPending;

    /// <summary>One frame of <c>BiomassCommandPending:true</c> after each biomass command, like the Hub's mailbox.</summary>
    public bool BiomassCommandPending
    {
        get => _biomassCommandPending;
        set => _biomassCommandPending = value;
    }
    public bool ConsumeBiomassCommandPending()
    {
        var pending = _biomassCommandPending;
        _biomassCommandPending = false;
        return pending;
    }

    public double FlowKp { get; set; } = 0.8;
    public double FlowKi { get; set; } = 0.05;
    public double FlowFfGain { get; set; } = 0.0;
    public double FlowFfOffset { get; set; } = 0.0;
    public double FlowRampRate { get; set; } = 1.0;
    public double FlowOutput => Math.Round(ReadFlow() * 0.0109, 3);
    public double FlowSetpointCorrected => FlowSetpoint;
    public long FlowmeterBootId { get; set; } = 1001;
    public double FlowTransitionVoltage { get; set; } = 0.0545;

    public double PumpTransitionSpeed { get; set; } = 500.0;
    public double PumpDerivedTransitionFlow { get; set; } = 15.7696062934;
    public double PumpA1 { get; set; }
    public double PumpB1 { get; set; }
    public double PumpK1 { get; set; }
    public double PumpF1 { get; set; } = 0.0280188148;
    public double PumpC1 { get; set; } = 1.7601988934;
    public double PumpK2 { get; set; }
    public double PumpF2 { get; set; } = 0.0280188148;
    public double PumpC2 { get; set; } = 1.7601988934;
    public long PumpCalCrc { get; set; } = CalculatePumpCalibrationCrc(
        0, 0, 0, 0.0280188148, 1.7601988934, 0, 0.0280188148, 1.7601988934, 500.0);

    private bool _pumpCommandPending;
    public bool PumpCommandPending
    {
        get => _pumpCommandPending;
        set => _pumpCommandPending = value;
    }
    public bool ConsumePumpCommandPending()
    {
        var pending = _pumpCommandPending;
        _pumpCommandPending = false;
        return pending;
    }

    public static uint CalculatePumpCalibrationCrc(params double[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), (float)values[i]);
        }

        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }
        return ~crc;
    }

    public double PumpPidKp { get; set; } = 0.5;
    public double PumpPidKi { get; set; } = 0.05;
    public double PumpPidKd { get; set; } = 0.001;

    /// <summary>Bench potentiometers in command (3.10 echo). Cleared by <c>pump_speed</c>, restored by <c>pump_pot:1</c>.</summary>
    public bool PumpPotEnabled { get; set; } = true;
    public double PumpVolumeOffset { get; set; }
    public double PumpVolume => Math.Max(0.0, (UptimeSeconds * 1.25 / 60.0) - PumpVolumeOffset);
    public void ResetPumpVolume() => PumpVolumeOffset = UptimeSeconds * 1.25 / 60.0;

    public int BiomassIntegrationTimeMs { get; set; } = 100;
    public double BiomassPwmPercent { get; set; } = 2.0;
    public int BiomassGear { get; set; }
    public double BiomassEma { get; set; } = 0.8;
    public int BiomassProbePeriodMs { get; set; } = 25000;

    /// <summary>
    /// What the Hub reports for a routing flag, which is not always what was commanded.
    /// </summary>
    /// <remarks>
    /// Under <see cref="Scenario.RoutingDrift"/> the echo is inverted, reproducing the post-reboot
    /// divergence between the Hub's NVS and the app's own persisted switches.
    /// </remarks>
    public bool RoutingEcho(bool commanded)
        => Scenario == Scenario.RoutingDrift ? !commanded : commanded;

    /// <summary>True while the external Wi-Fi nodes are answering the Hub.</summary>
    public bool ExternalNodesOnline => Scenario != Scenario.NodeDropout;

    // ------------------------------------------------------------------
    // Node registry (Hub 10.1): who each external node is on the Hub's network
    // ------------------------------------------------------------------

    /// <summary>Wire names in the Hub's registry order, with the key prefix the frame uses.</summary>
    public static readonly (string Device, string Prefix)[] RegistryNodes =
    [
        ("distance", "Distance"),
        ("agitator", "Agitator"),
        ("pump", "Pump"),
        ("flowmeter", "Flowmeter"),
        ("biomass", "Biomass"),
    ];

    /// <summary>False under <see cref="Scenario.LegacyHub"/>: a Hub from before the identity keys.</summary>
    public bool PublishesNodeIdentity => Scenario != Scenario.LegacyHub;

    /// <summary>What the simulated Hub calls itself; the wire keys are absent under <see cref="Scenario.LegacyHub"/>.</summary>
    public const string HubFirmwareVersion = "10.2.0-dev";
    public const int HubProtocolVersion = 10;

    /// <summary>Enables every external-node route at once (bench dry runs).</summary>
    public void EnableAllExternalNodes()
    {
        FlowmeterEnabled = true;
        BiomassEnabled = true;
        PumpEnabled = true;
        DistanceSensorEnabled = true;
    }

    /// <summary>
    /// The node has sent its <c>/nodeHello</c>: it is answering the Hub and the operator has
    /// it enabled. The agitator and the flowmeter have no enable switch and register whenever present.
    /// </summary>
    public bool NodeRegistered(string device) => ExternalNodesOnline && device switch
    {
        "distance" => DistanceSensorEnabled,
        "pump" => PumpEnabled,
        "biomass" => BiomassEnabled,
        "bath" => BathNodeOnline,
        _ => true,
    };

    public bool BathNodeOnline => Scenario != Scenario.BathOffline && Scenario != Scenario.NodeDropout;

    /// <summary>The address the Hub's DHCP gave the node; <c>0.0.0.0</c> before it registered.</summary>
    /// <remarks>
    /// Deterministic: <c>192.168.4.2</c>…<c>.6</c> in registry order, shifted by ten every
    /// twenty seconds under <see cref="Scenario.NodeRenumber"/>.
    /// </remarks>
    public string NodeIp(string device)
    {
        if (!NodeRegistered(device))
        {
            return "0.0.0.0";
        }

        var index = Array.FindIndex(RegistryNodes, n => n.Device == device);
        var host = 2 + index;
        if (Scenario == Scenario.NodeRenumber)
        {
            host += 10 * ((int)(UptimeSeconds / 20.0) % 20);
        }

        return $"192.168.4.{host}";
    }

    /// <summary>Fixed per node, so a swapped board is something the app could notice.</summary>
    public static string NodeMac(string device)
        => $"AA:BB:CC:DD:EE:{2 + Array.FindIndex(RegistryNodes, n => n.Device == device):X2}";

    /// <summary>What each firmware sends as <c>ver=</c> in its <c>/nodeHello</c> today.</summary>
    public static string NodeVersion(string device) => device switch
    {
        "pump" => "3.9",
        "agitator" => "v10",
        "bath" => "r3.1",
        _ => "v11",
    };

    // ------------------------------------------------------------------
    // ASDA-B2 servo drive node
    // ------------------------------------------------------------------

    private const double TwoPiOverSixty = 0.10471975511965977;

    private double _servoEnergyJoules;
    private bool _servoWasPresent = true;
    private double _servoRpm;
    private double _servoTorquePercent;
    private double _servoTorqueNoisePercent;
    private double _bathCascadeElapsed;
    private double _bathCompletionElapsed;
    private double _bathIntegral;
    private long _bathLastUpdateMs;

    /// <summary>Servo routing on the Hub. The only routing flag that is born <c>true</c>.</summary>
    /// <remarks>
    /// Deliberate in the firmware: the node has to come up on its own when energised, without
    /// waiting for a command from the PC. A module that has no servo is told so once, and the
    /// Hub remembers it in NVS.
    /// </remarks>
    public bool ServoEnabled { get; set; } = true;

    /// <summary>False under <see cref="Scenario.LegacyHub"/>: no <c>Servo*</c> key at all.</summary>
    public bool PublishesServo => Scenario != Scenario.LegacyHub;

    /// <summary>The node is answering the Hub inside its 6 s window.</summary>
    public bool ServoNodePresent => PublishesServo && ExternalNodesOnline;

    /// <summary>
    /// The Hub emits the ten measurements only with fresh presence <b>and</b> routing on.
    /// </summary>
    public bool ServoSamplePublishable => ServoNodePresent && ServoEnabled;

    /// <summary>Measured shaft speed, in rpm.</summary>
    /// <remarks>
    /// Approaches the commanded reference with configurable first-order dynamics. The small
    /// residual is the scatter left by the Hub 9.1.0-dev inverse CN1 calibration.
    /// </remarks>
    public double ServoRpm => Scenario == Scenario.ServoAlarm
        ? 0.0
        : _servoRpm <= 0.01 ? 0.0 : Math.Max(0.0, _servoRpm + _servoResidualRpm);

    /// <summary>
    /// Torque as a percentage of rated, including stage tare, liquid load and measured noise.
    /// </summary>
    /// <remarks>
    /// The liquid component follows <c>P = rho·Np·N^3·D^5</c> per stage. Dividing by angular
    /// speed gives the stage torque; dry-running tare is added per stage. The reported sample
    /// then receives zero-mean noise interpolated from the 2026-09-03 bench curve.
    /// </remarks>
    public double ServoTorquePct => _servoTorquePercent + _servoTorqueNoisePercent;

    public double ServoTorqueNm => ServoTorquePct / 100.0 * _servoPowerModel.MotorRatedTorqueNm;

    /// <summary>Estimated mechanical shaft power, <c>T·ω</c>. Not electrical draw.</summary>
    public double ServoPowerW => ServoTorqueNm * ServoRpm * TwoPiOverSixty;

    /// <summary>Average load rate, whole percent, as P0-10 reports it.</summary>
    public double ServoLoadPct => Math.Round(ServoTorquePct);

    /// <summary>Mechanical energy integrated on the node, in watt-hours.</summary>
    public double ServoEnergyWh => _servoEnergyJoules / 3600.0;

    /// <summary>0 OFF, 1 READY, 2 SON, 3 ALARM.</summary>
    public int ServoState => Scenario == Scenario.ServoAlarm ? 3 : ServoRpm > 0.0 ? 2 : 1;

    /// <summary>Raw P0-01 code. <c>0x0011</c> is the panel's <c>AL011</c>.</summary>
    public int ServoAlarmCode => Scenario == Scenario.ServoAlarm ? 0x0011 : 0;

    /// <summary>Successful Modbus transactions, three per accepted sample.</summary>
    public long ServoCommOk { get; private set; }

    /// <summary>Failed Modbus samples.</summary>
    /// <remarks>
    /// Starts at one, not zero. The bench saw exactly one error in 256 reads, on the first
    /// transaction after boot, and an app that alarms on a non-zero total rather than on the
    /// rate would fire on a perfectly healthy link.
    /// </remarks>
    public long ServoCommErr { get; private set; } = 1;

    /// <summary>Sampling interval the node was last told to use, in milliseconds.</summary>
    public int ServoPollMs { get; private set; } = 1000;

    /// <summary>The Hub's fixed FIFO of eight, drained one per 2 s node pull.</summary>
    private readonly Queue<string> _servoCommands = new();

    public int ServoCommandQueueDepth => _servoCommands.Count;

    public bool ServoCommandPending => _servoCommands.Count > 0;

    public int ServoMotorRouteAck => MotorControlViaModbus ? 1 : 0;

    /// <summary>Queues a servo command, refusing the ninth without overwriting anything.</summary>
    /// <returns>False when the queue is full, which is back-pressure and not an error.</returns>
    public bool EnqueueServoCommand(string command)
    {
        if (_servoCommands.Count >= 8)
        {
            return false;
        }

        _servoCommands.Enqueue(command);
        return true;
    }

    /// <summary>Consume-on-read, exactly as <c>GET /servoCommand</c> behaves.</summary>
    public string? TakeServoCommand() => _servoCommands.Count > 0 ? _servoCommands.Dequeue() : null;

    /// <summary>Applies the reset the node performs when it collects the command.</summary>
    public void ResetServoEnergy() => _servoEnergyJoules = 0.0;

    /// <summary>Sets the node's sampling interval, refusing anything outside 250-10000 ms.</summary>
    public bool SetServoPollMs(int pollMs)
    {
        if (pollMs is < 250 or > 10000)
        {
            return false;
        }

        ServoPollMs = pollMs;
        return true;
    }

    private double _servoResidualRpm;

    /// <summary>The profile mode the pump node reports running; 0 is idle.</summary>
    public int PumpMode { get; set; }

    /// <summary>Internal speed S held by a <c>pump_speed</c> frame; 0 when no manual run is on.</summary>
    public double PumpManualSpeed { get; set; }

    /// <summary>
    /// The flow the simulated pump would deliver at <see cref="PumpManualSpeed"/>, on the
    /// firmware's shipped curve (slope 0.0280, intercept 1.7602). A calibration run that
    /// collects this many mL per minute reproduces those coefficients within rounding.
    /// </summary>
    public double PumpManualFlowMlMin => PumpManualSpeed > 0.0 ? (0.0280188148 * PumpManualSpeed) + 1.7601988934 : 0.0;

    /// <summary>What the flask-agitator node reports actually driving.</summary>
    public double AgitatorPercent { get; set; }

    public bool AgitatorClockwise { get; set; } = true;

    /// <summary>
    /// The bench potentiometer is live, so the knob outranks anything the app commanded.
    /// </summary>
    /// <remarks>
    /// Defaults on, matching the Hub's own persisted default. The operator safe-stop and the
    /// recipe's stop both clear it; nothing else does except the explicit re-enable.
    /// </remarks>
    public bool AgitatorPotActive { get; set; } = true;

    /// <summary>
    /// <c>v_Flow</c> as echoed (<c>ValveFlow</c>): the active-high main shutoff, 1 = line
    /// closed. It is not a vent — the vent is C, on one of the two valve outputs.
    /// </summary>
    public bool MainLineClosed { get; set; } = true;

    public int Valve1 { get; set; }

    public int Valve2 { get; set; }

    /// <summary>How A, B and C are wired to the two outputs. The physical document's default: A on 2, B/C on 1.</summary>
    public GasRigConfiguration GasRig { get; set; } = GasRigConfiguration.Default;

    /// <summary>
    /// The manual valve on the N₂ line upstream of B. Open by default — the kLa's normal
    /// state; a power assay expects it shut, and <see cref="Scenario.NitrogenLeftOpen"/> is the
    /// case where it was not.
    /// </summary>
    public bool NitrogenSourceOpen { get; set; } = true;

    /// <summary>What the two outputs and the setpoint amount to on this rig.</summary>
    public ObservedGasRoute ObservedRoute => GasRouting.Interpret(Valve1 == 1, Valve2 == 1, FlowSetpoint, GasRig);

    /// <summary>True when nitrogen is actually reaching the broth: B open and the source open.</summary>
    public bool NitrogenFlowing
        => ObservedRoute == ObservedGasRoute.VentAndNitrogen && (NitrogenSourceOpen || Scenario == Scenario.NitrogenLeftOpen);

    public int DataDelayMs { get; set; } = 2000;

    /// <summary>Command correlation counters the app surfaces in diagnostics.</summary>
    public long FlowCommandId { get; private set; }

    public long FlowCommandAck { get; private set; }

    public int FlowCommandDeliveries { get; private set; }

    public bool FlowCommandPending { get; set; }

    private double _flowAckTimer;
    private double _previousFlowSetpoint;
    private double _flowPulseTimer;
    private double _reactorHeadTimer;
    private ObservedGasRoute _previousRoute = ObservedGasRoute.Closed;

    /// <summary>True while the ESP32 can reach the sensor module over its internal UART.</summary>
    public bool SensorModuleOnline => Scenario != Scenario.NoModule;

    /// <summary>Seconds since simulated boot, as the device reports them.</summary>
    public double UptimeSeconds => (_clock.Now - _bootedAt).TotalSeconds;

    /// <summary>Records that a flow command was accepted, for ack correlation.</summary>
    public void NoteFlowCommand()
    {
        FlowCommandId++;
        FlowCommandDeliveries++;
        FlowCommandPending = true;
        _flowAckTimer = 0.15;
    }

    /// <summary>Advances the process by the given simulated time step or the elapsed clock time.</summary>
    public void Tick(double? explicitDt = null)
    {
        double dt;
        if (explicitDt is { } step && step > 0)
        {
            dt = step;
            _clock.Advance(TimeSpan.FromSeconds(dt));
            _lastTick = _clock.Now;
        }
        else
        {
            var now = _clock.Now;
            dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            dt = Math.Clamp(dt, 0.0, 5.0);
        }

        if (dt <= 0)
        {
            return;
        }

        _elapsedSimulationSeconds += dt;

        if (Scenario == Scenario.Stall)
        {
            return; // deliberately frozen: the link lives, the process does not
        }

        StepTemperature(dt);
        StepExternalBath(dt);
        StepFlowAndPressure(dt);
        StepOxygen(dt);
        StepPH(dt);
        StepBiomass(dt);
        StepServo(dt);

        if (Scenario == Scenario.Drift)
        {
            _calibrationDrift += dt * 0.0008;
        }
    }

    // ------------------------------------------------------------------
    // Dynamics
    // ------------------------------------------------------------------

    /// <summary>
    /// Advances the servo node: Modbus counters, and the energy integral it keeps locally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The integral lives on the node in the real system, because it needs the continuous
    /// 1 Hz series that the Hub's aggregate frame does not carry. Two consequences the app
    /// has to survive, and which are reproduced here rather than smoothed over: the total
    /// <b>zeroes when the node reboots</b>, and it <b>does not integrate across a gap</b> -
    /// energy that was never measured is not invented.
    /// </para>
    /// <para>
    /// A node that comes back after being absent is a node that rebooted, so its accumulator
    /// starts again from zero. That is the step backwards a chart has to draw without
    /// treating it as corruption.
    /// </para>
    /// </remarks>
    private void StepServo(double dt)
    {
        var present = ServoNodePresent;

        if (!present)
        {
            _servoWasPresent = false;
            return;
        }

        if (!_servoWasPresent)
        {
            _servoEnergyJoules = 0.0;
            ServoCommOk = 0;
            ServoCommErr = 1;
            _servoWasPresent = true;
        }

        var speedTarget = Scenario == Scenario.ServoAlarm ? 0.0 : Math.Max(0.0, MotorRpm);
        _servoRpm = FirstOrderStep(_servoRpm, speedTarget, dt, _servoPowerModel.SpeedTimeConstantSeconds);
        if (speedTarget == 0.0 && _servoRpm < 0.01)
        {
            _servoRpm = 0.0;
        }

        var reactorFlow = ReadReactorFlow();
        var torqueTargetPercent = CalculateSteadyServoTorquePercent(_servoRpm, reactorFlow);
        _servoTorquePercent = FirstOrderStep(
            _servoTorquePercent,
            torqueTargetPercent,
            dt,
            _servoPowerModel.TorqueTimeConstantSeconds);

        var sigmaTorquePercent = InterpolateTorqueNoiseSigma(_servoRpm);
        _servoTorqueNoisePercent = NextStandardNormal() * sigmaTorquePercent;

        // A small residual so the measurement is never exactly the speed state. The CN1
        // correction in Hub 9.1.0-dev removes the systematic part; what is left is the
        // scatter of the fit, which the bench put under half an rpm.
        _servoResidualRpm = (_random.NextDouble() - 0.5) * 0.9;

        // Three transactions per sample, at whatever interval the node was last told.
        ServoCommOk += (long)Math.Round(3.0 * dt * 1000.0 / ServoPollMs);

        _servoEnergyJoules += ServoPowerW * dt;

        DrainServoQueue(dt);
    }

    /// <summary>
    /// Air actually reaching the sparger: only on the reactor route. Vented gas (C), nitrogen (B)
    /// and a dead-ended line put nothing through the vessel.
    /// </summary>
    public double ReadReactorFlow() => ObservedRoute == ObservedGasRoute.Reactor ? _flow : 0.0;

    private double CalculateSteadyServoTorquePercent(double rpm, double flowLpm)
    {
        if (rpm <= 0.0)
        {
            return 0.0;
        }

        var revolutionsPerSecond = rpm / 60.0;
        var angularSpeed = rpm * TwoPiOverSixty;

        var torqueNm = 0.0;
        foreach (var stage in _servoPowerModel.Impellers)
        {
            double gasPowerRatio;
            if (flowLpm <= 0.0)
            {
                gasPowerRatio = 1.0;
            }
            else if (_servoPowerModel.SimulateFloodingKnee)
            {
                var d = stage.DiameterM;
                var fr = (revolutionsPerSecond * revolutionsPerSecond * d) / 9.80665;
                var flGF = 30.0 * Math.Pow(d / _servoPowerModel.VesselDiameterM, 3.5) * fr;
                var qM3S = flowLpm / 60000.0;
                var flG = qM3S / (revolutionsPerSecond * Math.Pow(d, 3));

                if (flGF > 0 && double.IsFinite(flGF) && flG >= 0)
                {
                    var x = flG / flGF;
                    if (x <= 1.0)
                    {
                        gasPowerRatio = 1.0 - (1.0 - _servoPowerModel.GassedLiquidPowerRatio) * (1.0 - Math.Exp(-2.5 * x)) / (1.0 - Math.Exp(-2.5));
                    }
                    else
                    {
                        gasPowerRatio = _servoPowerModel.GassedLiquidPowerRatio + 0.12 * (1.0 - Math.Exp(-1.8 * (x - 1.0)));
                    }
                }
                else
                {
                    var gasFraction = _servoPowerModel.ReferenceGasFlowLpm <= 0.0
                        ? 0.0
                        : Math.Clamp(flowLpm / _servoPowerModel.ReferenceGasFlowLpm, 0.0, 1.0);
                    gasPowerRatio = 1.0 - gasFraction * (1.0 - _servoPowerModel.GassedLiquidPowerRatio);
                }
            }
            else
            {
                var gasFraction = _servoPowerModel.ReferenceGasFlowLpm <= 0.0
                    ? 0.0
                    : Math.Clamp(flowLpm / _servoPowerModel.ReferenceGasFlowLpm, 0.0, 1.0);
                gasPowerRatio = 1.0 - gasFraction * (1.0 - _servoPowerModel.GassedLiquidPowerRatio);
            }

            var liquidPowerW = _servoPowerModel.LiquidDensityKgM3
                               * stage.PowerNumber
                               * Math.Pow(revolutionsPerSecond, 3.0)
                               * Math.Pow(stage.DiameterM, 5.0)
                               * gasPowerRatio;
            var tarePercent = stage.TareTorquePercentAtZero
                              + stage.TareTorquePercentPerRpm * rpm;

            torqueNm += liquidPowerW / angularSpeed;
            torqueNm += tarePercent / 100.0 * _servoPowerModel.MotorRatedTorqueNm;
        }

        return torqueNm / _servoPowerModel.MotorRatedTorqueNm * 100.0;
    }

    private double InterpolateTorqueNoiseSigma(double rpm)
    {
        var curve = _servoPowerModel.TorqueNoiseCurve;
        if (curve.Count == 0)
        {
            return 0.0;
        }

        var speed = Math.Abs(rpm);
        if (speed <= curve[0].Rpm)
        {
            return curve[0].SigmaTorquePercent;
        }

        for (var i = 1; i < curve.Count; i++)
        {
            if (speed <= curve[i].Rpm)
            {
                var lower = curve[i - 1];
                var upper = curve[i];
                var fraction = (speed - lower.Rpm) / (upper.Rpm - lower.Rpm);
                return lower.SigmaTorquePercent
                       + fraction * (upper.SigmaTorquePercent - lower.SigmaTorquePercent);
            }
        }

        return curve[^1].SigmaTorquePercent;
    }

    private double NextStandardNormal()
    {
        // Box-Muller transform. Keep u1 away from zero so log remains finite.
        var u1 = Math.Max(_random.NextDouble(), double.Epsilon);
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double FirstOrderStep(double current, double target, double dt, double tauSeconds)
    {
        if (tauSeconds <= 0.0)
        {
            return target;
        }

        var alpha = 1.0 - Math.Exp(-dt / tauSeconds);
        return current + (target - current) * alpha;
    }

    private static void ValidateServoPowerModel(ServoPowerModelOptions options)
    {
        if (options.LiquidDensityKgM3 <= 0.0 || options.MotorRatedTorqueNm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Density and rated torque must be positive.");
        }

        if (options.SpeedTimeConstantSeconds < 0.0 || options.TorqueTimeConstantSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Servo time constants cannot be negative.");
        }

        if (options.Impellers.Count == 0 || options.Impellers.Any(
                stage => stage.DiameterM <= 0.0 || stage.PowerNumber < 0.0))
        {
            throw new ArgumentException("At least one impeller with positive diameter and non-negative Np is required.", nameof(options));
        }

        if (options.TorqueNoiseCurve.Any(point => point.Rpm < 0.0 || point.SigmaTorquePercent < 0.0)
            || options.TorqueNoiseCurve.Zip(options.TorqueNoiseCurve.Skip(1), (a, b) => a.Rpm < b.Rpm).Any(inOrder => !inOrder))
        {
            throw new ArgumentException("Torque-noise points must be non-negative and strictly increasing in rpm.", nameof(options));
        }

        if (options.GassedLiquidPowerRatio is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The gassed liquid-power ratio must be between zero and one.");
        }
    }

    /// <summary>
    /// The node collecting one queued command per pull, and acting on it.
    /// </summary>
    /// <remarks>
    /// One event per <c>GET /servoCommand</c>, every two seconds, consume-on-read. It matters
    /// that this is slow: a full queue of eight takes sixteen seconds to empty, which is the
    /// floor before anything may be called failed. An app that gave up sooner would report a
    /// phantom failure every time an operator clicked twice.
    /// </remarks>
    private void DrainServoQueue(double dt)
    {
        _servoPullTimer += dt;
        if (_servoPullTimer < 2.0)
        {
            return;
        }

        _servoPullTimer = 0.0;

        if (TakeServoCommand() == "reset_energy")
        {
            _servoEnergyJoules = 0.0;
        }
    }

    private double _servoPullTimer;

    private void StepTemperature(double dt)
    {
        const double ambient = 24.0;
        const double heatingTau = 90.0;
        const double lossTau = 600.0;

        var target = TempControlViaBath ? _bathTemperature : TemperatureSetpoint;
        if (target > 0)
        {
            _temperature += (target - _temperature) * Math.Clamp(dt / heatingTau, 0.0, 1.0);
        }

        _temperature += (ambient - _temperature) * (dt / lossTau);
    }

    private void StepExternalBath(double dt)
    {
        var nowMs = (long)(UptimeSeconds * 1000.0);
        _bathLastUpdateMs = nowMs;

        if (!TempControlViaBath || !BathCommEnabled)
        {
            BathCascadeState = "off";
            BathState = "idle";
            BathPhase = "idle";
            BathGuard = "off";
            BathCascadePausedReason = "";
            BathCommandPending = false;
            BathCommandCompletionPending = false;
            BathCascadeSaturated = false;
            return;
        }

        if (Scenario == Scenario.BathError)
        {
            BathState = "error";
            BathError = "bath_fault";
            BathCascadeState = "fault";
            BathCascadePausedReason = "bath_fault";
            return;
        }
        if (Scenario == Scenario.BathAbort)
        {
            BathState = "aborted";
            BathError = "aborted";
            BathCascadeState = "fault";
            BathCascadePausedReason = "aborted";
            return;
        }
        if (Scenario == Scenario.BathGuardSuspended)
        {
            BathGuard = "suspended";
            BathCascadeState = "paused";
            BathCascadePausedReason = "bath_guard";
            return;
        }
        if (!BathNodeOnline)
        {
            BathCascadeState = "paused";
            BathCascadePausedReason = "bath_offline";
            BathState = "idle";
            BathPhase = "idle";
            return;
        }

        BathGuard = "ok";
        BathError = "";
        BathCascadeState = "controlling";
        BathPhase = "running";
        BathState = BathCommandCompletionPending ? "running" : "done";

        _bathCascadeElapsed += dt * 1000.0;
        var pv = BathPv;
        if (double.IsNaN(pv))
        {
            BathCascadeState = "paused";
            BathCascadePausedReason = "reactor_pv_invalid";
            return;
        }

        // Outer PI: Tempval is the real reactor PV; only BathCommandSetpoint is sent to C404.
        var reference = TemperatureSetpoint > 0.0 ? TemperatureSetpoint : BathSetpoint;
        var error = reference - _temperature;
        BathCascadeError = error;
        BathCascadePvFiltered = BathCascadePvFiltered == 0.0
            ? _temperature
            : FirstOrderStep(BathCascadePvFiltered, _temperature, dt, BathCascadeFilterS);
        BathCascadeP = BathCascadeKp * error;
        _bathIntegral += BathCascadeKp * error * dt / Math.Max(BathCascadeTiS, 1.0);
        _bathIntegral = Math.Clamp(_bathIntegral, -20.0, 20.0);
        BathCascadeI = _bathIntegral;

        var desired = reference + BathCascadeBiasC + BathCascadeP + BathCascadeI;
        var lower = reference - BathCascadeOffsetLowC;
        var upper = reference + BathCascadeOffsetHighC;
        var clamped = Math.Clamp(desired, Math.Max(BathCascadeOutputMinC, lower),
            Math.Min(BathCascadeOutputMaxC, upper));
        BathCascadeSaturated = Scenario == Scenario.BathSaturation || Math.Abs(clamped - desired) > 1e-6;
        if (BathCascadeSaturated)
        {
            BathCascadeState = "paused";
            BathCascadePausedReason = "saturated";
        }

        if (_bathCascadeElapsed >= BathCascadePeriodMs &&
            !BathCommandCompletionPending &&
            Math.Abs(clamped - BathCommandSetpoint) >= BathCascadeCommandBandC)
        {
            _bathCascadeElapsed = 0.0;
            BathCommandSetpoint = Math.Clamp(clamped, BathCascadeOutputMinC, BathCascadeOutputMaxC);
            BathTarget = BathCommandSetpoint;
            BathCommandId++;
            BathCommandLastSentId = BathCommandId;
            BathCommandPending = true;
            BathCommandCompletionPending = true;
            _bathCompletionElapsed = 0.0;
            BathCommandLatestWins = false;
        }

        // C404 internal loop and mailbox ACK/done are deterministic and do not use network time.
        _bathTemperature += (BathCommandSetpoint - _bathTemperature) * Math.Clamp(dt / 45.0, 0.0, 1.0);
        if (BathCommandCompletionPending)
        {
            _bathCompletionElapsed += dt;
            if (_bathCompletionElapsed >= 2.0)
            {
                BathCommandPending = false;
                BathCommandAck = BathCommandLastSentId;
                BathCommandLastDoneId = BathCommandLastSentId;
                BathCommandCompletionPending = false;
                BathCommandCompletionAgeMs = 0;
                BathCommandConfirmed = BathCommandSetpoint;
                BathState = "done";
            }
            else
            {
                BathCommandCompletionAgeMs = (int)(_bathCompletionElapsed * 1000.0);
            }
        }
    }

    private void StepFlowAndPressure(double dt)
    {
        const double flowTau = 3.0;

        if (_flowAckTimer > 0)
        {
            _flowAckTimer -= dt;
            if (_flowAckTimer <= 0)
            {
                FlowCommandAck = FlowCommandId;
                FlowCommandPending = false;
            }
        }

        if (FlowSetpoint > 0 && _previousFlowSetpoint <= 0)
        {
            _flowPulseTimer = _servoPowerModel.VentFlowPulseDurationSeconds;
            _previousFlowSetpoint = FlowSetpoint;
        }
        else if (FlowSetpoint <= 0)
        {
            _flowPulseTimer = 0.0;
            _previousFlowSetpoint = 0.0;
        }

        if (_flowPulseTimer > 0)
        {
            _flowPulseTimer = Math.Max(0.0, _flowPulseTimer - dt);
        }

        // Switching into the reactor adds the sparger's head: the controller dips and recovers.
        var route = ObservedRoute;
        if (route == ObservedGasRoute.Reactor && _previousRoute != ObservedGasRoute.Reactor && FlowSetpoint > 0)
        {
            _reactorHeadTimer = _servoPowerModel.ReactorHeadStepDurationSeconds;
        }
        _previousRoute = route;
        if (_reactorHeadTimer > 0)
        {
            _reactorHeadTimer = Math.Max(0.0, _reactorHeadTimer - dt);
        }

        // A dead-ended line (setpoint above zero, both outputs shut) passes nothing: the meter
        // reads ~0 while the head-space pressure climbs. The observable failure of the rig.
        var deadEnd = route == ObservedGasRoute.DeadEnd;
        var target = deadEnd ? 0.0 : Math.Clamp(FlowSetpoint, 0.0, MaxFlow);
        _flow += (target - _flow) * (dt / flowTau);

        double targetPressure;
        if (deadEnd)
        {
            targetPressure = _servoPowerModel.DeadEndPressureKpa;
        }
        else
        {
            // The main shutoff (v_Flow) is the line's restriction; the sparger adds a little on A.
            var restriction = MainLineClosed ? 1.6 : route == ObservedGasRoute.Reactor ? 0.45 : 0.35;
            targetPressure = _flow * restriction * 2.0;
        }
        _pressure += (targetPressure - _pressure) * (dt / (deadEnd ? 4.0 : 8.0));
    }

    private void StepOxygen(double dt)
    {
        const double saturation = 100.0;

        CurrentPhase = Profile.GetPhase(_elapsedSimulationSeconds);

        // The rig decides what the broth sees. Air transfers oxygen only on the reactor route
        // (A); on B/C the vented air does nothing and, if the source is open, nitrogen strips
        // the broth towards zero at the kLa the same sparger would give that gas flow; on a
        // closed or dead-ended line only surface aeration at flow 0 remains.
        var reactorFlow = ReadReactorFlow();
        var kLa = KlaSource.Evaluate(MotorRpm, reactorFlow);
        CurrentKLa = kLa;

        var uptake = _biomass * CurrentPhase.SpecificOurPerAu;
        CurrentOur = uptake;

        var transfer = kLa * (saturation - _oxygenTrue);
        if (NitrogenFlowing)
        {
            // Stripping: the gas phase carries no oxygen, so the driving force is −C.
            transfer -= KlaSource.Evaluate(MotorRpm, _servoPowerModel.NitrogenFlowLpm) * _oxygenTrue;
        }

        _oxygenTrue += (transfer - uptake) * dt;
        _oxygenTrue = Math.Clamp(_oxygenTrue, 0.0, saturation);

        // Feed the delay line and read out whatever is old enough to have arrived.
        var now = _clock.Now;
        _oxygenDelayLine.Enqueue((now, _oxygenTrue));

        while (_oxygenDelayLine.Count > 0 &&
               now - _oxygenDelayLine.Peek().At >= OxygenProbeDeadTime)
        {
            _oxygenReported = _oxygenDelayLine.Dequeue().Value;
        }

        // Cap the queue in case the dead time is ever set absurdly high.
        while (_oxygenDelayLine.Count > 20_000)
        {
            _oxygenDelayLine.Dequeue();
        }
    }

    private void StepPH(double dt)
    {
        // Metabolism acidifies according to active cultivation phase; dosing corrects.
        var phase = CurrentPhase ?? Profile.GetPhase(_elapsedSimulationSeconds);
        _ph -= _biomass * phase.AcidificationRatePerAu * dt;

        if (PHSetpoint > 0 && PHIntensity > 0)
        {
            var error = PHSetpoint - _ph;
            if (Math.Abs(error) > PHInactiveBand)
            {
                var speedFraction = Math.Clamp(PHIntensity / 990.0, 0.0, 1.0);
                var dutyFraction = PHOperationSeconds /
                                   (double)Math.Max(PHOperationSeconds + PHMixSeconds, 1);
                var dosingGain = Math.Max(speedFraction * dutyFraction, 0.01);
                _ph += error * (dt / (240.0 / dosingGain));
            }
        }

        _ph = Math.Clamp(_ph, 3.0, 11.0);
    }

    private void StepBiomass(double dt)
    {
        var phase = CurrentPhase ?? Profile.GetPhase(_elapsedSimulationSeconds);

        // Growth stalls when oxygen runs out, which is the whole point of controlling it.
        var oxygenLimitation = Math.Clamp(_oxygenTrue / 25.0, 0.0, 1.0);

        if (phase.SpecificGrowthRatePerSecond > 0 && phase.CarryingCapacityAu > 0)
        {
            _biomass += phase.SpecificGrowthRatePerSecond * _biomass * (1.0 - (_biomass / phase.CarryingCapacityAu))
                        * oxygenLimitation * dt;
        }
    }

    // ------------------------------------------------------------------
    // Readings, with scenario effects applied
    // ------------------------------------------------------------------

    public double ReadTemperature() => Perturb(_temperature, 0.05);

    public double ReadOxygenPercent()
    {
        var value = _oxygenReported + _calibrationDrift;
        if (OxygenQuantisation > 0.0)
        {
            value = Math.Round(value / OxygenQuantisation) * OxygenQuantisation;
        }

        return Math.Clamp(Perturb(value, 0.4), 0.0, 100.0);
    }

    public double ReadPH() => Perturb(_ph + (_calibrationDrift * 0.05), 0.01);

    public double ReadFlow()
    {
        var overshoot = 0.0;
        if (_flowPulseTimer > 0 && _servoPowerModel.VentFlowPulseDurationSeconds > 0 && FlowSetpoint > 0
            && ObservedRoute != ObservedGasRoute.DeadEnd)
        {
            var fraction = _flowPulseTimer / _servoPowerModel.VentFlowPulseDurationSeconds;
            overshoot = fraction * (FlowSetpoint + _servoPowerModel.VentFlowPulseMagnitude);
        }

        var headDip = 0.0;
        if (_reactorHeadTimer > 0 && _servoPowerModel.ReactorHeadStepDurationSeconds > 0)
        {
            var fraction = _reactorHeadTimer / _servoPowerModel.ReactorHeadStepDurationSeconds;
            headDip = fraction * _servoPowerModel.ReactorHeadStepFraction * FlowSetpoint;
        }

        return Math.Max(0.0, Perturb(_flow + overshoot - headDip, 0.02));
    }

    public double ReadPressure() => Math.Max(0.0, Perturb(_pressure, 0.1));

    public double ReadBiomass() => Math.Max(0.0, Perturb(_biomass, 0.005));

    /// <summary>Applies scenario noise and the occasional injected outlier.</summary>
    private double Perturb(double value, double noiseScale)
    {
        var noise = Scenario switch
        {
            Scenario.Noise => noiseScale * 8.0,
            Scenario.Spikes => noiseScale,
            _ => noiseScale,
        };

        var result = value + ((_random.NextDouble() - 0.5) * 2.0 * noise);

        // A lone outlier the app's spike filter must hold against rather than follow.
        if (Scenario == Scenario.Spikes && _random.NextDouble() < 0.06)
        {
            result += (_random.NextDouble() - 0.5) * Math.Max(Math.Abs(value), 1.0) * 1.4;
        }

        return result;
    }
}
