namespace OpenTECHub.Protocol;

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
    public static OpenTECCommand Handshake()
        => OpenTECCommand.Create().Set(CommandKeys.ComTest, 1);

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
    public static OpenTECCommand FlowSetpoint(
        double setpoint,
        double maxFlow,
        bool valve1 = false,
        bool valve2 = false,
        bool mainValveClosed = false)
    {
        var clamped = Math.Clamp(setpoint, 0.0, maxFlow);

        return OpenTECCommand.Create()
            .Set(CommandKeys.FlowSetpoint, clamped)
            .Set(CommandKeys.MaxFlow, maxFlow)
            .Set(CommandKeys.Valve1, valve1)
            .Set(CommandKeys.Valve2, valve2)
            .Set(CommandKeys.V_Flow, mainValveClosed || clamped == 0.0);
    }

    /// <summary>
    /// Enables flow control at <paramref name="setpoint"/> L/min with the gas sent where
    /// <paramref name="route"/> says, on the rig wired as <paramref name="rig"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The A/B/C rig has no default path: with both outputs off and a setpoint above zero the
    /// controller pushes into a dead-ended line. So a setpoint above zero <b>requires a
    /// destination</b>, and <see cref="GasRoute.Closed"/> with one throws rather than build the
    /// frame. Every producer of a gas command goes through here; nobody writes
    /// <c>valve_1</c>/<c>valve_2</c> by hand.
    /// </para>
    /// <para>
    /// Built on <see cref="FlowSetpoint"/>, so the wire shape (key order, <c>v_Flow</c> derived
    /// from the setpoint) is byte-identical to before.
    /// </para>
    /// </remarks>
    public static OpenTECCommand FlowRoute(double setpoint, double maxFlow, GasRoute route, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        if (route == GasRoute.Closed && setpoint > 0.0)
        {
            throw new ArgumentException(
                "Um setpoint de vazão acima de zero exige um destino para o gás (Reator ou Descarga + N₂); " +
                "com A e B/C fechadas a linha fica sem saída.", nameof(route));
        }

        var (valve1, valve2) = GasRouting.Resolve(route, rig);
        return FlowSetpoint(setpoint, maxFlow, valve1, valve2);
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
    public static OpenTECCommand FlowmeterLoopEnabled(bool enabled)
        => OpenTECCommand.Create().Set(CommandKeys.FlowmeterComm, enabled ? 1 : 0);

    /// <summary>
    /// Commands the flowmeter to enable or disable its automatic Wi-Fi reconnection logic.
    /// </summary>
    public static OpenTECCommand FlowmeterReconnectWifi(bool enable)
        => OpenTECCommand.Create().Set(CommandKeys.ReconnectWifi, enable ? 1 : 0);

    /// <summary>
    /// Disables the flow subsystem and closes everything.
    /// </summary>
    /// <remarks>
    /// <b>Both valves are forced closed</b> rather than preserving the operator's
    /// manual selection. v.6 does this deliberately: leaving a nitrogen valve open
    /// through a safe-stop is a hazard. Preserve this behaviour.
    /// </remarks>
    public static OpenTECCommand FlowSafeStop(double maxFlow)
        => OpenTECCommand.Create()
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
    /// gas inputs close and the line shutoff (`v_Flow`, active high) is asserted. Keeping the whole stop
    /// here makes the destructive-confirmation preview byte-identical to what is sent.
    /// </remarks>
    public static OpenTECCommand CoreSafeStop(double maxFlow)
        => OpenTECCommand.Create()
            .Set(CommandKeys.TempSetpoint, 0.0)
            .Set(CommandKeys.MotorSetpoint, 0)
            .Set(CommandKeys.OxygenMonitor, 0.0)
            .Merge(FlowSafeStop(maxFlow))
            .Set(CommandKeys.PressureReference, 0.0);

    /// <summary>
    /// Agitation reference in rpm. Valid range is 15-1000; <c>0</c> <b>disables</b> the motor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always an integer on the wire. Hub 10 sends it by the route selected with
    /// <see cref="MotorControlMode(bool)"/>. UART/CN1 intentionally sends the raw value,
    /// without the removed PI, so the original module and its display remain authoritative.
    /// </para>
    /// <para>
    /// <b>Zero disables rather than stops.</b> The module's UART vocabulary uses <c>V</c>
    /// as the enable flag, so a zero reference sends <c>0V</c> and the module latches
    /// disabled - its own keypad will not restore the motor until a non-zero setpoint
    /// arrives. Anything presenting this as a stop control has to say so.
    /// </para>
    /// </remarks>
    public static OpenTECCommand MotorSetpoint(int rpm)
    {
        if (rpm != 0)
        {
            rpm = Math.Clamp(rpm, 15, 1000);
        }

        return OpenTECCommand.Create().Set(CommandKeys.MotorSetpoint, rpm);
    }

    /// <summary>Selects the exclusive motor route; changing it disables the motor.</summary>
    public static OpenTECCommand MotorControlMode(bool viaModbus)
        => OpenTECCommand.Create().Set(CommandKeys.MotorControlMode, viaModbus ? 1 : 0);

    /// <summary>Calibrated pH echoed back to the device as a 2-decimal quoted string.</summary>
    /// <remarks>See <c>docs/PROTOCOL.md</c> section 2.2.</remarks>
    public static OpenTECCommand PHCalibration(double calibratedPH)
        => OpenTECCommand.Create().SetFixedString(CommandKeys.PHCal, calibratedPH, 2);

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
    public static OpenTECCommand PHControl(
        double setpoint,
        double inactiveBand,
        int operationSeconds,
        int mixSeconds,
        double pumpSpeedPercent)
        => OpenTECCommand.Create()
            .Set(CommandKeys.PHSetpoint, setpoint)
            .Set(CommandKeys.PHError, inactiveBand)
            .Set(CommandKeys.PHOperation, (double)operationSeconds)
            .Set(CommandKeys.PHMix, (double)mixSeconds)
            .Set(CommandKeys.PHIntensity, pumpSpeedPercent * 10.0);

    /// <summary>
    /// Disables pH dosing without discarding the last valid timing and inactive band.
    /// </summary>
    public static OpenTECCommand PHControlSafeStop(
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
    public static OpenTECCommand NutrientControl(
        int operationSeconds,
        int mixSeconds,
        int operationCycles,
        int mixCycles,
        double pumpSpeedPercent)
        => OpenTECCommand.Create()
            .Set(CommandKeys.NutriOperation, (double)operationSeconds)
            .Set(CommandKeys.NutriMix, (double)mixSeconds)
            .Set(CommandKeys.NutriOpCycle, (double)operationCycles)
            .Set(CommandKeys.NutriMixCycle, (double)mixCycles)
            .Set(CommandKeys.NutriIntensity, pumpSpeedPercent);

    /// <summary>Stops nutrient dosing (intensity zero), keeping the last valid timing and cycles.</summary>
    public static OpenTECCommand NutrientControlSafeStop(
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
    public static OpenTECCommand AntifoamControl(
        int operationSeconds,
        int mixSeconds,
        double pumpSpeedPercent)
        => OpenTECCommand.Create()
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
    public static OpenTECCommand AntifoamControlSafeStop(int mixSeconds)
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
    public static OpenTECCommand FoamControl(
        bool sensorEnabled,
        double referenceMillimetres,
        int startDelaySeconds,
        int pulseSeconds,
        int intervalSeconds)
        => OpenTECCommand.Create()
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
    public static OpenTECCommand FlaskAgitator(
        bool on, bool automatic, double signedPercent, bool? reEnablePot = null)
    {
        var magnitude = Math.Clamp(Math.Abs(signedPercent), 0.0, 100.0);
        var clockwise = signedPercent >= 0.0;

        var command = OpenTECCommand.Create()
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
    public static OpenTECCommand FlaskAgitatorOff(double signedPercent)
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
    public static OpenTECCommand FlaskAgitatorSafeStop(double signedPercent)
        => FlaskAgitator(on: false, automatic: false, signedPercent, reEnablePot: false);

    /// <summary>Re-enables the flask agitator's physical potentiometer. A momentary action.</summary>
    public static OpenTECCommand FlaskAgitatorReEnablePot()
        => OpenTECCommand.Create().Set(CommandKeys.AgitatorReEnablePot, 1);

    // ── Biomass (Phase 3 WP1) ────────────────────────────────────────────────

    /// <summary>Enables or disables the biomass optical sensor: <c>{"biomassComm":1/0}</c>.</summary>
    /// <remarks>
    /// Enable/disable is handled on the hub itself. While disabled, the hub drops the
    /// blank/start/stop/threshold sub-commands rather than forwarding them, so the operator
    /// enables the sensor before those take effect. See <c>docs/PROTOCOL.md</c> §3.4.
    /// </remarks>
    public static OpenTECCommand BiomassComm(bool on)
        => OpenTECCommand.Create().Set(CommandKeys.BiomassComm, on);

    /// <summary>Momentary: capture the blank (zero-absorbance) reference — <c>{"blank":1}</c>.</summary>
    public static OpenTECCommand BiomassBlank()
        => OpenTECCommand.Create().Set(CommandKeys.Blank, 1);

    /// <summary>Momentary: start the biomass acquisition loop — <c>{"start":1}</c>.</summary>
    public static OpenTECCommand BiomassStart()
        => OpenTECCommand.Create().Set(CommandKeys.BiomassStart, 1);

    /// <summary>Momentary: stop the biomass acquisition loop — <c>{"stop":1}</c>.</summary>
    public static OpenTECCommand BiomassStop()
        => OpenTECCommand.Create().Set(CommandKeys.BiomassStop, 1);

    /// <summary>
    /// The three integration-time thresholds, emitted atomically: <c>{"low":..,"high":..,"opt":..}</c>.
    /// </summary>
    /// <remarks>
    /// Raw ADC counts, integers, sent together exactly as v.6's <c>send_biomass_config</c> does.
    /// </remarks>
    public static OpenTECCommand BiomassThresholds(int low, int high, int optimal)
        => OpenTECCommand.Create()
            .Set(CommandKeys.Low, low)
            .Set(CommandKeys.High, high)
            .Set(CommandKeys.Opt, optimal);

    // ── External pump (Phase 3 WP2) ──────────────────────────────────────────

    /// <summary>Enables the external pump's command routing on the hub: <c>{"pumpComm":1}</c>.</summary>
    public static OpenTECCommand PumpEnable()
        => OpenTECCommand.Create().Set(CommandKeys.PumpComm, 1);

    /// <summary>
    /// Stops the running profile: <c>{"mode":0}</c>.
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
    /// Per plan 2026-09-12 §3.7, <c>speed</c> is omitted so the pump node firmware
    /// does not interpret it as a command to arm or run in speed mode.
    /// </para>
    /// </remarks>
    public static OpenTECCommand PumpStopProfile()
        => OpenTECCommand.Create()
            .Set(CommandKeys.Mode, 0);

    /// <summary>
    /// Clears the Hub's pump routing: <c>{"pumpComm":0}</c>.
    /// </summary>
    /// <remarks>
    /// Always the <i>second</i> frame, after <see cref="PumpStopProfile"/>. On its own it
    /// stops the Hub forwarding and publishing, and leaves the node running whatever
    /// profile it last received.
    /// </remarks>
    public static OpenTECCommand PumpRoutingDisabled()
        => OpenTECCommand.Create().Set(CommandKeys.PumpComm, 0);

    // ── ASDA-B2 servo drive (Hub v9) ─────────────────────────────────────────
    //
    // Servo data commands. The motor route itself is selected separately by
    // MotorControlMode and remains outside the event FIFO.
    //
    // This link has no acknowledgement: the node pulls from a consume-on-read mailbox
    // every 2 s and never answers. Confirmation is therefore observational - the energy
    // falling to zero, or the ServoCommOk growth rate changing - and never an ack.

    /// <summary>Servo routing on the Hub: <c>{"servoComm":1/0}</c>.</summary>
    /// <remarks>
    /// Persisted in the Hub's NVS and echoed back as <c>ServoCommEnabled</c>. Turning it
    /// off does not make the node absent: <c>ServoOnline</c> stays true while the node
    /// keeps pushing, and only the ten measured values leave the frame. That is the
    /// distinction the v8 contract could not express.
    /// </remarks>
    public static OpenTECCommand ServoRouting(bool on)
        => OpenTECCommand.Create().Set(CommandKeys.ServoComm, on);

    /// <summary>Zeroes the node's energy accumulator: <c>{"resetServoEnergy":1}</c>.</summary>
    /// <remarks>
    /// <para>
    /// Only the value <c>1</c> is a command; the Hub queues nothing for <c>0</c>. Never
    /// coalesced with other queued commands, unlike a bare poll-interval change.
    /// </para>
    /// <para>
    /// It clears a data accumulator and does nothing to the process, so it must not be
    /// presented alongside a stop control. Confirmation is <c>ServoEnergyWh</c> falling to
    /// roughly zero - with a full queue that can take up to sixteen seconds to appear.
    /// </para>
    /// </remarks>
    public static OpenTECCommand ResetServoEnergy()
        => OpenTECCommand.Create().Set(CommandKeys.ResetServoEnergy, 1);

    /// <summary>Modbus sampling interval on the node, 250-10000 ms.</summary>
    /// <remarks>
    /// <para>
    /// Out-of-range values are <b>rejected here</b> rather than clamped. The Hub also
    /// validates and refuses them, but it does so by printing a warning on its own serial
    /// port, which nobody is reading: silently sending a value that will be dropped would
    /// leave the operator waiting for an effect that never arrives.
    /// </para>
    /// <para>
    /// The interval is a <i>delay between samples</i>, not a period. Each sample costs
    /// about 250 ms of bus time - four Modbus transactions at 9600 8N2 - so 250 ms
    /// produces a real cycle near 500 ms. Do not compute an expected rate as
    /// <c>1000/pollMs</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pollMs"/> is outside 250-10000.
    /// </exception>
    public static OpenTECCommand ServoPollInterval(int pollMs)
    {
        if (pollMs is < ServoPollMinimumMs or > ServoPollMaximumMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollMs), pollMs,
                $"O intervalo de amostragem deve estar entre {ServoPollMinimumMs} e {ServoPollMaximumMs} ms.");
        }

        return OpenTECCommand.Create().Set(CommandKeys.ServoPollMs, pollMs);
    }

    /// <summary>Shortest sampling interval the Hub accepts, in milliseconds.</summary>
    public const int ServoPollMinimumMs = 250;

    /// <summary>Longest sampling interval the Hub accepts, in milliseconds.</summary>
    /// <remarks>
    /// The node slices its wait and feeds the watchdog on every slice, so the top of this
    /// range no longer restarts the board the way it did before the v9 fix.
    /// </remarks>
    public const int ServoPollMaximumMs = 10000;

    /// <summary>Mode-1 constant profile: <c>Q(t') = λ</c> mL/min.</summary>
    public static OpenTECCommand PumpConstant(double initMinutes, double finalMinutes, double lambda)
        => PumpHeader(PumpProfileMode.Constant, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaConst, lambda);

    /// <summary>Mode-2 linear profile: <c>Q(t') = λ + φ·t'</c>.</summary>
    public static OpenTECCommand PumpLinear(double initMinutes, double finalMinutes, double lambda, double phi)
        => PumpHeader(PumpProfileMode.Linear, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaLinear, lambda)
            .Set(CommandKeys.PhiLinear, phi);

    /// <summary>Mode-3 exponential profile: <c>Q(t') = λ·e^(φ·t')</c>.</summary>
    public static OpenTECCommand PumpExponential(double initMinutes, double finalMinutes, double lambda, double phi)
        => PumpHeader(PumpProfileMode.Exponential, initMinutes, finalMinutes)
            .Set(CommandKeys.LambdaExp, lambda)
            .Set(CommandKeys.PhiExp, phi);

    /// <summary>
    /// Mode-4 polynomial profile: <c>Q(t') = p0 + p1·t' + … + pN·t'^N</c>, one key per coefficient.
    /// </summary>
    /// <param name="coefficients"><c>p0..pN</c>, low order first. 1 to 21 values (firmware holds p0..p20).</param>
    public static OpenTECCommand PumpPolynomial(
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
    public static OpenTECCommand PumpPiecewise(
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
    private static OpenTECCommand PumpHeader(PumpProfileMode mode, double initMinutes, double finalMinutes)
        => OpenTECCommand.Create()
            .Set(CommandKeys.Mode, (int)mode)
            .Set(CommandKeys.InitT, initMinutes)
            .Set(CommandKeys.FinalT, finalMinutes);

    /// <summary>
    /// Flow setpoint shape used while acquiring a calibration point in v.6.
    /// Legacy overload: blows through C on the default wiring; the line shutoff follows the setpoint.
    /// </summary>
    public static OpenTECCommand FlowCalibrationSetpoint(double setpoint)
        => FlowCalibrationSetpoint(setpoint, setpoint > 0.0 ? GasRoute.VentAndNitrogen : GasRoute.Closed, GasRigConfiguration.Default);

    /// <summary>
    /// Calibration trial setpoint sent where <paramref name="route"/> says. Calibration blows
    /// through C by default (<see cref="GasRoute.VentAndNitrogen"/>): the meter is exercised
    /// without filling the vessel, with the nitrogen shut at the source.
    /// </summary>
    /// <remarks>
    /// The frame keeps the firmware key order (<c>flowSetpoint</c>, valves, <c>v_Flow</c>) and
    /// carries no <c>maxFlow</c>, exactly as before; only the valve pair comes from the router.
    /// </remarks>
    public static OpenTECCommand FlowCalibrationSetpoint(double setpoint, GasRoute route, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var safe = Math.Max(setpoint, 0.0);
        if (route == GasRoute.Closed && safe > 0.0)
        {
            throw new ArgumentException(
                "Um setpoint de calibração acima de zero exige um destino para o gás.", nameof(route));
        }

        var (valve1, valve2) = GasRouting.Resolve(route, rig);
        return OpenTECCommand.Create()
            .Set(CommandKeys.FlowSetpoint, safe)
            .Set(CommandKeys.Valve1, valve1)
            .Set(CommandKeys.Valve2, valve2)
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
    public static OpenTECCommand FlowCalibrationLow(double k, double f, double c, double a = 0.0, double b = 0.0)
        => OpenTECCommand.Create()
            .Set(CommandKeys.A1, a)
            .Set(CommandKeys.B1, b)
            .Set(CommandKeys.K1, k)
            .Set(CommandKeys.F1, f)
            .Set(CommandKeys.C1, c);

    /// <summary>High-voltage flowmeter polynomial, valid at V &gt; 0.0545.</summary>
    public static OpenTECCommand FlowCalibrationHigh(double k, double f, double c)
        => OpenTECCommand.Create()
            .Set(CommandKeys.K2, k)
            .Set(CommandKeys.F2, f)
            .Set(CommandKeys.C2, c);

    /// <summary>
    /// Builds an atomic flowmeter calibration command containing both segments, maxFlow, and the transition voltage.
    /// Emits keys in the order: maxFlow, a1, b1, k1, f1, c1, k2, f2, c2, flowTransitionVoltage.
    /// </summary>
    public static OpenTECCommand FlowCalibration(
        double maxFlow,
        double a1, double b1, double k1, double f1, double c1,
        double k2, double f2, double c2,
        double transitionVoltage)
    {
        if (!double.IsFinite(maxFlow) || maxFlow <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFlow), "MaxFlow must be finite and greater than zero.");
        }
        if (!double.IsFinite(a1)) { throw new ArgumentOutOfRangeException(nameof(a1), "a1 must be finite."); }
        if (!double.IsFinite(b1)) { throw new ArgumentOutOfRangeException(nameof(b1), "b1 must be finite."); }
        if (!double.IsFinite(k1)) { throw new ArgumentOutOfRangeException(nameof(k1), "k1 must be finite."); }
        if (!double.IsFinite(f1)) { throw new ArgumentOutOfRangeException(nameof(f1), "f1 must be finite."); }
        if (!double.IsFinite(c1)) { throw new ArgumentOutOfRangeException(nameof(c1), "c1 must be finite."); }
        if (!double.IsFinite(k2)) { throw new ArgumentOutOfRangeException(nameof(k2), "k2 must be finite."); }
        if (!double.IsFinite(f2)) { throw new ArgumentOutOfRangeException(nameof(f2), "f2 must be finite."); }
        if (!double.IsFinite(c2)) { throw new ArgumentOutOfRangeException(nameof(c2), "c2 must be finite."); }
        if (!double.IsFinite(transitionVoltage) || transitionVoltage <= 0.0 || transitionVoltage >= 3.3)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionVoltage), "Transition voltage must be finite and in (0.0, 3.3) V.");
        }

        return OpenTECCommand.Create()
            .Set(CommandKeys.MaxFlow, maxFlow)
            .Set(CommandKeys.A1, a1)
            .Set(CommandKeys.B1, b1)
            .Set(CommandKeys.K1, k1)
            .Set(CommandKeys.F1, f1)
            .Set(CommandKeys.C1, c1)
            .Set(CommandKeys.K2, k2)
            .Set(CommandKeys.F2, f2)
            .Set(CommandKeys.C2, c2)
            .Set(CommandKeys.FlowTransitionVoltage, transitionVoltage);
    }

    /// <summary>
    /// Combined cascade actuation: flow, oxygen and motor in one frame.
    /// </summary>
    /// <remarks>
    /// The kLa cascade sends all three together to save round trips on the shared
    /// UART. Key order matches v.6 so captured traffic stays byte-comparable.
    /// </remarks>
    public static OpenTECCommand CascadeActuation(double flowSetpoint, double oxygenSetpoint, int motorRpm)
        => CascadeActuation(flowSetpoint, oxygenSetpoint, motorRpm, GasRigConfiguration.Default);

    /// <summary>
    /// Cascade actuation frame with the aeration sent to the reactor (A) on <paramref name="rig"/>;
    /// a zero aeration closes A. Same v.6 key order as the legacy overload.
    /// </summary>
    public static OpenTECCommand CascadeActuation(
        double flowSetpoint, double oxygenSetpoint, int motorRpm, GasRigConfiguration rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var route = flowSetpoint > 0.0 ? GasRoute.Reactor : GasRoute.Closed;
        var (valve1, valve2) = GasRouting.Resolve(route, rig);
        return OpenTECCommand.Create()
            .Set(CommandKeys.FlowSetpoint, flowSetpoint)
            .Set(CommandKeys.Valve1, valve1)
            .Set(CommandKeys.Valve2, valve2)
            .Set(CommandKeys.V_Flow, flowSetpoint <= 0.0)
            .Set(CommandKeys.OxygenMonitor, oxygenSetpoint)
            .Set(CommandKeys.MotorSetpoint, motorRpm);
    }

    /// <summary>Telemetry emission period, in milliseconds.</summary>
    public static OpenTECCommand DataDelay(int milliseconds)
        => OpenTECCommand.Create().Set(CommandKeys.DataDelay, milliseconds);

    /// <summary>Resets the module's process variables.</summary>
    public static OpenTECCommand ResetVariables()
        => OpenTECCommand.Create().Set(CommandKeys.ResetVariables, 1);

    /// <summary>Restarts all controller communications.</summary>
    public static OpenTECCommand Restart()
        => OpenTECCommand.Create().Set(CommandKeys.Restart, 1);

    // ── External-node configuration (Hub 10.2) ───────────────────────────────

    /// <summary>
    /// Configures distance node parameters: offset, sample period, and/or send period.
    /// Emits only the non-null keys.
    /// </summary>
    public static OpenTECCommand DistanceConfig(
        double? offsetMm = null,
        int? samplePeriodMs = null,
        int? sendPeriodMs = null)
    {
        if (offsetMm is null && samplePeriodMs is null && sendPeriodMs is null)
        {
            throw new ArgumentException("Pelo menos um parâmetro de distância deve ser fornecido.");
        }

        var cmd = OpenTECCommand.Create();
        if (offsetMm.HasValue)
        {
            cmd.Set(CommandKeys.DistanceOffsetMm, offsetMm.Value);
        }
        if (samplePeriodMs.HasValue)
        {
            cmd.Set(CommandKeys.DistanceSamplePeriodMs, samplePeriodMs.Value);
        }
        if (sendPeriodMs.HasValue)
        {
            cmd.Set(CommandKeys.DistanceSendPeriodMs, sendPeriodMs.Value);
        }
        return cmd;
    }

    /// <summary>Restores distance node factory defaults in NVS: <c>{"distanceResetNvs":1}</c>.</summary>
    public static OpenTECCommand DistanceResetNvs()
        => OpenTECCommand.Create().Set(CommandKeys.DistanceResetNvs, 1);

    /// <summary>
    /// Configures flowmeter controller tuning parameters. Emits only the non-null keys.
    /// </summary>
    public static OpenTECCommand FlowTuning(
        double? kp = null,
        double? ki = null,
        double? ffGain = null,
        double? ffOffset = null,
        double? rampRate = null)
    {
        if (kp is null && ki is null && ffGain is null && ffOffset is null && rampRate is null)
        {
            throw new ArgumentException("Pelo menos um parâmetro de sintonia deve ser fornecido.");
        }

        var cmd = OpenTECCommand.Create();
        if (kp.HasValue)
        {
            cmd.Set(CommandKeys.FlowKp, kp.Value);
        }
        if (ki.HasValue)
        {
            cmd.Set(CommandKeys.FlowKi, ki.Value);
        }
        if (ffGain.HasValue)
        {
            cmd.Set(CommandKeys.FlowFfGain, ffGain.Value);
        }
        if (ffOffset.HasValue)
        {
            cmd.Set(CommandKeys.FlowFfOffset, ffOffset.Value);
        }
        if (rampRate.HasValue)
        {
            cmd.Set(CommandKeys.FlowRampRate, rampRate.Value);
        }
        return cmd;
    }

    /// <summary>
    /// Holds the pump motor at internal speed <paramref name="speedUnits"/> (0..1000) with
    /// no profile running: <c>{"pump_speed":S}</c>. Zero stops the motor.
    /// </summary>
    /// <remarks>
    /// The node applies it in idle mode and keeps the speed until the next <c>pump_speed</c>
    /// or profile frame - there is no timer on the node side. The caller owns the stop: the
    /// volumetric calibration sends <c>0</c> from its own clock so it knows how long the
    /// pump actually ran.
    /// </remarks>
    public static OpenTECCommand PumpManualSpeed(int speedUnits)
    {
        if (speedUnits is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(speedUnits), "Pump speed must be within 0..1000 internal units.");
        }

        return OpenTECCommand.Create().Set(CommandKeys.PumpManualSpeed, speedUnits);
    }

    /// <summary>
    /// <see cref="PumpManualSpeed(int)"/> with a node-side deadline: a 3.10 pump stops the
    /// motor by itself after <paramref name="deadlineMs"/>. The caller still owns the
    /// primary stop; the deadline is the safety net for a link that drops mid-run.
    /// </summary>
    public static OpenTECCommand PumpManualSpeed(int speedUnits, int deadlineMs)
    {
        if (deadlineMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineMs), "Deadline must be positive.");
        }

        return PumpManualSpeed(speedUnits).Set(CommandKeys.PumpManualSpeedMs, deadlineMs);
    }

    /// <summary>Hands the motor to the bench potentiometers (<c>true</c>) or locks them out: <c>{"pump_pot":1|0}</c>.</summary>
    public static OpenTECCommand PumpPotentiometers(bool enabled)
        => OpenTECCommand.Create().Set(CommandKeys.PumpPotentiometers, enabled ? 1 : 0);

    /// <summary>Resets the accumulated volume on the external pump node.</summary>
    public static OpenTECCommand PumpResetVolume()
        => OpenTECCommand.Create().Set(CommandKeys.PumpCommand, "reset_volume");

    /// <summary>
    /// Configures the external pump quartic/quadratic C0+C1 calibration atomically.
    /// </summary>
    public static OpenTECCommand PumpDualRangeCalibration(
        double a1, double b1, double k1, double f1, double c1,
        double k2, double f2, double c2, double transitionSpeed)
    {
        var coefficients = new[] { a1, b1, k1, f1, c1, k2, f2, c2 };
        if (coefficients.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(a1), "Pump coefficients must be finite.");
        }
        if (!double.IsFinite(transitionSpeed) || transitionSpeed <= 0.0 || transitionSpeed >= 1000.0)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionSpeed), "Transition speed must be finite and in (0, 1000) speed units.");
        }

        return OpenTECCommand.Create()
            .Set(CommandKeys.PumpA1, a1).Set(CommandKeys.PumpB1, b1)
            .Set(CommandKeys.PumpK1, k1).Set(CommandKeys.PumpF1, f1).Set(CommandKeys.PumpC1, c1)
            .Set(CommandKeys.PumpK2, k2).Set(CommandKeys.PumpF2, f2).Set(CommandKeys.PumpC2, c2)
            .Set(CommandKeys.PumpTransitionSpeed, transitionSpeed);
    }

    /// <summary>Configures the external pump PID gains.</summary>
    public static OpenTECCommand PumpPid(double kp, double ki, double kd)
        => OpenTECCommand.Create()
            .Set(CommandKeys.PumpPidKp, kp)
            .Set(CommandKeys.PumpPidKi, ki)
            .Set(CommandKeys.PumpPidKd, kd);

    /// <summary>Sets the VEML7700 integration-time code (0..5) on the active IT slot.</summary>
    public static OpenTECCommand BiomassIt(int integrationCode)
    {
        if (integrationCode is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(integrationCode));
        }
        return OpenTECCommand.Create().Set(CommandKeys.BiomassIt, integrationCode);
    }

    /// <summary>Sets LED PWM drive (0-100%) on biomass sensor node.</summary>
    public static OpenTECCommand BiomassPwm(double pwm)
    {
        if (!double.IsFinite(pwm) || pwm is < 0.0 or > 100.0)
        {
            throw new ArgumentOutOfRangeException(nameof(pwm));
        }
        return OpenTECCommand.Create().Set(CommandKeys.BiomassPwm, pwm);
    }

    /// <summary>Sets the combined optical gear (IT index * 8 + PWM index, 0..31).</summary>
    public static OpenTECCommand BiomassGear(int gear)
    {
        if (gear is < 0 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(gear));
        }
        return OpenTECCommand.Create().Set(CommandKeys.BiomassGear, gear);
    }

    /// <summary>Sets EMA smoothing factor (0.0-1.0) on biomass sensor node.</summary>
    public static OpenTECCommand BiomassEma(double ema)
    {
        if (!double.IsFinite(ema) || ema is < 0.01 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(ema));
        }
        return OpenTECCommand.Create().Set(CommandKeys.BiomassEma, ema);
    }

    /// <summary>Sets acquisition probe period in milliseconds on biomass sensor node.</summary>
    public static OpenTECCommand BiomassProbePeriod(int probePeriodMs)
    {
        if (probePeriodMs is < 100 or > 3_600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(probePeriodMs));
        }
        return OpenTECCommand.Create().Set(CommandKeys.BiomassProbePeriodMs, probePeriodMs);
    }

    /// <summary>
    /// Auto-range on the biomass node: <c>{"biomassAutoRange":"auto"|"manual"}</c>. The Hub
    /// translates it to the node's <c>auto</c>/<c>manual</c> command (one command per revision).
    /// </summary>
    /// <remarks>
    /// The node persists the choice and does not echo it, so the card tracks what it sent.
    /// <c>biomassGear</c> implies <c>manual</c> on a v11.1 node - selecting a gear is asking for
    /// that gear - so <see cref="BiomassTuning"/> with a gear leaves the node in manual mode.
    /// </remarks>
    public static OpenTECCommand BiomassAutoRange(bool enabled)
        => OpenTECCommand.Create().Set(CommandKeys.BiomassAutoRange, enabled ? "auto" : "manual");

    /// <summary>
    /// Builds a list of discrete commands for biomass tuning parameters, emitting one frame per
    /// parameter to comply with Hub mailbox single-command-per-revision constraints (§3.5).
    /// </summary>
    public static IReadOnlyList<OpenTECCommand> BiomassTuning(
        int? it = null,
        double? pwm = null,
        int? gear = null,
        double? ema = null,
        int? probePeriodMs = null)
    {
        var list = new List<OpenTECCommand>();
        // set_it and set_pwm modify the currently selected table slots. Select the
        // combined gear first so both writes target the slots the operator requested.
        if (gear.HasValue)
        {
            list.Add(BiomassGear(gear.Value));
        }
        if (it.HasValue)
        {
            list.Add(BiomassIt(it.Value));
        }
        if (pwm.HasValue)
        {
            list.Add(BiomassPwm(pwm.Value));
        }
        if (ema.HasValue)
        {
            list.Add(BiomassEma(ema.Value));
        }
        if (probePeriodMs.HasValue)
        {
            list.Add(BiomassProbePeriod(probePeriodMs.Value));
        }
        return list;
    }

    // ── External bath / thermal cascade (Hub 10.5.1) ───────────────────────

    /// <summary>Selects the reactor temperature route: UART module or external C404 bath.</summary>
    public static OpenTECCommand TemperatureRoute(bool externalBath)
        => OpenTECCommand.Create().Set(CommandKeys.TempControlMode, externalBath ? 1 : 0);

    public static OpenTECCommand BathCommunication(bool enabled)
        => OpenTECCommand.Create().Set(CommandKeys.BathComm, enabled ? 1 : 0);

    public static OpenTECCommand BathMode(bool automatic)
        => OpenTECCommand.Create().Set(CommandKeys.BathMode, automatic ? "auto" : "manual");

    /// <summary>Synchronizes the C404 display setpoint; Hub accepts 0..100 °C.</summary>
    public static OpenTECCommand BathSynchronize(double setpointC)
    {
        ValidateFiniteRange(setpointC, 0.0, 100.0, nameof(setpointC));
        return OpenTECCommand.Create().Set(CommandKeys.BathSync, setpointC);
    }

    public static OpenTECCommand BathAbort()
        => OpenTECCommand.Create().Set(CommandKeys.BathAbort, 1);

    public static OpenTECCommand BathCascadeReset()
        => OpenTECCommand.Create().Set(CommandKeys.BathCascadeReset, 1);

    /// <summary>Applies one or more cascade parameters as a validated transaction.</summary>
    public static OpenTECCommand BathCascadeTuning(
        double? kp = null, double? tiS = null, double? biasC = null, int? periodMs = null,
        double? filterS = null, int? commandMinMs = null, double? commandBandC = null,
        double? slewCMin = null, double? offsetHighC = null, double? offsetLowC = null,
        double? outputMinC = null, double? outputMaxC = null)
    {
        if (kp is null && tiS is null && biasC is null && periodMs is null && filterS is null &&
            commandMinMs is null && commandBandC is null && slewCMin is null &&
            offsetHighC is null && offsetLowC is null && outputMinC is null && outputMaxC is null)
        {
            throw new ArgumentException("Pelo menos um parâmetro da cascata deve ser fornecido.");
        }

        var values = new[] { kp, tiS, biasC, filterS, commandBandC, slewCMin,
            offsetHighC, offsetLowC, outputMinC, outputMaxC };
        if (values.Any(v => v is { } value && !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(kp), "Parâmetros da cascata devem ser finitos.");
        }
        if (periodMs is <= 0 or > 600_000 || commandMinMs is <= 0 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(periodMs), "Temporizações da cascata fora da faixa.");
        }

        var command = OpenTECCommand.Create();
        if (kp is { } v1) command.Set(CommandKeys.BathCascadeKp, v1);
        if (tiS is { } v2) command.Set(CommandKeys.BathCascadeTiS, v2);
        if (biasC is { } v3) command.Set(CommandKeys.BathCascadeBiasC, v3);
        if (periodMs is { } v4) command.Set(CommandKeys.BathCascadePeriodMs, v4);
        if (filterS is { } v5) command.Set(CommandKeys.BathCascadeFilterS, v5);
        if (commandMinMs is { } v6) command.Set(CommandKeys.BathCascadeCommandMinMs, v6);
        if (commandBandC is { } v7) command.Set(CommandKeys.BathCascadeCommandBandC, v7);
        if (slewCMin is { } v8) command.Set(CommandKeys.BathCascadeSlewCMin, v8);
        if (offsetHighC is { } v9) command.Set(CommandKeys.BathCascadeOffsetHighC, v9);
        if (offsetLowC is { } v10) command.Set(CommandKeys.BathCascadeOffsetLowC, v10);
        if (outputMinC is { } v11) command.Set(CommandKeys.BathCascadeOutputMinC, v11);
        if (outputMaxC is { } v12) command.Set(CommandKeys.BathCascadeOutputMaxC, v12);
        return command;
    }

    private static void ValidateFiniteRange(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Valor deve estar entre {min} e {max}.");
        }
    }

    /// <summary>Requests cached health for one external node, or all five over USB.</summary>
    public static OpenTECCommand NodeDiag(string device)
    {
        var valid = device == "all" || device is "distance" or "agitator" or "pump" or "flowmeter" or "biomass";
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(device), device, "Nó externo desconhecido.");
        }

        return OpenTECCommand.Create().Set(CommandKeys.NodeDiag, device);
    }
}
