using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenTECHub.Harness;
using OpenTECHub.Protocol;

// ---------------------------------------------------------------------------
// Phase 0 hardware harness.
//
// Purpose: prove the C# protocol stack talks to a real ESP32-S3 over both
// transports, and capture a wire trace that can be byte-compared against v.6's
// command_logs/ output. That comparison is the Phase 0 exit criterion
// (docs/ROADMAP.md).
//
//   opentec-harness ports
//   opentec-harness usb [COM7]
//   opentec-harness wifi [192.168.4.1]
//
// Deliberately runs under the machine's real culture: if a formatting bug can
// emit "6,98" on a pt-BR system, it must show up here rather than in the lab.
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var target = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;

// --for <seconds> runs headless for a fixed period instead of reading stdin, so the
// harness works from a script or CI and can be used for the Phase 0 soak test.
var probe = args.Contains("--probe");
TimeSpan? runFor = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--for" &&
        double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
    {
        runFor = TimeSpan.FromSeconds(seconds);
    }
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    }));

switch (mode)
{
    case "ports":
        ListPorts();
        return 0;

    case "usb":
    case "wifi":
        return await RunSessionAsync(mode, target, runFor, probe, loggerFactory).ConfigureAwait(false);

    case "reset-test":
        {
            using var resetCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; resetCts.Cancel(); };
            var experiment = new ResetExperiment(target ?? "COM3", loggerFactory);
            return await experiment.RunAsync(resetCts.Token).ConfigureAwait(false);
        }

    case "wifi-test":
        return await RunWiFiTestAsync(target, loggerFactory).ConfigureAwait(false);

    default:
        PrintUsage();
        return 1;
}

static async Task<int> RunWiFiTestAsync(string? target, ILoggerFactory loggerFactory)
{
    // Fixed, predictable path: the operator has no internet while joined to the
    // device's access point, so the results must be somewhere trivially findable
    // afterwards rather than in a session-scoped temp directory.
    var outputDirectory = Path.Combine(
        Path.GetTempPath(), "opentec-wifi-test",
        DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var suite = new WiFiTestSuite(target ?? "192.168.4.1", outputDirectory, loggerFactory);

    try
    {
        return await suite.RunAsync(cts.Token).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        // Never die without leaving something on disk to read.
        Console.WriteLine();
        Console.WriteLine($"SUITE ABORTED: {ex.GetType().Name}: {ex.Message}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "wifi-abort.txt"),
            ex.ToString(),
            CancellationToken.None).ConfigureAwait(false);
        return 2;
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        OpenTEC-Hub Phase 0 harness

          opentec-harness ports              list serial ports
          opentec-harness usb  [COM7]        connect over USB (auto-probes if omitted)
          opentec-harness wifi [192.168.4.1] connect over Wi-Fi
          opentec-harness wifi-test [ip]     UNATTENDED Wi-Fi validation suite,
                                            writes a report to a temp folder
          opentec-harness reset-test [COM3]  determine what reboots the board on
                                            connect, and whether the settle is needed

        Options:
          --for <seconds>   run headless for a fixed period (no stdin)
          --probe           send no-op commands to verify the write path

        While connected:
          t <degC>    temperature setpoint       m <rpm>   motor setpoint (0, 50-1000)
          f <L/min>   flow setpoint              o <pct>   oxygen monitor setpoint
          p <kPa>     pressure reference         x         flow safe-stop
          d           diagnostics                q         quit

        A wire trace is written to ./harness-trace-<timestamp>.log for byte
        comparison against v.6 command_logs.
        """);
}

static void ListPorts()
{
    var ports = SerialTransport.ListCandidatePorts();
    if (ports.Count == 0)
    {
        Console.WriteLine("No serial ports found.");
        return;
    }

    Console.WriteLine($"{ports.Count} serial port(s):");
    foreach (var port in ports)
    {
        Console.WriteLine($"  {port}");
    }

    Console.WriteLine();
    Console.WriteLine("Adapters ranked as ESP32-like by descriptor keyword: " +
                      string.Join(", ", SerialTransport.EspAdapterKeywords));
}

static async Task<int> RunSessionAsync(
    string mode, string? target, TimeSpan? runFor, bool probe, ILoggerFactory loggerFactory)
{
    using var trace = new WireTrace();
    Console.WriteLine($"Wire trace -> {trace.Path}");
    Console.WriteLine($"Culture    -> {CultureInfo.CurrentCulture.Name} " +
                      $"(decimal separator '{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}')");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await using var manager = new ConnectionManager(loggerFactory: loggerFactory);

    var telemetryCount = 0;
    manager.StateChanged += change =>
    {
        var reason = string.IsNullOrEmpty(change.Reason) ? "" : $" - {change.Reason}";
        Console.WriteLine($"[state] {change.State} {change.Medium} {change.Endpoint}{reason}");
        trace.State(change);
    };

    manager.TelemetryReceived += snapshot =>
    {
        trace.Rx(snapshot);

        // One line per second is plenty; the trace file keeps everything.
        if (++telemetryCount % 4 == 1)
        {
            Console.WriteLine(Format(snapshot));
        }
    };

    manager.DeviceLogReceived += line =>
    {
        Console.WriteLine($"[esp32] {line}");
        trace.DeviceLog(line);
    };

    if (mode == "usb")
    {
        var port = target;
        if (port is null)
        {
            Console.WriteLine("Probing serial ports in parallel...");
            var started = DateTimeOffset.Now;
            port = await SerialTransport
                .ProbePortsAsync(new SerialTransportConfig { PortName = "PROBE" },
                                 preferredPort: null, loggerFactory, cts.Token)
                .ConfigureAwait(false);

            var elapsed = (DateTimeOffset.Now - started).TotalSeconds;
            if (port is null)
            {
                Console.WriteLine(FormattableString.Invariant($"No ESP32 found after {elapsed:F1} s."));
                return 2;
            }

            Console.WriteLine(FormattableString.Invariant($"Found ESP32 on {port} in {elapsed:F1} s."));
        }

        manager.ConnectUsb(new SerialTransportConfig { PortName = port });
    }
    else
    {
        manager.ConnectWiFi(new HttpTransportConfig
        {
            IpAddress = target ?? "192.168.4.1",
        });
    }

    if (probe)
    {
        // Exercises the full write path (buffer -> flush -> transport) without
        // changing anything on the device: dataDelay is set to the value already in
        // the field, and comTest is the same probe the handshake uses. Deliberately
        // avoids actuator setpoints, which have physical consequences.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("--- write-path probe (no-op commands) ---");
        Send(manager, trace, CommandBuilders.Handshake());
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
        Send(manager, trace, CommandBuilders.DataDelay(2000));
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
        Console.WriteLine("--- probe done ---");
        Console.WriteLine();
    }

    if (runFor is { } duration)
    {
        Console.WriteLine(FormattableString.Invariant($"Running headless for {duration.TotalSeconds:F0} s..."));
        try
        {
            await Task.Delay(duration, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }
    }
    else
    {
        await ReadCommandsAsync(manager, trace, cts).ConfigureAwait(false);
    }

    manager.Disconnect();
    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);

    var diagnostics = manager.Diagnostics;
    Console.WriteLine();
    Console.WriteLine("=== session summary ===");
    Console.WriteLine($"frames received : {diagnostics.FramesReceived}");
    Console.WriteLine($"commands sent   : {diagnostics.CommandsSent}");
    Console.WriteLine($"parse failures  : {diagnostics.ParseFailures}");
    Console.WriteLine($"device log lines: {diagnostics.DeviceLogLines}");
    Console.WriteLine($"command acks    : {diagnostics.CommandAcks}");
    Console.WriteLine($"liveness probes : {diagnostics.LivenessProbes}");
    Console.WriteLine($"connect attempts: usb={diagnostics.ConnectAttemptsUsb} wifi={diagnostics.ConnectAttemptsWiFi}");
    Console.WriteLine($"last error      : {(string.IsNullOrEmpty(diagnostics.LastError) ? "-" : diagnostics.LastError)}");
    Console.WriteLine($"trace           : {trace.Path}");

    return diagnostics.FramesReceived > 0 ? 0 : 3;
}

static async Task ReadCommandsAsync(ConnectionManager manager, WireTrace trace, CancellationTokenSource cts)
{
    while (!cts.IsCancellationRequested)
    {
        var line = await Task.Run(Console.ReadLine, CancellationToken.None).ConfigureAwait(false);
        if (line is null)
        {
            return; // stdin closed
        }

        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        // Accept a decimal comma from a pt-BR keyboard; the wire always gets a point.
        var argument = parts.Length > 1 ? parts[1].Replace(',', '.') : null;

        switch (parts[0].ToLowerInvariant())
        {
            case "q":
                cts.Cancel();
                return;

            case "d":
                var d = manager.Diagnostics;
                Console.WriteLine($"[diag] frames={d.FramesReceived} tx={d.CommandsSent} " +
                                  $"parseFail={d.ParseFailures} esp32Log={d.DeviceLogLines} " +
                                  $"rttMs={d.LastWriteMs?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"} " +
                                  $"lastError={(string.IsNullOrEmpty(d.LastError) ? "-" : d.LastError)}");
                break;

            case "t" when TryValue(argument, out var celsius):
                Send(manager, trace, OpenTECCommand.Create()
                    .Set(CommandKeys.TempSetpoint, celsius));
                break;

            case "m" when TryValue(argument, out var rpm):
                Send(manager, trace, OpenTECCommand.Create()
                    .Set(CommandKeys.MotorSetpoint, (int)rpm));
                break;

            case "o" when TryValue(argument, out var percent):
                Send(manager, trace, OpenTECCommand.Create()
                    .Set(CommandKeys.OxygenMonitor, percent));
                break;

            case "p" when TryValue(argument, out var kpa):
                Send(manager, trace, OpenTECCommand.Create()
                    .Set(CommandKeys.PressureReference, kpa));
                break;

            case "f" when TryValue(argument, out var flow):
                Send(manager, trace, CommandBuilders.FlowSetpoint(flow, maxFlow: 50.0));
                break;

            case "x":
                Send(manager, trace, CommandBuilders.FlowSafeStop(maxFlow: 50.0));
                break;

            default:
                Console.WriteLine("? unknown command - see usage");
                break;
        }
    }
}

static bool TryValue(string? text, out double value)
    => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

static void Send(ConnectionManager manager, WireTrace trace, OpenTECCommand command)
{
    var json = command.ToJson();
    Console.WriteLine($"[ tx  ] {json}");
    trace.Tx(json);
    manager.SendCommand(command);
}

static string Format(SensorSnapshot s)
{
    static string N(double v, int decimals = 1)
        => v <= SensorReadings.NotReceived
            ? "  --"
            : v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // Every numeric here goes through N(): a bare $"{value:F2}" would use the
    // machine culture and print "0,05" on this pt-BR box. Display-only, but it is
    // exactly the slip that breaks the wire when it happens in a command builder.
    return $"[ rx  ] t={N(s.TimeMinutes, 2),7}min  T={N(s.Temperature),6}C  " +
           $"O2={N(s.OxygenCalibrated, 2),6}  pH={N(s.PHCalibrated, 2),5}  " +
           $"Q={N(s.FlowRate, 2),5}L/min  P={N(s.Pressure),5}  " +
           $"valves={s.FlowValve1}{s.FlowValve2}{s.FlowValveMain}  " +
           $"sensorOk={(s.SensorCommOk ? "y" : "N")}";
}
