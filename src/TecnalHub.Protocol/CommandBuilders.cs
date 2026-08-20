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
