using System.Globalization;
using TecnalHub.Protocol;

namespace TecnalHub.Harness;

/// <summary>
/// Append-only log of everything crossing the wire during a harness session.
/// </summary>
/// <remarks>
/// The Phase 0 exit criterion is that TECNAL-Hub and v.6 emit identical command
/// bytes for the same operator actions (docs/ROADMAP.md). v.6 already writes
/// <c>command_logs/command_log_*.txt</c>; this is the other half of that comparison.
/// <para>
/// Format is one record per line: <c>ISO8601 TAB direction TAB payload</c>, so it
/// can be diffed or loaded into a script without parsing effort.
/// </para>
/// </remarks>
public sealed class WireTrace : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public WireTrace(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Directory.GetCurrentDirectory(),
            $"harness-trace-{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.log");

        _writer = new StreamWriter(Path, append: true) { AutoFlush = true };

        Write("meta", $"session start; culture={CultureInfo.CurrentCulture.Name}; " +
                      $"decimalSeparator='{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}'");
    }

    /// <summary>Full path of the trace file.</summary>
    public string Path { get; }

    /// <summary>Records a command sent to the device, exactly as serialised.</summary>
    public void Tx(string json) => Write("tx", json);

    /// <summary>Records a parsed telemetry frame.</summary>
    public void Rx(SensorSnapshot snapshot) => Write("rx", Describe(snapshot));

    /// <summary>Records a device log line.</summary>
    public void DeviceLog(string line) => Write("esp32", line);

    /// <summary>Records a connection state transition.</summary>
    public void State(ConnectionStateChange change)
        => Write("state", $"{change.State} {change.Medium} {change.Endpoint} {change.Reason}".TrimEnd());

    private void Write(string direction, string payload)
    {
        lock (_gate)
        {
            _writer.WriteLine(
                $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fff}\t{direction}\t{payload}");
        }
    }

    private static string Describe(SensorSnapshot s)
    {
        static string N(double v) =>
            v.ToString("G", CultureInfo.InvariantCulture);

        return string.Join(' ', [
            $"time={N(s.TimeRawSeconds)}",
            $"temp={N(s.Temperature)}",
            $"oxyRaw={N(s.OxygenRaw)}",
            $"oxyCal={N(s.OxygenCalibrated)}",
            $"phRaw={N(s.PHRaw)}",
            $"phCal={N(s.PHCalibrated)}",
            $"pressure={N(s.Pressure)}",
            $"flow={N(s.FlowRate)}",
            $"flowSp={N(s.FlowSetpoint)}",
            $"flowV={N(s.FlowVoltage)}",
            $"valves={s.FlowValve1}/{s.FlowValve2}/{s.FlowValveMain}",
            $"flowOnline={s.FlowmeterOnline}",
            $"cmdId={s.FlowCommandId}",
            $"cmdAck={s.FlowCommandAck}",
            $"cmdDeliveries={s.FlowCommandDeliveries}",
            $"cmdAgeMs={s.FlowCommandAgeMs}",
            $"hubStations={s.HubStations}",
            $"sensorOk={s.SensorCommOk}",
        ]);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Write("meta", "session end");
            _writer.Dispose();
        }
    }
}
