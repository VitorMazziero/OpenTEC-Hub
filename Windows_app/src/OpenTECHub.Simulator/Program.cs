using System.Globalization;
using OpenTECHub.Simulator;
using OpenTECHub.Protocol;

// ---------------------------------------------------------------------------
// OpenTEC device simulator.
//
// Stands in for the ESP32-S3 and the bioreactor behind it, speaking the protocol
// documented in docs/PROTOCOL.md. See docs/SIMULATOR.md.
//
//   opentec-simulator http [--port 8080]
//   opentec-simulator serial COM11
//   opentec-simulator headless [--duration 8h] [--output run.csv]
//
// Type a scenario name while running to switch behaviour; "?" lists them.
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (mode is not ("http" or "serial" or "headless"))
{
    PrintUsage();
    return 1;
}

var quiet = args.Contains("--quiet");
var httpPort = ReadOption("--port", 8080);
var dataDelay = ReadOption("--data-delay", 2000);
var randomSeed = ReadOption("--seed", 20260819);
var deadTimeSeconds = ReadDoubleOption("--dead-time", 25.0);
var quantisation = ReadDoubleOption("--quantisation", 0.0);
var servoSpeedTau = ReadDoubleOption("--servo-speed-tau", 1.5);
var servoTorqueTau = ReadDoubleOption("--servo-torque-tau", 8.0);
var profileName = ReadStringOption("--profile", "default");
var klaProfilePath = ReadStringOption("--kla-profile", null);

// kLa source
IKlaSource klaSource;
if (!string.IsNullOrWhiteSpace(klaProfilePath) && File.Exists(klaProfilePath))
{
    try
    {
        klaSource = ProfileKla.FromJsonFile(klaProfilePath);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] Could not load kLa profile from '{klaProfilePath}': {ex.Message}");
        return 1;
    }
}
else
{
    klaSource = new PowerLawKla();
}

// Cultivation profile
var profile = CultivationProfile.FromName(profileName ?? "default");
var servoPowerModel = ServoPowerModelOptions.Default with
{
    SpeedTimeConstantSeconds = servoSpeedTau,
    TorqueTimeConstantSeconds = servoTorqueTau,
};

// Headless Mode
if (mode == "headless")
{
    var durationText = ReadStringOption("--duration", "1h");
    var durationSeconds = ParseDuration(durationText ?? "1h");
    var stepSeconds = ReadDoubleOption("--step", 0.2);
    var outputPath = ReadStringOption("--output", null);

    var headlessClock = new AcceleratedClock();
    var headlessModel = new DeviceModel(
        clock: headlessClock,
        klaSource: klaSource,
        profile: profile,
        probeDeadTime: TimeSpan.FromSeconds(deadTimeSeconds),
        oxygenQuantisation: quantisation,
        randomSeed: randomSeed,
        servoPowerModel: servoPowerModel)
    {
        Scenario = args.Contains("--no-module") ? Scenario.NoModule : Scenario.Normal,
    };

    if (TryReadScenario(out var s))
    {
        headlessModel.Scenario = s;
    }

    ApplyRigOptions(headlessModel);

    return HeadlessRunner.Run(headlessModel, durationSeconds, stepSeconds, outputPath);
}

// Real-Time Server Modes (HTTP / Serial)
var clock = new WallClock();
var model = new DeviceModel(
    clock: clock,
    klaSource: klaSource,
    profile: profile,
    probeDeadTime: TimeSpan.FromSeconds(deadTimeSeconds),
    oxygenQuantisation: quantisation,
    randomSeed: randomSeed,
    servoPowerModel: servoPowerModel)
{
    DataDelayMs = dataDelay,
    Scenario = args.Contains("--no-module") ? Scenario.NoModule : Scenario.Normal,
};

if (TryReadScenario(out var startScenario))
{
    model.Scenario = startScenario;
}

if (args.Contains("--all-nodes"))
{
    model.EnableAllExternalNodes();
}

ApplyRigOptions(model);

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

Console.WriteLine("OpenTEC device simulator");
Console.WriteLine($"  mode       : {mode}");
Console.WriteLine($"  scenario   : {model.Scenario}");
Console.WriteLine($"  kLa source : {klaSource.Description}");
Console.WriteLine($"  profile    : {profile.Name} ({profile.Phases.Count} phases)");
Console.WriteLine($"  deadTime   : {deadTimeSeconds:F1} s");
Console.WriteLine($"  quantis.   : {(quantisation > 0 ? $"{quantisation:F2}%" : "none")}");
Console.WriteLine($"  servo tau  : speed {servoSpeedTau:F1} s; torque {servoTorqueTau:F1} s");
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

// Advance the process on its own cadence, independent of how often it is polled
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

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

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

double ReadDoubleOption(string name, double fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name &&
            double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }

    return fallback;
}

string? ReadStringOption(string name, string? fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return fallback;
}

double ParseDuration(string text)
{
    var trimmed = text.Trim().ToLowerInvariant();
    if (trimmed.EndsWith("h") && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
    {
        return hours * 3600.0;
    }
    if (trimmed.EndsWith("m") && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
    {
        return minutes * 60.0;
    }
    if (trimmed.EndsWith("s") && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
    {
        return seconds;
    }
    if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawSeconds))
    {
        return rawSeconds;
    }
    return 3600.0; // default 1h
}

// --rig a-on-1|a-on-2 (default a-on-2: MOSFET 2 → A, MOSFET 1 → B+C, the physical document);
// --nitrogen-source open|closed (default open — the kLa's normal state).
void ApplyRigOptions(DeviceModel target)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--rig")
        {
            target.GasRig = args[i + 1].Replace("_", "-").ToLowerInvariant() switch
            {
                "a-on-1" or "1" => new GasRigConfiguration(GasInput.Input1),
                "a-on-2" or "2" => new GasRigConfiguration(GasInput.Input2),
                var other => throw new ArgumentException($"--rig: '{other}' não é a-on-1 nem a-on-2."),
            };
        }
        else if (args[i] == "--nitrogen-source")
        {
            target.NitrogenSourceOpen = args[i + 1].ToLowerInvariant() switch
            {
                "open" or "aberta" => true,
                "closed" or "fechada" => false,
                var other => throw new ArgumentException($"--nitrogen-source: '{other}' não é open nem closed."),
            };
        }
    }
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
        OpenTEC device simulator

          opentec-simulator http [--port 8080]       serve on 127.0.0.1 (HTTP)
          opentec-simulator serial COM11             serve on a virtual COM pair
          opentec-simulator headless [--duration 8h] run batch simulation to CSV

        Options:
          --scenario <name>     start in a scenario (normal, no-module, stall, etc.)
          --kla-profile <path>  load published kLa mapping profile JSON
          --profile <name>      cultivation profile (default, batch-ecoli, fed-batch, step-test)
          --dead-time <sec>     oxygen probe dead time in seconds (default 25)
          --quantisation <pct>  oxygen sensor quantisation step (default 0)
          --servo-speed-tau <s> servo speed first-order time constant (default 1.5)
          --servo-torque-tau <s> servo torque first-order time constant (default 8)
          --data-delay <ms>     telemetry period (default 2000)
          --seed <int>          random number generator seed
          --duration <time>     (headless only) simulation length (e.g. 8h, 30m, 3600s)
          --step <sec>          (headless only) integration step dt (default 0.2s)
          --output <path.csv>   (headless only) write CSV output to file (default stdout)
          --no-module           start with the sensor module offline
          --all-nodes           start with every external-node route enabled (bench-test dry run)
          --rig a-on-1|a-on-2   which flowmeter output drives valve A; B/C share the other (default a-on-2)
          --nitrogen-source open|closed  manual N2 valve upstream of B (default open)
          --quiet               no per-frame console echo
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
          nitrogen-left-open  N2 source open during a power assay: every vent (C) opening strips DO
        """);
