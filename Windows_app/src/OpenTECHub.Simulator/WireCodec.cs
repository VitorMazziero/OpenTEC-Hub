using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTECHub.Protocol;

namespace OpenTECHub.Simulator;

/// <summary>
/// Builds telemetry frames and applies incoming commands, per
/// <c>docs/PROTOCOL.md</c>.
/// </summary>
/// <remarks>
/// The field calibration is <b>inverted</b> here so the simulator emits raw ADC
/// counts, exactly as the firmware does. Emitting engineering units directly would
/// leave the app's entire calibration and spike-filter path untested - and a wrong
/// coefficient is precisely the defect that would then survive to the lab.
/// </remarks>
public static class WireCodec
{
    // Field calibration (v.6 preferences.json), used in reverse.
    private const double OxygenA = 0.0305473419314;
    private const double OxygenB = -25.09136520919;
    private const double PHSlope = 0.0005012405704;
    private const double PHIntercept = -0.600385955239;

    /// <summary>Engineering value to the raw count that decodes back to it.</summary>
    private static double ToOxygenRaw(double percent) => (percent - OxygenB) / OxygenA;

    private static double ToPHRaw(double ph) => (ph - PHIntercept) / PHSlope;

    /// <summary>
    /// Serialises one telemetry frame.
    /// </summary>
    /// <remarks>
    /// Absent sensors emit the <c>-1</c> sentinel rather than being omitted, matching
    /// what a bare board sends. Keys the firmware does not send with no module
    /// attached are genuinely omitted, so the app's "missing key means no update"
    /// handling is exercised too.
    /// </remarks>
    public static string BuildTelemetry(DeviceModel model)
    {
        var online = model.SensorModuleOnline;

        var buffer = new StringBuilder(320);
        buffer.Append('{');

        // The Hub opens every frame with its own version (Telemetry.h); a legacy Hub has neither key.
        if (model.Scenario != Scenario.LegacyHub)
        {
            buffer.Append("\"HubFirmwareVersion\":\"").Append(DeviceModel.HubFirmwareVersion).Append("\",");
            buffer.Append("\"HubProtocolVersion\":").Append(DeviceModel.HubProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append(',');
        }
        Append(buffer, "Time", model.UptimeSeconds, 1);
        Append(buffer, "Tempval", online ? model.ReadTemperature() : -1.0, 2);

        // The probes report raw counts; the app owns the calibration.
        Append(buffer, "Oxyval", online ? ToOxygenRaw(model.ReadOxygenPercent()) : -1.0, 1);
        Append(buffer, "pHval", online ? ToPHRaw(model.ReadPH()) : -1.0, 1);

        Append(buffer, "Pressure", online ? model.ReadPressure() : 0.0, 2);
        Append(buffer, "FlowRate", online ? model.ReadFlow() : -1.0, 3);
        Append(buffer, "FlowSetpoint", online ? model.FlowSetpoint : -1.0, 2);
        Append(buffer, "FlowVoltage", online ? model.ReadFlow() * 0.0109 : 0.0, 4);
        Append(buffer, "Antifoam", online ? 0.0 : -1.0, 1);

        if (online)
        {
            Append(buffer, "BiomassAbs", model.ReadBiomass(), 4);
            if (model.ExternalNodesOnline && model.BiomassEnabled)
            {
                // Telemetry.h emits Raw/IT/PWM beside Abs whenever the last biomass push was valid.
                AppendInt(buffer, "BiomassRaw", (int)(model.ReadBiomass() * 65535.0 / 4.0));
                AppendInt(buffer, "BiomassIT", model.BiomassIntegrationTimeMs);
                Append(buffer, "BiomassPWM", model.BiomassPwmPercent, 1);
            }
            AppendInt(buffer, "Valve1", model.Valve1);
            AppendInt(buffer, "Valve2", model.Valve2);
            AppendInt(buffer, "ValveFlow", model.MainLineClosed ? 1 : 0);
            AppendInt(buffer, "FlowCommandId", model.FlowCommandId);
            AppendInt(buffer, "FlowCommandAck", model.FlowCommandAck);
            AppendInt(buffer, "FlowCommandDeliveries", model.FlowCommandDeliveries);
            AppendBool(buffer, "FlowCommandPending", model.FlowCommandPending);
            AppendBool(buffer, "FlowmeterOnline", model.ExternalNodesOnline);
            AppendBool(buffer, "FlowControlEnabled", model.FlowmeterEnabled);

            if (model.ExternalNodesOnline && model.Scenario != Scenario.LegacyHub)
            {
                Append(buffer, "FlowKp", model.FlowKp, 3);
                Append(buffer, "FlowKi", model.FlowKi, 4);
                Append(buffer, "FlowFfGain", model.FlowFfGain, 3);
                Append(buffer, "FlowFfOffset", model.FlowFfOffset, 3);
                Append(buffer, "FlowRampRate", model.FlowRampRate, 2);
                Append(buffer, "FlowOutput", model.FlowOutput, 3);
                Append(buffer, "FlowSetpointCorrected", model.FlowSetpointCorrected, 2);
                AppendInt(buffer, "FlowmeterBootId", (int)model.FlowmeterBootId);
            }
        }

        AppendExternalDevices(buffer, model);
        AppendNodeIdentity(buffer, model);

        AppendBool(buffer, "SensorCommOK", online);

        // Trim the trailing separator.
        if (buffer[^1] == ',')
        {
            buffer.Length--;
        }

        return buffer.Append('}').ToString();
    }

    /// <summary>
    /// The external-device presence contract, as a patched Hub publishes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presence and routing go out on <b>every</b> frame, true or false. That is the whole point of
    /// them: the absence of one of these keys means "this Hub predates it", which is a different
    /// thing from <c>false</c>, and an app that cannot tell those apart has to treat an old Hub as
    /// a shelf of failed devices.
    /// </para>
    /// <para>
    /// Value blocks follow the Hub's own rule and are emitted only while the node is present, so
    /// the app's invalidation path is exercised rather than assumed.
    /// </para>
    /// </remarks>
    private static void AppendExternalDevices(StringBuilder buffer, DeviceModel model)
    {
        var present = model.ExternalNodesOnline;

        AppendBool(buffer, "BiomassOnline", present && model.BiomassEnabled);
        AppendBool(buffer, "BiomassCommEnabled", model.RoutingEcho(model.BiomassEnabled));
        AppendBool(buffer, "BiomassCommandPending", model.ConsumeBiomassCommandPending());
        if (present && model.BiomassEnabled && model.Scenario != Scenario.LegacyHub)
        {
            AppendInt(buffer, "BiomassGear", model.BiomassGear);
            Append(buffer, "BiomassEma", model.BiomassEma, 3);
            AppendInt(buffer, "BiomassProbePeriodMs", model.BiomassProbePeriodMs);
        }

        AppendBool(buffer, "DistanceOnline", present && model.DistanceSensorEnabled);
        AppendBool(buffer, "DistanceCommEnabled", model.RoutingEcho(model.DistanceSensorEnabled));
        if (model.Scenario != Scenario.LegacyHub)
        {
            AppendBool(buffer, "DistanceCommandPending", model.ConsumeDistanceCommandPending());
        }
        if (present && model.DistanceSensorEnabled)
        {
            Append(buffer, "Distance", 118.0, 2);
            if (model.Scenario != Scenario.LegacyHub)
            {
                Append(buffer, "DistanceOffsetMm", model.DistanceOffsetMm, 2);
                AppendInt(buffer, "DistanceSamplePeriodMs", model.DistanceSamplePeriodMs);
                AppendInt(buffer, "DistanceSendPeriodMs", model.DistanceSendPeriodMs);
            }
        }

        AppendBool(buffer, "PumpOnline", present && model.PumpEnabled);
        AppendBool(buffer, "PumpCommEnabled", model.RoutingEcho(model.PumpEnabled));
        AppendBool(buffer, "PumpCommandPending", false);
        if (present && model.PumpEnabled)
        {
            AppendInt(buffer, "PumpMode", model.PumpMode);
            AppendInt(buffer, "PumpPWM", 180);
            Append(buffer, "PumpSpeed", model.PumpManualSpeed > 0.0 ? model.PumpManualSpeed : 42.5, 1);
            Append(buffer, "PumpFlow", model.PumpManualSpeed > 0.0 ? model.PumpManualFlowMlMin : 1.25, 3);
            Append(buffer, "PumpVol", model.PumpVolume, 3);
            Append(buffer, "PumpTargetVol", model.PumpVolume, 3);
            AppendBool(buffer, "PumpActive", model.PumpMode > 0);
            AppendBool(buffer, "PumpWaiting", false);
            if (model.Scenario != Scenario.LegacyHub)
            {
                Append(buffer, "PumpSlope", model.PumpSlope, 4);
                Append(buffer, "PumpIntercept", model.PumpIntercept, 4);
                Append(buffer, "PumpPidKp", model.PumpPidKp, 4);
                Append(buffer, "PumpPidKi", model.PumpPidKi, 4);
                Append(buffer, "PumpPidKd", model.PumpPidKd, 4);
                AppendBool(buffer, "PumpPotEnabled", model.PumpPotEnabled);
            }
        }

        // The agitator has no routing flag on the wire: the Hub forwards to it unconditionally.
        AppendBool(buffer, "AgitatorOnline", present);
        AppendBool(buffer, "AgitatorCommandPending", false);
        if (present)
        {
            Append(buffer, "AgitatorPercent", model.AgitatorPercent, 1);
            AppendInt(buffer, "AgitatorDir", model.AgitatorClockwise ? 1 : 0);
            AppendBool(buffer, "AgitatorPotActive", model.AgitatorPotActive);
            AppendString(buffer, "AgitatorSource", model.AgitatorPotActive ? "Pot" : "Hub");
        }

        AppendServo(buffer, model);
    }

    /// <summary>
    /// The ASDA-B2 servo node, in the two groups the Hub actually publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <see cref="Scenario.LegacyHub"/> nothing at all is emitted, which is the shape
    /// of every Hub built before the servo contract. The app must read that as <i>nothing
    /// has been claimed</i>, not as a failed device.
    /// </para>
    /// <para>
    /// Otherwise the four presence and queue keys go out on <b>every</b> frame, and the ten
    /// measurements only when there is a publishable sample - fresh presence <b>and</b>
    /// routing on. Their absence is meaningful, so they are genuinely omitted rather than
    /// sent as zeros: a zero rpm is a stopped motor, and the app has to be able to tell that
    /// from a node that is not reporting.
    /// </para>
    /// </remarks>
    private static void AppendServo(StringBuilder buffer, DeviceModel model)
    {
        if (!model.PublishesServo)
        {
            return;
        }

        AppendBool(buffer, "ServoOnline", model.ServoNodePresent);
        AppendBool(buffer, "ServoCommEnabled", model.RoutingEcho(model.ServoEnabled));
        AppendBool(buffer, "ServoCommandPending", model.ServoCommandPending);
        AppendInt(buffer, "ServoCommandQueueDepth", model.ServoCommandQueueDepth);
        AppendBool(buffer, "MotorControlViaModbus", model.MotorControlViaModbus);
        AppendInt(buffer, "ServoMotorRouteAck", model.ServoMotorRouteAck);

        if (!model.ServoSamplePublishable)
        {
            return;
        }

        Append(buffer, "ServoRpm", model.ServoRpm, 1);
        Append(buffer, "ServoTorquePct", model.ServoTorquePct, 1);
        Append(buffer, "ServoTorqueNm", model.ServoTorqueNm, 4);
        Append(buffer, "ServoLoadPct", model.ServoLoadPct, 1);
        Append(buffer, "ServoPowerW", model.ServoPowerW, 2);
        Append(buffer, "ServoEnergyWh", model.ServoEnergyWh, 6);
        AppendInt(buffer, "ServoState", model.ServoState);
        AppendInt(buffer, "ServoAlarm", model.ServoAlarmCode);
        AppendInt(buffer, "ServoCommOk", (int)model.ServoCommOk);
        AppendInt(buffer, "ServoCommErr", (int)model.ServoCommErr);
    }

    /// <summary>
    /// Applies a command object to the model.
    /// </summary>
    /// <returns>False when the payload was not parseable JSON.</returns>
    public static bool ApplyCommand(DeviceModel model, string json, out bool wasHandshake)
    {
        wasHandshake = false;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty(CommandKeys.ComTest, out _))
        {
            wasHandshake = true;
            return true;
        }

        if (TryDouble(root, CommandKeys.TempSetpoint, out var temperature))
        {
            model.TemperatureSetpoint = temperature;
        }

        if (TryDouble(root, CommandKeys.MotorSetpoint, out var rpm))
        {
            model.MotorRpm = (int)rpm;
        }

        if (TryDouble(root, CommandKeys.MotorControlMode, out var motorControlMode) &&
            motorControlMode is >= 0 and <= 1)
        {
            var viaModbus = motorControlMode != 0;
            if (model.MotorControlViaModbus != viaModbus)
            {
                model.MotorControlViaModbus = viaModbus;
                model.MotorRpm = 0;
            }
        }

        if (TryDouble(root, CommandKeys.OxygenMonitor, out var oxygen))
        {
            model.OxygenSetpoint = oxygen;
        }

        if (TryDouble(root, CommandKeys.PressureReference, out var pressure))
        {
            model.PressureReference = pressure;
        }

        if (TryDouble(root, CommandKeys.PHSetpoint, out var ph))
        {
            model.PHSetpoint = ph;
        }

        if (TryDouble(root, CommandKeys.PHError, out var phError))
        {
            model.PHInactiveBand = phError;
        }

        if (TryDouble(root, CommandKeys.PHOperation, out var phOperation))
        {
            model.PHOperationSeconds = (int)phOperation;
        }

        if (TryDouble(root, CommandKeys.PHMix, out var phMix))
        {
            model.PHMixSeconds = (int)phMix;
        }

        if (TryDouble(root, CommandKeys.PHIntensity, out var phIntensity))
        {
            model.PHIntensity = phIntensity;
        }

        if (TryDouble(root, CommandKeys.PHCal, out var phCalibrated))
        {
            model.PHDisplayValue = phCalibrated;
        }

        if (TryDouble(root, CommandKeys.K1, out var k1))
        {
            model.FlowK1 = k1;
        }

        if (TryDouble(root, CommandKeys.F1, out var f1))
        {
            model.FlowF1 = f1;
        }

        if (TryDouble(root, CommandKeys.C1, out var c1))
        {
            model.FlowC1 = c1;
        }

        if (TryDouble(root, CommandKeys.K2, out var k2))
        {
            model.FlowK2 = k2;
        }

        if (TryDouble(root, CommandKeys.F2, out var f2))
        {
            model.FlowF2 = f2;
        }

        if (TryDouble(root, CommandKeys.C2, out var c2))
        {
            model.FlowC2 = c2;
        }

        if (TryDouble(root, CommandKeys.MaxFlow, out var maxFlow))
        {
            model.MaxFlow = maxFlow;
        }

        if (TryDouble(root, CommandKeys.FlowmeterComm, out var flowEnabled))
        {
            model.FlowmeterEnabled = flowEnabled != 0;
        }

        if (TryDouble(root, CommandKeys.BiomassComm, out var biomassEnabled))
        {
            model.BiomassEnabled = biomassEnabled != 0;
        }

        if (TryDouble(root, CommandKeys.PumpComm, out var pumpEnabled))
        {
            model.PumpEnabled = pumpEnabled != 0;
        }

        if (TryDouble(root, CommandKeys.DistanceSensorComm, out var distanceEnabled))
        {
            model.DistanceSensorEnabled = distanceEnabled != 0;
        }

        if (TryDouble(root, CommandKeys.ServoComm, out var servoEnabled))
        {
            model.ServoEnabled = servoEnabled != 0;
        }

        // Only the value 1 is a command. The Hub queues nothing for zero, and an app that
        // expected otherwise would sit waiting for an energy reset that was never asked for.
        if (TryDouble(root, CommandKeys.ResetServoEnergy, out var resetEnergy) && resetEnergy != 0)
        {
            // Queued, not applied: the node collects it on its next 2 s pull. Watching the
            // energy fall a frame or two later is the only confirmation this link offers,
            // because it carries no acknowledgement at all.
            model.EnqueueServoCommand("reset_energy");
        }

        // Out of range is refused outright - nothing is queued, and the previous interval
        // stands. The real Hub says so on its own serial port, which nobody is reading, so
        // an app that clamped instead would leave the operator with an interval they never
        // chose and no sign of it.
        if (TryDouble(root, CommandKeys.ServoPollMs, out var pollMs) &&
            model.SetServoPollMs((int)pollMs))
        {
            model.EnqueueServoCommand("poll_ms");
        }

        // The Hub forwards the pump block only while routing is on, so mode:0 arriving in the
        // same frame as pumpComm:0 is dropped - which is exactly why the app sends two.
        if (model.PumpEnabled && TryDouble(root, CommandKeys.Mode, out var pumpMode))
        {
            model.PumpMode = (int)pumpMode;
        }

        // Manual speed: the node drops to idle and holds S; zero stops it. The simulator
        // mirrors that so the volumetric calibration can be rehearsed without a bench.
        if (model.PumpEnabled && TryDouble(root, CommandKeys.PumpManualSpeed, out var manualSpeed))
        {
            model.PumpMode = 0;
            model.PumpManualSpeed = Math.Clamp(manualSpeed, 0.0, 1000.0);
            model.PumpPotEnabled = false;
        }

        if (model.PumpEnabled && TryDouble(root, CommandKeys.PumpPotentiometers, out var potEnabled))
        {
            model.PumpPotEnabled = potEnabled != 0;
            if (model.PumpPotEnabled)
            {
                model.PumpManualSpeed = 0.0;
            }
        }

        if (TryDouble(root, CommandKeys.AgitatorPercent, out var agitatorPercent))
        {
            model.AgitatorPercent = agitatorPercent;
        }

        if (TryDouble(root, CommandKeys.AgitatorDir, out var agitatorDir))
        {
            model.AgitatorClockwise = agitatorDir != 0;
        }

        if (TryDouble(root, CommandKeys.AgitatorOn, out var agitatorOn))
        {
            if (agitatorOn == 0)
            {
                model.AgitatorPercent = 0.0;
            }
        }

        if (TryDouble(root, CommandKeys.AgitatorReEnablePot, out var reEnablePot))
        {
            model.AgitatorPotActive = reEnablePot != 0;
        }

        if (TryDouble(root, CommandKeys.FlowSetpoint, out var flow))
        {
            model.FlowSetpoint = flow;
            model.NoteFlowCommand();
        }

        // v_Flow is the active-high main shutoff: 1 = line closed (PROTOCOL §3.1).
        if (TryDouble(root, CommandKeys.V_Flow, out var vent))
        {
            model.MainLineClosed = vent != 0;
        }

        if (TryDouble(root, CommandKeys.Valve1, out var valve1) || TryDouble(root, "Valve1", out valve1))
        {
            model.Valve1 = (int)valve1;
        }

        if (TryDouble(root, CommandKeys.Valve2, out var valve2) || TryDouble(root, "Valve2", out valve2))
        {
            model.Valve2 = (int)valve2;
        }

        if (TryDouble(root, CommandKeys.DataDelay, out var delay))
        {
            model.DataDelayMs = Math.Clamp((int)delay, 100, 60_000);
        }

        // External-node commands (Hub 10.2)
        if (TryDouble(root, CommandKeys.DistanceOffsetMm, out var distOffset))
        {
            model.DistanceOffsetMm = distOffset;
            model.DistanceCommandPending = true;
        }
        if (TryDouble(root, CommandKeys.DistanceSamplePeriodMs, out var distSample))
        {
            model.DistanceSamplePeriodMs = (int)distSample;
            model.DistanceCommandPending = true;
        }
        if (TryDouble(root, CommandKeys.DistanceSendPeriodMs, out var distSend))
        {
            model.DistanceSendPeriodMs = (int)distSend;
            model.DistanceCommandPending = true;
        }
        if (TryDouble(root, CommandKeys.DistanceResetNvs, out var distReset) && distReset != 0)
        {
            model.DistanceOffsetMm = 20.0;
            model.DistanceSamplePeriodMs = 500;
            model.DistanceSendPeriodMs = 1000;
            model.DistanceCommandPending = true;
        }

        if (TryDouble(root, CommandKeys.FlowKp, out var flowKp))
        {
            model.FlowKp = flowKp;
        }
        if (TryDouble(root, CommandKeys.FlowKi, out var flowKi))
        {
            model.FlowKi = flowKi;
        }
        if (TryDouble(root, CommandKeys.FlowFfGain, out var flowFfGain))
        {
            model.FlowFfGain = flowFfGain;
        }
        if (TryDouble(root, CommandKeys.FlowFfOffset, out var flowFfOffset))
        {
            model.FlowFfOffset = flowFfOffset;
        }
        if (TryDouble(root, CommandKeys.FlowRampRate, out var flowRampRate))
        {
            model.FlowRampRate = flowRampRate;
        }

        if (root.TryGetProperty(CommandKeys.PumpCommand, out var pumpCmdProp) &&
            pumpCmdProp.ValueKind == JsonValueKind.String)
        {
            var cmdStr = pumpCmdProp.GetString();
            if (cmdStr == "reset_volume")
            {
                model.ResetPumpVolume();
            }
        }
        if (TryDouble(root, CommandKeys.PumpSlope, out var pumpSlope))
        {
            model.PumpSlope = pumpSlope;
        }
        if (TryDouble(root, CommandKeys.PumpIntercept, out var pumpIntercept))
        {
            model.PumpIntercept = pumpIntercept;
        }
        if (TryDouble(root, CommandKeys.PumpPidKp, out var pumpPidKp))
        {
            model.PumpPidKp = pumpPidKp;
        }
        if (TryDouble(root, CommandKeys.PumpPidKi, out var pumpPidKi))
        {
            model.PumpPidKi = pumpPidKi;
        }
        if (TryDouble(root, CommandKeys.PumpPidKd, out var pumpPidKd))
        {
            model.PumpPidKd = pumpPidKd;
        }

        if (TryDouble(root, CommandKeys.BiomassIt, out var bioIt))
        {
            model.BiomassCommandPending = true;
            model.BiomassIntegrationTimeMs = (int)bioIt switch
            {
                0 => 25,
                1 => 50,
                2 => 100,
                3 => 200,
                4 => 400,
                5 => 800,
                _ => model.BiomassIntegrationTimeMs
            };
        }
        if (TryDouble(root, CommandKeys.BiomassPwm, out var bioPwm))
        {
            model.BiomassCommandPending = true;
            model.BiomassPwmPercent = bioPwm;
        }
        if (TryDouble(root, CommandKeys.BiomassGear, out var bioGear))
        {
            model.BiomassCommandPending = true;
            model.BiomassGear = (int)bioGear;
        }
        if (TryDouble(root, CommandKeys.BiomassEma, out var bioEma))
        {
            model.BiomassCommandPending = true;
            model.BiomassEma = bioEma;
        }
        if (TryDouble(root, CommandKeys.BiomassProbePeriodMs, out var bioProbeMs))
        {
            model.BiomassCommandPending = true;
            model.BiomassProbePeriodMs = (int)bioProbeMs;
        }

        return true;
    }

    private static bool TryDouble(JsonElement root, string key, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(key, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            JsonValueKind.True => (value = 1) == 1,
            JsonValueKind.False => (value = 0) == 0,
            _ => false,
        };
    }

    // Invariant formatting throughout: the wire never carries a locale.
    private static void Append(StringBuilder buffer, string key, double value, int decimals)
        => buffer.Append('"').Append(key).Append("\":")
                 .Append(value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                        CultureInfo.InvariantCulture))
                 .Append(',');

    private static void AppendInt(StringBuilder buffer, string key, int value)
        => buffer.Append('"').Append(key).Append("\":")
                 .Append(value.ToString(CultureInfo.InvariantCulture))
                 .Append(',');

    /// <summary>
    /// The Hub 10.1 node registry: <c>*IP</c> on every frame (<c>0.0.0.0</c> = never seen) and
    /// <c>*NodeVer</c>/<c>*NodeMac</c> only once the node has registered - the same conditional
    /// rule the real Hub applies so an unregistered node costs the frame nothing.
    /// </summary>
    private static void AppendNodeIdentity(StringBuilder buffer, DeviceModel model)
    {
        if (!model.PublishesNodeIdentity)
        {
            return;
        }

        foreach (var (device, prefix) in DeviceModel.RegistryNodes)
        {
            AppendString(buffer, prefix + "IP", model.NodeIp(device));
            if (model.NodeRegistered(device))
            {
                AppendString(buffer, prefix + "NodeVer", DeviceModel.NodeVersion(device));
                AppendString(buffer, prefix + "NodeMac", DeviceModel.NodeMac(device));
            }
        }
    }

    private static void AppendBool(StringBuilder buffer, string key, bool value)
        => buffer.Append('"').Append(key).Append("\":")
                 .Append(value ? "true" : "false")
                 .Append(',');

    /// <summary>A quoted string value, as the Hub emits AgitatorSource and FlowCommandSource.</summary>
    private static void AppendString(StringBuilder buffer, string key, string value)
        => buffer.Append('"').Append(key).Append("\":\"")
                 .Append(value)
                 .Append("\",");
}
