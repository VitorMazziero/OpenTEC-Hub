namespace TecnalHub.Protocol;

/// <summary>
/// Builders for commands whose correct shape is a protocol rule rather than a
/// caller's choice.
/// </summary>
/// <remarks>
/// Anything here exists because getting it wrong by hand is easy and the
    /// consequence is on real hardware - an inverted shutoff flag, or a nitrogen valve
/// left open through a stop. Callers should reach for these rather than assembling
/// the keys themselves. See <c>docs/PROTOCOL.md</c> section 3.1.
/// </remarks>
public static class CommandBuilders
{
    /// <summary>Handshake probe. Same object on both transports; only framing differs.</summary>
    public static TecnalCommand Handshake()
        => TecnalCommand.Create().Set(CommandKeys.ComTest, 1);

    /// <summary>
    /// Enables flow control at <paramref name="setpoint"/> L/min.
    /// </summary>
    /// <remarks>
    /// <c>v_Flow</c> is the active-high main shutoff: 1 closes the gas path. A zero
    /// setpoint always closes it; it can also close while preserving a nonzero setpoint.
    /// </remarks>
    /// <param name="setpoint">Target flow, clamped to <paramref name="maxFlow"/>.</param>
    /// <param name="maxFlow">Ceiling reported to the firmware.</param>
    /// <param name="valve1">Auxiliary valve state.</param>
    /// <param name="valve2">Nitrogen valve state.</param>
    /// <param name="mainValveClosed">Close the main gas path without changing setpoint.</param>
    public static TecnalCommand FlowSetpoint(
        double setpoint,
        double maxFlow,
        bool valve1 = false,
        bool valve2 = false,
        bool mainValveClosed = false)
    {
        var clamped = Math.Clamp(setpoint, 0.0, maxFlow);

        return TecnalCommand.Create()
            .Set(CommandKeys.FlowSetpoint, clamped)
            .Set(CommandKeys.MaxFlow, maxFlow)
            .Set(CommandKeys.Valve1, valve1)
            .Set(CommandKeys.Valve2, valve2)
            .Set(CommandKeys.V_Flow, mainValveClosed || clamped == 0.0);
    }

    /// <summary>
    /// Records the flow loop as enabled or disabled on the Hub.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>flowmeterComm</c> never reaches the v05 — the Hub builds and delivers the flow
    /// mailbox from <c>flowSetpoint</c>/valves regardless of it, which is why it is not part
    /// of <see cref="FlowSetpoint"/>. It is not, however, unused: the Hub v7 parses it, keeps
    /// it in the <c>flowComm</c> preference across reboots, folds it into its settings hash and
    /// publishes it back as <c>FlowControlEnabled</c>. That flag is the Hub's record of whether
    /// the loop is on, and the only thing that writes it.
    /// </para>
    /// <para>
    /// So it is sent where the loop is actually switched — the Vazão de Ar enable and the
    /// recipe's aeration-loop block — and never bundled into a setpoint frame. Leaving it
    /// unwritten silently desynchronises the Hub from the app and disables the
    /// <c>Fluxômetro offline</c> alarm, whose condition needs <c>FlowControlEnabled</c>.
    /// </para>
    /// </remarks>
    public static TecnalCommand FlowmeterLoopEnabled(bool enabled)
        => TecnalCommand.Create().Set(CommandKeys.FlowmeterComm, enabled ? 1 : 0);

    /// <summary>
    /// Disables the flow subsystem and closes everything.
    /// </summary>
    /// <remarks>
    /// <b>Both valves are forced closed</b> rather than preserving the operator's
    /// manual selection. v.6 does this deliberately: leaving a nitrogen valve open
    /// through a safe-stop is a hazard. Preserve this behaviour.
    /// </remarks>
    public static TecnalCommand FlowSafeStop(double maxFlow)
        => TecnalCommand.Create()
            .Set(CommandKeys.FlowSetpoint, 0.0)
            .Set(CommandKeys.MaxFlow, maxFlow)
            .Set(CommandKeys.Valve1, false)
            .Set(CommandKeys.Valve2, false)
            .Set(CommandKeys.V_Flow, true);

    /// <summary>
    /// Safely disables every subsystem in the Phase 1 core loop in one command.
    /// </summary>
    /// <remarks>
    /// Flow uses the full safe-stop shape rather than a zero setpoint alone, so both
    /// gas valves close and the inverted vent flag is asserted. Keeping the whole stop
    /// here makes the destructive-confirmation preview byte-identical to what is sent.
    /// </remarks>
    public static TecnalCommand CoreSafeStop(double maxFlow)
        => TecnalCommand.Create()
            .Set(CommandKeys.TempSetpoint, 0.0)
            .Set(CommandKeys.MotorSetpoint, 0)
            .Set(CommandKeys.OxygenMonitor, 0.0)
            .Merge(FlowSafeStop(maxFlow))
            .Set(CommandKeys.PressureReference, 0.0);

    /// <summary>
    /// Motor setpoint in rpm. Valid range is 50-1000; <c>0</c> stops the motor.
    /// </summary>
    /// <remarks>Always an integer on the wire.</remarks>
    public static TecnalCommand MotorSetpoint(int rpm)
    {
        if (rpm != 0)
        {
            rpm = Math.Clamp(rpm, 50, 1000);
        }

        return TecnalCommand.Create().Set(CommandKeys.MotorSetpoint, rpm);
    }

    /// <summary>Calibrated pH echoed back to the device as a 2-decimal quoted string.</summary>
    /// <remarks>See <c>docs/PROTOCOL.md</c> section 2.2.</remarks>
    public static TecnalCommand PHCalibration(double calibratedPH)
        => TecnalCommand.Create().SetFixedString(CommandKeys.PHCal, calibratedPH, 2);

    /// <summary>
    /// Complete pH desired state used by v.6: reference, inactive band, pump timing
    /// and pump speed travel in one frame.
    /// </summary>
    /// <remarks>
    /// The operator-facing speed remains 0-99 percent, while the sensor module expects
    /// that value multiplied by ten. Operation and mixing are written as floating-point
    /// JSON values because v.6 parsed those text fields with <c>float()</c> before
    /// serialising them, even though the firmware ultimately stores integers.
    /// </remarks>
    public static TecnalCommand PHControl(
        double setpoint,
        double inactiveBand,
        int operationSeconds,
        int mixSeconds,
        double pumpSpeedPercent)
        => TecnalCommand.Create()
            .Set(CommandKeys.PHSetpoint, setpoint)
            .Set(CommandKeys.PHError, inactiveBand)
            .Set(CommandKeys.PHOperation, (double)operationSeconds)
            .Set(CommandKeys.PHMix, (double)mixSeconds)
            .Set(CommandKeys.PHIntensity, pumpSpeedPercent * 10.0);

    /// <summary>
    /// Disables pH dosing without discarding the last valid timing and inactive band.
    /// </summary>
    public static TecnalCommand PHControlSafeStop(
        double inactiveBand,
        int operationSeconds,
        int mixSeconds)
        => PHControl(0.0, inactiveBand, operationSeconds, mixSeconds, 0.0);

    /// <summary>
    /// Complete nutrient dosing state: operation/mix timing, the two cycle counts and
    /// pump intensity, in one atomic frame.
    /// </summary>
    /// <remarks>
    /// Timing and cycle counts travel as floating-point JSON values, matching v.6's
    /// <c>float()</c> handling of those text fields. <b>Intensity is the raw operator
    /// percent (0-99)</b> — it is <i>not</i> multiplied by ten. Only pH carries the
    /// <c>× 10</c> quirk (see <see cref="PHControl"/> and <c>docs/PROTOCOL.md</c> §3.3).
    /// </remarks>
    public static TecnalCommand NutrientControl(
        int operationSeconds,
        int mixSeconds,
        int operationCycles,
        int mixCycles,
        double pumpSpeedPercent)
        => TecnalCommand.Create()
            .Set(CommandKeys.NutriOperation, (double)operationSeconds)
            .Set(CommandKeys.NutriMix, (double)mixSeconds)
            .Set(CommandKeys.NutriOpCycle, (double)operationCycles)
            .Set(CommandKeys.NutriMixCycle, (double)mixCycles)
            .Set(CommandKeys.NutriIntensity, pumpSpeedPercent);

    /// <summary>Stops nutrient dosing (intensity zero), keeping the last valid timing and cycles.</summary>
    public static TecnalCommand NutrientControlSafeStop(
        int operationSeconds,
        int mixSeconds,
        int operationCycles,
        int mixCycles)
        => NutrientControl(operationSeconds, mixSeconds, operationCycles, mixCycles, 0.0);

    /// <summary>
    /// Complete antifoam dosing state: operation/mix timing and pump intensity.
    /// </summary>
    /// <remarks>
    /// As with nutrient, timing travels as floating-point JSON and the intensity is the
    /// raw operator percent (0-99), never <c>× 10</c>. Operation accepts 0-999 s and mix
    /// 1-999 s (<c>docs/PROTOCOL.md</c> §3.3).
    /// </remarks>
    public static TecnalCommand AntifoamControl(
        int operationSeconds,
        int mixSeconds,
        double pumpSpeedPercent)
        => TecnalCommand.Create()
            .Set(CommandKeys.AntifoamOperation, (double)operationSeconds)
            .Set(CommandKeys.AntifoamMix, (double)mixSeconds)
            .Set(CommandKeys.AntifoamIntensity, pumpSpeedPercent);

    /// <summary>
    /// Stops the antifoam pump — operation and intensity zero, mix retained.
    /// </summary>
    /// <remarks>
    /// This turns off the <i>pump</i>; it deliberately does not touch the level/foam
    /// sensor, so foam monitoring keeps running through a stop. See <see cref="FoamControl"/>.
    /// </remarks>
    public static TecnalCommand AntifoamControlSafeStop(int mixSeconds)
        => AntifoamControl(0, mixSeconds, 0.0);

    /// <summary>
    /// Level/foam sensor configuration and its automatic antifoam response.
    /// </summary>
    /// <remarks>
    /// This is sensor and automation configuration, not a held actuator, so it is
    /// intentionally outside the command arbiter and the global safe-stop — disabling the
    /// sensor to "stop" would blind foam monitoring. The reference is millimetres; the
    /// three timers travel as floating-point JSON.
    /// </remarks>
    public static TecnalCommand FoamControl(
        bool sensorEnabled,
        double referenceMillimetres,
        int startDelaySeconds,
        int pulseSeconds,
        int intervalSeconds)
        => TecnalCommand.Create()
            .Set(CommandKeys.DistanceSensorComm, sensorEnabled)
            .Set(CommandKeys.DistanceSensorReference, referenceMillimetres)
            .Set(CommandKeys.FoamStartDelaySeconds, (double)startDelaySeconds)
            .Set(CommandKeys.FoamPulseSeconds, (double)pulseSeconds)
            .Set(CommandKeys.FoamIntervalSeconds, (double)intervalSeconds);

    /// <summary>
    /// Flask-agitator state, converting the operator's <b>signed</b> percent into the
    /// wire's separate magnitude and direction keys.
    /// </summary>
    /// <remarks>
    /// The UI carries direction as the sign of a single -100..100 value; the wire carries
    /// magnitude (<c>agitatorPercent</c>, 0-100) and direction (<c>agitatorDir</c>, 1 CW /
    /// 0 CCW) as two keys. <b>The signed form must never leak onto the wire.</b>
    /// See <c>docs/PROTOCOL.md</c> §3.3. This is the separate flask agitator, not the
    /// reactor impeller (<see cref="MotorSetpoint"/>).
    /// </remarks>
    /// <param name="reEnablePot">
    /// When set, also writes the Hub's potentiometer re-enable flag. Leave null to keep
    /// whatever the Hub has persisted - which is what an ordinary on/off must do.
    /// </param>
    public static TecnalCommand FlaskAgitator(
        bool on, bool automatic, double signedPercent, bool? reEnablePot = null)
    {
        var magnitude = Math.Clamp(Math.Abs(signedPercent), 0.0, 100.0);
        var clockwise = signedPercent >= 0.0;

        var command = TecnalCommand.Create()
            .Set(CommandKeys.AgitatorOn, on)
            .Set(CommandKeys.AgitatorAuto, automatic)
            .Set(CommandKeys.AgitatorPercent, magnitude)
            .Set(CommandKeys.AgitatorDir, clockwise);

        // The Hub reads agitatorReEnablePot into its persisted flag before it acts on
        // agitatorOn, whichever order the keys appear in - it matches on the raw text. So
        // one frame is enough to switch the flag and stop in the intended state.
        return reEnablePot is { } pot ? command.Set(CommandKeys.AgitatorReEnablePot, pot) : command;
    }

    /// <summary>
    /// Ordinary stop: off and out of automatic mode, keeping the operator's staged
    /// magnitude and direction so re-enabling resumes it.
    /// </summary>
    /// <remarks>
    /// The potentiometer flag is deliberately left alone. If the Hub has it set, the bench
    /// knob takes the motor back on the node's next loop - which is the documented
    /// behaviour of that switch, and the operator's choice to make.
    /// </remarks>
    public static TecnalCommand FlaskAgitatorOff(double signedPercent)
        => FlaskAgitator(on: false, automatic: false, signedPercent);

    /// <summary>
    /// Safe stop: off, out of automatic mode, <b>and the potentiometer locked out</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place the pot flag is forced. The Hub turns <c>agitatorOn:0</c> into
    /// <c>{"RPM_percent":0,"ActivePot":agitatorReEnablePot}</c>, and the node's loop
    /// re-reads the knob on the very next pass whenever <c>ActivePot</c> is 1. A safe stop
    /// with the bench knob at 60 % would therefore restart the motor at 60 % - a stop that
    /// does not stop.
    /// </para>
    /// <para>
    /// The flag is persisted on the Hub, so it stays locked out until the operator
    /// deliberately re-arms it with <see cref="FlaskAgitatorReEnablePot"/>. That is the
    /// intended posture after an emergency stop: the knob does not get to restart the
    /// motor on its own.
    /// </para>
    /// </remarks>
    public static TecnalCommand FlaskAgitatorSafeStop(double signedPercent)
        => FlaskAgitator(on: false, automatic: false, signedPercent, reEnablePot: false);

    /// <summary>Re-enables the flask agitator's physical potentiometer. A momentary action.</summary>
    public static TecnalCommand FlaskAgitatorReEnablePot()
        => TecnalCommand.Create().Set(CommandKeys.AgitatorReEnablePot, 1);

    // ── Biomass (Phase 3 WP1) ────────────────────────────────────────────────

    /// <summary>Enables or disables the biomass optical sensor: <c>{"biomassComm":1/0}</c>.</summary>
    /// <remarks>
    /// Enable/disable is handled on the hub itself. While disabled, the hub drops the
    /// blank/start/stop/threshold sub-commands rather than forwarding them, so the operator
    /// enables the sensor before those take effect. See <c>docs/PROTOCOL.md</c> §3.4.
    /// </remarks>
    public static TecnalCommand BiomassComm(bool on)
        => TecnalCommand.Create().Set(CommandKeys.BiomassComm, on);

    /// <summary>Momentary: capture the blank (zero-absorbance) reference — <c>{"blank":1}</c>.</summary>
    public static TecnalCommand BiomassBlank()
        => TecnalCommand.Create().Set(CommandKeys.Blank, 1);

    /// <summary>Momentary: start the biomass acquisition loop — <c>{"start":1}</c>.</summary>
    public static TecnalCommand BiomassStart()
        => TecnalCommand.Create().Set(CommandKeys.BiomassStart, 1);

    /// <summary>Momentary: stop the biomass acquisition loop — <c>{"stop":1}</c>.</summary>
    public static TecnalCommand BiomassStop()
        => TecnalCommand.Create().Set(CommandKeys.BiomassStop, 1);

    /// <summary>
    /// The three integration-time thresholds, emitted atomically: <c>{"low":..,"high":..,"opt":..}</c>.
    /// </summary>
    /// <remarks>
    /// Raw ADC counts, integers, sent together exactly as v.6's <c>send_biomass_config</c> does.
    /// </remarks>
    public static TecnalCommand BiomassThresholds(int low, int high, int optimal)
        => TecnalCommand.Create()
            .Set(CommandKeys.Low, low)
            .Set(CommandKeys.High, high)
            .Set(CommandKeys.Opt, optimal);

    // ── External pump (Phase 3 WP2) ──────────────────────────────────────────

    /// <summary>Enables the external pump's command routing on the hub: <c>{"pumpComm":1}</c>.</summary>
    public static TecnalCommand PumpEnable()
        => TecnalCommand.Create().Set(CommandKeys.PumpComm, 1);

    /// <summary>
    /// Stops the running profile: <c>{"mode":0,"speed":0}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Must be sent while routing is still on.</b> The Hub forwards the pump block only
    /// under <c>if (pumpCmdFound &amp;&amp; pumpCommOn)</c>, and it parses <c>pumpComm</c>
    /// before it reaches that block - so v.6's combined
    /// <c>{"pumpComm":0,"mode":0,"speed":0}</c> clears routing and then discards its own
    /// <c>mode:0</c>. The node keeps dosing; only the telemetry goes quiet.
    /// </para>
    /// <para>
    /// Once this frame is queued in the Hub's mailbox it survives a later
    /// <c>pumpComm:0</c>: <c>/pumpCommand</c> has no routing gate, so the node still
    /// collects it on its next poll. The two frames only have to arrive in order, which is
    /// what <see cref="PumpRoutingDisabled"/> and the ordered-frame send guarantee.
    /// </para>
    /// <para>
    /// <c>speed</c> stays for byte-parity with v.6 and is inert - the firmware forwards
    /// <c>pump_speed</c>, not <c>speed</c>.
    /// </para>
    /// </remarks>
    public static TecnalCommand PumpStopProfile()
        => TecnalCommand.Create()
            .Set(CommandKeys.Mode, 0)
            .Set(CommandKeys.Speed, 0);

    /// <summary>
    /// Clears the Hub's pump routing: <c>{"pumpComm":0}</c>.
    /// </summary>
    /// <remarks>
    /// Always the <i>second</i> frame, after <see cref="PumpStopProfile"/>. On its own it
    /// stops the Hub forwarding and publishing, and leaves the node running whatever
    /// profile it last received.
    /// </remarks>
    public static TecnalCommand PumpRoutingDisabled()
        => TecnalCommand.Create().Set(CommandKeys.PumpComm, 0);

    /// <summary>Mode-1 constant profile: <c>Q(t') = λ</c> mL/min.</summary>
    public static TecnalCommand PumpConstant(double initMinutes, double finalMinutes, double lambda)
        => PumpHeader(PumpProfileMode.Constant, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaConst, lambda);

    /// <summary>Mode-2 linear profile: <c>Q(t') = λ + φ·t'</c>.</summary>
    public static TecnalCommand PumpLinear(double initMinutes, double finalMinutes, double lambda, double phi)
        => PumpHeader(PumpProfileMode.Linear, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaLinear, lambda)
            .Set(CommandKeys.PhiLinear, phi);

    /// <summary>Mode-3 exponential profile: <c>Q(t') = λ·e^(φ·t')</c>.</summary>
    public static TecnalCommand PumpExponential(double initMinutes, double finalMinutes, double lambda, double phi)
        => PumpHeader(PumpProfileMode.Exponential, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaExp, lambda)
            .Set(CommandKeys.PhiExp, phi);

    /// <summary>
    /// Mode-4 polynomial profile: <c>Q(t') = p0 + p1·t' + … + pN·t'^N</c>, one key per coefficient.
    /// </summary>
    /// <param name="coefficients"><c>p0..pN</c>, low order first. 1 to 21 values (firmware holds p0..p20).</param>
    public static TecnalCommand PumpPolynomial(
        double initMinutes, double finalMinutes, IReadOnlyList<double> coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (coefficients.Count is < 1 or > CommandKeys.MaxPolynomialCoefficientIndex + 1)
        {
            throw new ArgumentException(
                $"Polynomial mode takes 1 to {CommandKeys.MaxPolynomialCoefficientIndex + 1} coefficients.",
                nameof(coefficients));
        }

        var command = PumpHeader(PumpProfileMode.Polynomial, initMinutes, finalMinutes);
        for (var i = 0; i < coefficients.Count; i++)
        {
            command.Set(CommandKeys.PolynomialCoefficient(i), coefficients[i]);
        }

        return command;
    }

    /// <summary>
    /// Mode-5 piecewise profile: linear interpolation through <paramref name="times"/> (minutes,
    /// <c>t0 = 0</c>, strictly increasing) and <paramref name="flows"/> (mL/min), sent as
    /// <c>num_segments</c> then interleaved <c>t0,q0,t1,q1,…</c> exactly as v.6 does.
    /// </summary>
    public static TecnalCommand PumpPiecewise(
        double initMinutes, double finalMinutes, IReadOnlyList<double> times, IReadOnlyList<double> flows)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(flows);
        if (times.Count != flows.Count)
        {
            throw new ArgumentException("Piecewise times and flows must have the same count.", nameof(flows));
        }

        if (times.Count is < 2 or > CommandKeys.MaxPiecewiseSegments)
        {
            throw new ArgumentException(
                $"Piecewise mode takes 2 to {CommandKeys.MaxPiecewiseSegments} points.", nameof(times));
        }

        var command = PumpHeader(PumpProfileMode.Piecewise, initMinutes, finalMinutes)
            .Set(CommandKeys.NumSegments, times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            command.Set(CommandKeys.PiecewiseTime(i), times[i]);
            command.Set(CommandKeys.PiecewiseFlow(i), flows[i]);
        }

        return command;
    }

    /// <summary>The common <c>mode</c>/<c>init_t</c>/<c>final_t</c> header every profile carries.</summary>
    private static TecnalCommand PumpHeader(PumpProfileMode mode, double initMinutes, double finalMinutes)
        => TecnalCommand.Create()
            .Set(CommandKeys.Mode, (int)mode)
            .Set(CommandKeys.InitT, initMinutes)
            .Set(CommandKeys.FinalT, finalMinutes);

    /// <summary>
    /// Flow setpoint shape used while acquiring a calibration point in v.6.
    /// Both optional gas valves are closed and the inverted vent flag is derived.
    /// </summary>
    public static TecnalCommand FlowCalibrationSetpoint(double setpoint)
    {
        var safe = Math.Max(setpoint, 0.0);
        return TecnalCommand.Create()
            .Set(CommandKeys.FlowSetpoint, safe)
            .Set(CommandKeys.Valve1, false)
            .Set(CommandKeys.Valve2, false)
            .Set(CommandKeys.V_Flow, safe <= 0.0);
    }

    /// <summary>
    /// Low-voltage flowmeter polynomial, valid at V &lt;= 0.0545.
    /// </summary>
    /// <remarks>
    /// Sending <paramref name="a"/> and <paramref name="b"/> opts the firmware into the quartic
    /// low-range model; a quadratic fit passes zero for both, which the firmware treats exactly
    /// as the legacy k1/f1/c1 command.
    /// </remarks>
    public static TecnalCommand FlowCalibrationLow(double k, double f, double c, double a = 0.0, double b = 0.0)
        => TecnalCommand.Create()
            .Set(CommandKeys.A1, a)
            .Set(CommandKeys.B1, b)
            .Set(CommandKeys.K1, k)
            .Set(CommandKeys.F1, f)
            .Set(CommandKeys.C1, c);

    /// <summary>High-voltage flowmeter polynomial, valid at V &gt; 0.0545.</summary>
    public static TecnalCommand FlowCalibrationHigh(double k, double f, double c)
        => TecnalCommand.Create()
            .Set(CommandKeys.K2, k)
            .Set(CommandKeys.F2, f)
            .Set(CommandKeys.C2, c);

    /// <summary>
    /// Combined cascade actuation: flow, oxygen and motor in one frame.
    /// </summary>
    /// <remarks>
    /// The kLa cascade sends all three together to save round trips on the shared
    /// UART. Key order matches v.6 so captured traffic stays byte-comparable.
    /// </remarks>
    public static TecnalCommand CascadeActuation(double flowSetpoint, double oxygenSetpoint, int motorRpm)
        => TecnalCommand.Create()
            .Set(CommandKeys.FlowSetpoint, flowSetpoint)
            .Set(CommandKeys.Valve1, false)
            .Set(CommandKeys.Valve2, false)
            .Set(CommandKeys.V_Flow, flowSetpoint <= 0.0)
            .Set(CommandKeys.OxygenMonitor, oxygenSetpoint)
            .Set(CommandKeys.MotorSetpoint, motorRpm);

    /// <summary>Telemetry emission period, in milliseconds.</summary>
    public static TecnalCommand DataDelay(int milliseconds)
        => TecnalCommand.Create().Set(CommandKeys.DataDelay, milliseconds);

    /// <summary>Resets the module's process variables.</summary>
    public static TecnalCommand ResetVariables()
        => TecnalCommand.Create().Set(CommandKeys.ResetVariables, 1);

    /// <summary>Restarts all controller communications.</summary>
    public static TecnalCommand Restart()
        => TecnalCommand.Create().Set(CommandKeys.Restart, 1);
}
