namespace TecnalHub.Protocol;

/// <summary>
/// Builders for commands whose correct shape is a protocol rule rather than a
/// caller's choice.
/// </summary>
/// <remarks>
/// Anything here exists because getting it wrong by hand is easy and the
/// consequence is on real hardware - an inverted vent flag, or a nitrogen valve
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
    /// <c>v_Flow</c> is <b>inverted</b>: 1 when the setpoint is zero, else 0. It is
    /// computed here so no caller has to remember that.
    /// </remarks>
    /// <param name="setpoint">Target flow, clamped to <paramref name="maxFlow"/>.</param>
    /// <param name="maxFlow">Ceiling reported to the firmware.</param>
    /// <param name="valve1">Auxiliary valve state.</param>
    /// <param name="valve2">Nitrogen valve state.</param>
    public static TecnalCommand FlowSetpoint(
        double setpoint,
        double maxFlow,
        bool valve1 = false,
        bool valve2 = false)
    {
        var clamped = Math.Clamp(setpoint, 0.0, maxFlow);

        return TecnalCommand.Create()
            .Set(CommandKeys.FlowmeterComm, 1)
            .Set(CommandKeys.FlowSetpoint, clamped)
            .Set(CommandKeys.MaxFlow, maxFlow)
            .Set(CommandKeys.Valve1, valve1)
            .Set(CommandKeys.Valve2, valve2)
            .Set(CommandKeys.V_Flow, clamped == 0.0);
    }

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
            .Set(CommandKeys.FlowmeterComm, 0)
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
    public static TecnalCommand FlaskAgitator(bool on, bool automatic, double signedPercent)
    {
        var magnitude = Math.Clamp(Math.Abs(signedPercent), 0.0, 100.0);
        var clockwise = signedPercent >= 0.0;

        return TecnalCommand.Create()
            .Set(CommandKeys.AgitatorOn, on)
            .Set(CommandKeys.AgitatorAuto, automatic)
            .Set(CommandKeys.AgitatorPercent, magnitude)
            .Set(CommandKeys.AgitatorDir, clockwise);
    }

    /// <summary>
    /// Stops the flask agitator — off and out of automatic mode — keeping the operator's
    /// staged magnitude and direction so re-enabling resumes it.
    /// </summary>
    public static TecnalCommand FlaskAgitatorSafeStop(double signedPercent)
        => FlaskAgitator(on: false, automatic: false, signedPercent);

    /// <summary>Re-enables the flask agitator's physical potentiometer. A momentary action.</summary>
    public static TecnalCommand FlaskAgitatorReEnablePot()
        => TecnalCommand.Create().Set(CommandKeys.AgitatorReEnablePot, 1);

    /// <summary>
    /// Flow setpoint shape used while acquiring a calibration point in v.6.
    /// Both optional gas valves are closed and the inverted vent flag is derived.
    /// </summary>
    public static TecnalCommand FlowCalibrationSetpoint(double setpoint)
    {
        var safe = Math.Max(setpoint, 0.0);
        return TecnalCommand.Create()
            .Set(CommandKeys.FlowmeterComm, 1)
            .Set(CommandKeys.FlowSetpoint, safe)
            .Set(CommandKeys.Valve1, false)
            .Set(CommandKeys.Valve2, false)
            .Set(CommandKeys.V_Flow, safe <= 0.0);
    }

    /// <summary>Low-voltage flowmeter polynomial, valid at V &lt;= 0.0545.</summary>
    public static TecnalCommand FlowCalibrationLow(double k, double f, double c)
        => TecnalCommand.Create()
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
            .Set(CommandKeys.FlowmeterComm, 1)
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
