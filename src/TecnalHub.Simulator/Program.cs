using System.Globalization;
using TecnalHub.Simulator;

// ---------------------------------------------------------------------------
// TECNAL device simulator.
//
// Stands in for the ESP32-S3 and the bioreactor behind it, speaking the protocol
// documented in docs/PROTOCOL.md. See docs/SIMULATOR.md.
//
//   tecnal-simulator http [--port 8080]
//   tecnal-simulator serial COM11
//
// Type a scenario name while running to switch behaviour; "?" lists them.
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (mode is not ("http" or "serial"))
{
    PrintUsage();
    return 1;
}

var quiet = args.Contains("--quiet");
var httpPort = ReadOption("--port", 8080);
var dataDelay = ReadOption("--data-delay", 2000);

var model = new DeviceModel
{
    DataDelayMs = dataDelay,
    Scenario = args.Contains("--no-module") ? Scenario.NoModule : Scenario.Normal,
};

if (TryReadScenario(out var startScenario))
{
    model.Scenario = startScenario;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

void Log(string message)
{
    if (!quiet)
    {
        Console.WriteLine(message);
    }
}

Console.WriteLine("TECNAL device simulator");
Console.WriteLine($"  scenario   : {model.Scenario}");
Console.WriteLine($"  dataDelay  : {dataDelay.ToString(CultureInfo.InvariantCulture)} ms");
Console.WriteLine();

IDisposable endpoint;
try
{
    if (mode == "http")
    {
        var http = new HttpEndpoint(model, httpPort, Log);
        http.Start(cts.Token);
        endpoint = http;
    }
    else
    {
        var portName = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : null;

        if (portName is null)
        {
            Console.WriteLine("serial mode needs a port name, e.g. 'serial COM11'.");
            return 1;
        }

        var serial = new SerialEndpoint(model, portName, Log);
        serial.Start(cts.Token);
        endpoint = serial;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Could not start: {ex.GetType().Name}: {ex.Message}");
    if (mode == "serial")
    {
        Console.WriteLine();
        Console.WriteLine("For serial mode you need a virtual COM pair (com0com) and must");
        Console.WriteLine("open the half the app is NOT using. See docs/SIMULATOR.md.");
    }

    return 2;
}

// Advance the process on its own cadence, independent of how often it is polled -
// a bioreactor does not stop reacting because nobody is looking.
var ticker = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        model.Tick();
        try
        {
            await Task.Delay(200, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
}, CancellationToken.None);

Console.WriteLine("Type a scenario name to switch, '?' to list, 'q' to quit.");
Console.WriteLine();

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();
    if (line is null)
    {
        // stdin closed (launched from a script): keep serving until cancelled.
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    var input = line?.Trim() ?? "";
    if (input.Length == 0)
    {
        continue;
    }

    if (input is "q" or "quit" or "exit")
    {
        break;
    }

    if (input is "?" or "help")
    {
        PrintScenarios();
        continue;
    }

    if (Enum.TryParse<Scenario>(input.Replace("-", ""), ignoreCase: true, out var scenario))
    {
        model.Scenario = scenario;
        Console.WriteLine($"scenario -> {scenario}");
    }
    else
    {
        Console.WriteLine($"unknown scenario '{input}'; '?' to list");
    }
}

await cts.CancelAsync().ConfigureAwait(false);
endpoint.Dispose();
await ticker.ConfigureAwait(false);

Console.WriteLine("stopped.");
return 0;

int ReadOption(string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name &&
            int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }

    return fallback;
}

bool TryReadScenario(out Scenario scenario)
{
    scenario = Scenario.Normal;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--scenario")
        {
            return Enum.TryParse(args[i + 1].Replace("-", ""), ignoreCase: true, out scenario);
        }
    }

    return false;
}

static void PrintUsage()
{
    Console.WriteLine("""
        TECNAL device simulator

          tecnal-simulator http [--port 8080]     serve on 127.0.0.1 - NO SETUP NEEDED
          tecnal-simulator serial COM11           serve on a virtual COM pair half

        Options:
          --scenario <name>   start in a scenario
          --data-delay <ms>   telemetry period (default 2000)
          --no-module         start with the sensor module offline, as a bare board
          --quiet             no per-frame console echo

        For http mode, point the app at 127.0.0.1 via the connection chip (Wi-Fi).
        See docs/SIMULATOR.md.
        """);

    PrintScenarios();
}

static void PrintScenarios() => Console.WriteLine("""

        Scenarios:
          normal      healthy process, all sensors reporting
          no-module   SensorCommOK:false, probes at sentinel - a bare board
          stall       link stays up, telemetry frozen (the silence-timeout case)
          dropout     link disappears entirely
          spikes      single-sample outliers, to test the spike filter
          noise       heavy measurement noise
          drift       slow calibration drift
          garbage     malformed frames, to test the parse-failure path
        """);
