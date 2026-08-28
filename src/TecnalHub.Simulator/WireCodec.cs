using System.Globalization;
using System.Text;
using System.Text.Json;
using TecnalHub.Protocol;

namespace TecnalHub.Simulator;

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
            AppendInt(buffer, "Valve1", model.Valve1);
            AppendInt(buffer, "Valve2", model.Valve2);
            AppendInt(buffer, "ValveFlow", model.VentValveOpen ? 1 : 0);
            AppendInt(buffer, "FlowCommandId", model.FlowCommandId);
            AppendInt(buffer, "FlowCommandAck", model.FlowCommandAck);
            AppendInt(buffer, "FlowCommandDeliveries", model.FlowCommandDeliveries);
            AppendBool(buffer, "FlowmeterOnline", true);
            AppendBool(buffer, "FlowControlEnabled", model.FlowmeterEnabled);
        }

        AppendBool(buffer, "SensorCommOK", online);

        // Trim the trailing separator.
        if (buffer[^1] == ',')
        {
            buffer.Length--;
        }

        return buffer.Append('}').ToString();
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

        if (TryDouble(root, CommandKeys.FlowSetpoint, out var flow))
        {
            model.FlowSetpoint = flow;
            model.NoteFlowCommand();
        }

        // v_Flow is inverted: 1 means the vent is open because flow is zero.
        if (TryDouble(root, CommandKeys.V_Flow, out var vent))
        {
            model.VentValveOpen = vent != 0;
        }

        if (TryDouble(root, CommandKeys.Valve1, out var valve1))
        {
            model.Valve1 = (int)valve1;
        }

        if (TryDouble(root, CommandKeys.Valve2, out var valve2))
        {
            model.Valve2 = (int)valve2;
        }

        if (TryDouble(root, CommandKeys.DataDelay, out var delay))
        {
            model.DataDelayMs = Math.Clamp((int)delay, 100, 60_000);
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

    private static void AppendBool(StringBuilder buffer, string key, bool value)
        => buffer.Append('"').Append(key).Append("\":")
                 .Append(value ? "true" : "false")
                 .Append(',');
}
