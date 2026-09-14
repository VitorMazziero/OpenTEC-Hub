using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTECHub.Protocol;

namespace OpenTECHub.Harness;

/// <summary>
/// Options for <c>opentec-harness bench-test</c>.
/// </summary>
internal sealed record BenchOptions
{
    /// <summary><c>COMx</c> or an IPv4 address.</summary>
    public required string Target { get; init; }
    public string ExpectedHubVersion { get; init; } = "10.4.0-dev";
    public IReadOnlySet<string> Suites { get; init; } = new HashSet<string>(["B1", "B2", "B3", "B5"]);
    public int SoakMinutes { get; init; }
    public string OutputDirectory { get; init; } = "";
    /// <summary>B2.1 reboots the Hub through the DTR pulse to read the boot heap line (USB only).</summary>
    public bool ResetHub { get; init; }
    /// <summary>B3.8 and B3.11 touch the pump route; off unless asked.</summary>
    public bool Pump { get; init; }

    public bool IsUsb => Target.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Unattended bench validation of Hub 10.2 + nodes v11/3.9, as specified in
/// <c>docs/processes/TESTES_AUTOMATICOS_BANCADA.md</c>. Suites B1 (frame and identity),
/// B2 (node health and Hub heap), B3 (configuration echoes) and B5 (soak and bursts).
/// </summary>
/// <remarks>
/// <para>
/// Uses the app's own <see cref="TelemetryParser"/>, <see cref="CommandBuilders"/>,
/// <see cref="HubNodeDirectoryClient"/> and <see cref="HubNodeDiagClient"/>, so what passes
/// here is the contract the app relies on - not a second implementation of it.
/// </para>
/// <para>
/// Every value it changes on a node is restored in <c>finally</c>; flow setpoint and motor
/// are never touched. B4 (arbiter) is not here: it needs the app's <c>CommandArbiter</c>.
/// </para>
/// </remarks>
internal sealed class BenchTestSuite(BenchOptions options, ILoggerFactory loggerFactory)
{
    private static readonly string[] Devices = ["distance", "agitator", "pump", "flowmeter", "biomass"];
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedNodeVersions = new Dictionary<string, string[]>
    {
        ["distance"] = ["v11"],
        ["agitator"] = ["v10"],
        ["pump"] = ["3.12"],
        ["flowmeter"] = ["v12.0"],
        ["biomass"] = ["v11.1"],
    };

    private readonly List<Row> _rows = [];
    private readonly StringBuilder _log = new();
    private readonly TelemetryParser _parser = new();
    private readonly List<(string what, Func<CancellationToken, Task> restore)> _restores = [];

    private ITransport? _transport;
    private HttpClient? _http;
    private string _hubIp = "";
    private string? _lastRawFrame;
    private readonly List<string> _nodeDiagLines = [];
    private int _malformed;
    private int _acks;

    private sealed record Row(string Suite, string Id, string Name, string Measured, string Limit, string Verdict, string Evidence);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        Log($"OpenTEC-Hub bench-test — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log($"target: {options.Target} ({(options.IsUsb ? "USB" : "Wi-Fi")})  expected hub: {options.ExpectedHubVersion}");
        Log($"suites: {string.Join(",", options.Suites)}  soak: {options.SoakMinutes} min  out: {options.OutputDirectory}");
        Log("");

        try
        {
            if (!await PreconditionsAsync(ct).ConfigureAwait(false))
            {
                Log("Pré-condições reprovadas: suítes não executadas.");
            }
            else
            {
                if (options.Suites.Contains("B1"))
                {
                    await SuiteB1Async(ct).ConfigureAwait(false);
                }

                if (options.Suites.Contains("B2"))
                {
                    await SuiteB2Async(ct).ConfigureAwait(false);
                }

                if (options.Suites.Contains("B3"))
                {
                    await SuiteB3Async(ct).ConfigureAwait(false);
                }

                if (options.Suites.Contains("B5"))
                {
                    await SuiteB5Async(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("Interrompido pelo operador.");
        }
        catch (Exception ex)
        {
            Log($"ABORTADO: {ex}");
            Add("—", "X", "exceção não tratada", ex.GetType().Name, "—", "FAIL", "");
        }
        finally
        {
            await RestoreAllAsync().ConfigureAwait(false);
            if (_transport is not null)
            {
                await _transport.DisconnectAsync().ConfigureAwait(false);
            }
            _http?.Dispose();
        }

        var failed = _rows.Count(r => r.Verdict == "FAIL");
        await WriteReportAsync().ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Report: {Path.Combine(options.OutputDirectory, "report.md")}");
        return failed == 0 ? 0 : 1;
    }

    // ──────────────────────────────────────────────────────────────────────
    // §1 Pré-condições
    // ──────────────────────────────────────────────────────────────────────

    private async Task<bool> PreconditionsAsync(CancellationToken ct)
    {
        Log("== Pré-condições ==");

        if (options.IsUsb)
        {
            var serial = new SerialTransport(
                new SerialTransportConfig { PortName = options.Target },
                loggerFactory.CreateLogger<SerialTransport>());
            _transport = serial;
        }
        else
        {
            _hubIp = options.Target;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            _transport = new HttpTransport(
                new HttpTransportConfig { IpAddress = options.Target },
                loggerFactory.CreateLogger<HttpTransport>());
        }

        var connected = await _transport.ConnectAsync(ct).ConfigureAwait(false);
        Add("P", "P1", "Hub responde ao handshake", connected ? "OK" : "sem OK", "OK", connected ? "PASS" : "FAIL", "");
        if (!connected)
        {
            return false;
        }

        var first = await ReadFramesAsync(TimeSpan.FromSeconds(8), 1, ct).ConfigureAwait(false);
        Add("P", "P1b", "primeiro quadro", $"{first.Count} em 8 s", "≥ 1", first.Count > 0 ? "PASS" : "FAIL", "");
        if (first.Count == 0)
        {
            return false;
        }

        var r = _parser.Readings;
        var version = r.HubFirmwareVersion ?? "(ausente)";
        Add("P", "P2", "HubFirmwareVersion", version, options.ExpectedHubVersion,
            version == options.ExpectedHubVersion ? "PASS" : "FAIL", "");
        Add("P", "P3", "HubProtocolVersion", r.HubProtocolVersion.ToString(CultureInfo.InvariantCulture), "10",
            r.HubProtocolVersion == 10 ? "PASS" : "FAIL", "");

        // P4: fleet versions. On USB the frame carries *NodeVer once registered; on Wi-Fi /nodes.
        var versionsOk = true;
        var detail = new StringBuilder();
        foreach (var dev in Devices)
        {
            var node = NodeOf(r, dev);
            var v = node.FirmwareVersion ?? "(sem NodeVer)";
            var ok = ExpectedNodeVersions[dev].Contains(v, StringComparer.OrdinalIgnoreCase);
            versionsOk &= ok;
            detail.Append(CultureInfo.InvariantCulture, $"{dev}={v}{(ok ? "" : "!")} ");
        }
        Add("P", "P4", "frota regravada (NodeVer)", detail.ToString().Trim(),
            "v11 v10 3.12 v12.0 v11.1", versionsOk ? "PASS" : "FAIL", "");
        if (!versionsOk)
        {
            Log("  → regravar com External-Devices/tools/Publish-OtaFirmware.ps1 -Device <nó> -Compile");
        }

        // P5: presence for 30 s.
        var frames = await ReadFramesAsync(TimeSpan.FromSeconds(30), int.MaxValue, ct).ConfigureAwait(false);
        var presence = frames.Count > 0 && frames.All(f =>
            f.DistanceOnline && f.AgitatorOnline && f.PumpOnline && f.FlowmeterOnline && f.BiomassOnline);
        Add("P", "P5", "cinco nós online por 30 s", presence ? $"sim ({frames.Count} quadros)" : DescribePresence(frames),
            "5/5 em todos os quadros", presence ? "PASS" : "FAIL", "");

        // P7: safe state.
        var safe = r.FlowSetpoint <= 0.0001;
        Add("P", "P7", "vazão em 0 (estado seguro)", FormattableString.Invariant($"flowSetpoint={r.FlowSetpoint:F2}"),
            "0", safe ? "PASS" : "FAIL", "");

        return versionsOk && presence && safe && version == options.ExpectedHubVersion;
    }

    // ──────────────────────────────────────────────────────────────────────
    // B1 — quadro agregado e identidade
    // ──────────────────────────────────────────────────────────────────────

    private async Task SuiteB1Async(CancellationToken ct)
    {
        Log("== B1 quadro e identidade ==");
        var sizes = new List<(long ms, int bytes, bool parsed)>();
        var sw = Stopwatch.StartNew();

        if (options.IsUsb)
        {
            // 60 frames as they come (≈ 2 min at 2 s).
            var lines = await ReadRawAsync(TimeSpan.FromSeconds(140), 60, ct).ConfigureAwait(false);
            foreach (var (ms, line) in lines)
            {
                sizes.Add((ms, Encoding.UTF8.GetByteCount(line), true));
            }
        }
        else
        {
            for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                var body = await GetAsync("/readData", ct).ConfigureAwait(false);
                var parsed = body is not null && _parser.Parse(body) == ParseOutcome.Updated;
                if (body is not null)
                {
                    _lastRawFrame = body;
                }
                sizes.Add((sw.ElapsedMilliseconds, body is null ? 0 : Encoding.UTF8.GetByteCount(body), parsed));
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        await WriteCsvAsync("frame-size.csv", "t_ms,bytes,parsed",
            sizes.Select(s => FormattableString.Invariant($"{s.ms},{s.bytes},{(s.parsed ? 1 : 0)}"))).ConfigureAwait(false);

        var max = sizes.Count == 0 ? 0 : sizes.Max(s => s.bytes);
        var median = sizes.Count == 0 ? 0 : sizes.Select(s => s.bytes).Order().ElementAt(sizes.Count / 2);
        var verdict = max <= 2600 ? "PASS" : max <= 3072 ? "WARN" : "FAIL";
        Add("B1", "B1.1", "Content-Length do quadro (máx / mediana)", $"{max} / {median} B", "≤ 2600 B (≤ 3072 aviso)", verdict, "frame-size.csv");
        if (verdict == "WARN")
        {
            Log("  → acima do teto do plano: cortar FlowOutput / FlowSetpointCorrected primeiro.");
        }

        var parsedCount = sizes.Count(s => s.parsed);
        Add("B1", "B1.2", "quadros parseáveis", $"{parsedCount}/{sizes.Count}; malformed={_malformed}", "todos, 0 malformed",
            parsedCount == sizes.Count && _malformed == 0 ? "PASS" : "FAIL", "frame-size.csv");

        var r = _parser.Readings;
        var keys = 0;
        var identity = new Dictionary<string, object?>();
        foreach (var dev in Devices)
        {
            var n = NodeOf(r, dev);
            keys += (n.Ip is not null ? 1 : 0) + (n.FirmwareVersion is not null ? 1 : 0) + (n.Mac is not null ? 1 : 0);
            identity[dev] = new { n.Ip, n.Mac, n.FirmwareVersion };
        }
        await WriteJsonAsync("identity.json", identity).ConfigureAwait(false);
        Add("B1", "B1.3", "chaves de identidade no quadro", $"{keys}/15", "15/15", keys == 15 ? "PASS" : "FAIL", "identity.json");

        if (!options.IsUsb)
        {
            using var dir = new HubNodeDirectoryClient();
            var nodes = await dir.FetchAsync(_hubIp, ct).ConfigureAwait(false);
            if (nodes is null)
            {
                Add("B1", "B1.4", "/nodes coerente com o quadro", "sem resposta", "0 divergências", "FAIL", "");
            }
            else
            {
                var diverge = new List<string>();
                var ageBad = new List<string>();
                foreach (var dev in Devices)
                {
                    var frame = NodeOf(r, dev);
                    var e = nodes.Find(dev);
                    if (e is null) { diverge.Add($"{dev}: ausente em /nodes"); continue; }
                    if (e.Identity.Ip != frame.Ip)
                    {
                        diverge.Add($"{dev}: ip {e.Identity.Ip}≠{frame.Ip}");
                    }

                    if (e.Identity.FirmwareVersion != frame.FirmwareVersion)
                    {
                        diverge.Add($"{dev}: ver {e.Identity.FirmwareVersion}≠{frame.FirmwareVersion}");
                    }

                    if (e.Identity.Mac != frame.Mac)
                    {
                        diverge.Add($"{dev}: mac");
                    }

                    if (nodes.HubTimeMs is { } now && (e.LastHelloMs is not null || e.LastDataMs is not null))
                    {
                        var expected = now - Math.Max(e.LastHelloMs ?? 0, e.LastDataMs ?? 0);
                        if (Math.Abs(expected - e.AgeMs) > 50)
                        {
                            ageBad.Add($"{dev}: age {e.AgeMs} vs {expected}");
                        }
                    }
                }
                Add("B1", "B1.4", "/nodes coerente com o quadro", diverge.Count == 0 ? "0 divergências" : string.Join("; ", diverge),
                    "0 divergências", diverge.Count == 0 ? "PASS" : "FAIL", "identity.json");
                Add("B1", "B1.5", "age_ms = hub_time_ms − max(last_*)", ageBad.Count == 0 ? "±50 ms nos cinco" : string.Join("; ", ageBad),
                    "± 50 ms", ageBad.Count == 0 ? "PASS" : "FAIL", "");
            }
        }
        else
        {
            Skip("B1", "B1.4", "/nodes coerente com o quadro", "só em Wi-Fi");
            Skip("B1", "B1.5", "age_ms honesto", "só em Wi-Fi");
        }

        Skip("B1", "B1.6", "reboot de um nó é visto", "manual (relé ou OTA)");

        if (options.IsUsb)
        {
            var lines = await ReadRawAsync(TimeSpan.FromSeconds(260), 120, ct).ConfigureAwait(false);
            var periods = lines.Zip(lines.Skip(1), (a, b) => b.ms - a.ms).ToList();
            await WriteCsvAsync("usb-period.csv", "t_ms,period_ms",
                lines.Skip(1).Zip(periods, (l, p) => FormattableString.Invariant($"{l.ms},{p}"))).ConfigureAwait(false);
            var med = periods.Count == 0 ? 0 : periods.Order().ElementAt(periods.Count / 2);
            var gaps = periods.Count(p => p > 5000);
            Add("B1", "B1.7", "período USB (mediana) / gaps > 5 s", $"{med} ms / {gaps}", "2000 ± 100 ms / 0",
                Math.Abs(med - 2000) <= 100 && gaps == 0 ? "PASS" : "FAIL", "usb-period.csv");
        }
        else
        {
            Skip("B1", "B1.7", "período USB", "só em USB");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // B2 — saúde dos nós e heap
    // ──────────────────────────────────────────────────────────────────────

    private async Task SuiteB2Async(CancellationToken ct)
    {
        Log("== B2 saúde dos nós ==");

        if (options.IsUsb && options.ResetHub && _transport is SerialTransport)
        {
            await B2_1_HeapAsync(ct).ConfigureAwait(false);
        }
        else
        {
            Skip("B2", "B2.1", "heap depois da tarefa NodeDiag", options.IsUsb ? "passe --reset-hub para ler o boot" : "só em USB (linha de boot)");
        }

        if (!options.IsUsb)
        {
            using var diag = new HubNodeDiagClient();
            var d = await diag.FetchAsync(_hubIp, ct).ConfigureAwait(false);
            var raw = await GetAsync("/nodeDiag", ct).ConfigureAwait(false);
            if (raw is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "nodediag.json"), raw, ct).ConfigureAwait(false);
            }
            EvaluateDiagDirectory(d, raw, "Wi-Fi");

            var one = HubNodeDiagClient.Parse(await GetAsync("/nodeDiag?dev=pump", ct).ConfigureAwait(false));
            var filtered = one is { Nodes.Count: 1 } && one.Nodes[0].Device == "pump";
            Add("B2", "B2.5", "?dev=pump filtra", one is null ? "sem resposta" : $"{one.Nodes.Count} entrada(s)", "1, dev=pump", filtered ? "PASS" : "FAIL", "");
        }
        else
        {
            Skip("B2", "B2.2–B2.5", "/nodeDiag por HTTP", "só em Wi-Fi");
        }

        if (options.IsUsb)
        {
            await B2_6_SerialAsync(ct).ConfigureAwait(false);
        }
        else
        {
            Skip("B2", "B2.6–B2.8", "serial nodeDiag", "só em USB");
        }

        Skip("B2", "B2.9", "nó desligado → code ≠ 200", "manual (relé)");
        Skip("B2", "B2.10", "varredura não custa push", options.SoakMinutes > 0 ? "ver B5.3" : "passe --soak-min 30");
    }

    private async Task B2_1_HeapAsync(CancellationToken ct)
    {
        // Reconnect with the DTR pulse: that is what reboots the board (PROTOCOL Q6).
        await _transport!.DisconnectAsync().ConfigureAwait(false);
        var pulsed = new SerialTransport(
            new SerialTransportConfig { PortName = options.Target, PulseResetOnConnect = true },
            loggerFactory.CreateLogger<SerialTransport>());
        _transport = pulsed;
        var ok = await pulsed.ConnectAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            Add("B2", "B2.1", "heap depois da tarefa NodeDiag", "reconexão com pulso falhou", "> 150 000 B", "FAIL", "");
            return;
        }

        // The boot banner may already have scrolled past during the settle; read what is left.
        string? heapLine = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15) && heapLine is null)
        {
            var line = await _transport.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
            Track(line);
            if (line.Contains("NodeDiag task criada", StringComparison.Ordinal))
            {
                heapLine = line;
            }
        }

        if (heapLine is null)
        {
            Add("B2", "B2.1", "heap depois da tarefa NodeDiag", "linha de boot não capturada (o pulso consome o banner; ler pelo monitor serial)", "> 150 000 B", "WARN", "hub-heap.txt");
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "hub-heap.txt"), heapLine + Environment.NewLine, ct).ConfigureAwait(false);
        var before = ExtractNumber(heapLine, "antes=");
        var after = ExtractNumber(heapLine, "depois=");
        var pass = after is > 150_000 && before - after < 12_000;
        Add("B2", "B2.1", "heap depois da tarefa NodeDiag", $"antes={before} depois={after}", "> 150 000 B; custo < 12 000 B", pass ? "PASS" : "FAIL", "hub-heap.txt");
    }

    private void EvaluateDiagDirectory(HubNodeDiagDirectory? d, string? raw, string via)
    {
        if (d is null)
        {
            Add("B2", "B2.2", $"/nodeDiag completo ({via})", "sem resposta ou não parseável", "5 × code 200", "FAIL", "nodediag.json");
            return;
        }

        var ok200 = Devices.Select(d.Find).Count(n => n is { Code: 200 } && n.Age is { } a && a < TimeSpan.FromSeconds(35));
        Add("B2", "B2.2", $"/nodeDiag completo ({via})", $"{ok200}/5 com code 200 e age < 35 s", "5/5", ok200 == 5 ? "PASS" : "FAIL", "nodediag.json");

        var common = 0; var rssiOk = true; var otaOk = true;
        foreach (var dev in Devices)
        {
            var n = d.Find(dev);
            if (n is null)
            {
                continue;
            }

            common += (n.UptimeS is not null ? 1 : 0) + (n.FreeHeap is not null ? 1 : 0) + (n.Rssi is not null ? 1 : 0) + (n.HubFailStreak is not null ? 1 : 0) + (n.Ota is not null ? 1 : 0);
            if (n.Rssi is { } rssi && (rssi < -90 || rssi > -20))
            {
                rssiOk = false;
            }

            if (n.Ota == true)
            {
                otaOk = false;
            }
        }
        Add("B2", "B2.3", "métricas comuns presentes", $"{common}/25; rssi ok={rssiOk}; ota livre={otaOk}", "25/25, −90…−20 dBm, ota:false",
            common == 25 && rssiOk && otaOk ? "PASS" : "FAIL", "nodediag.json");

        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var worst = 0; var worstDev = "";
                foreach (var e in doc.RootElement.GetProperty("nodes").EnumerateArray())
                {
                    if (!e.TryGetProperty("diag", out var diag) || diag.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var len = Encoding.UTF8.GetByteCount(diag.GetRawText());
                    if (len > worst) { worst = len; worstDev = e.GetProperty("dev").GetString() ?? ""; }
                }
                Add("B2", "B2.4", "maior corpo de /diag", $"{worst} B ({worstDev})", "≤ 480 B", worst <= 480 ? "PASS" : "FAIL", "nodediag.json");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Add("B2", "B2.4", "maior corpo de /diag", "documento inesperado", "≤ 480 B", "FAIL", "nodediag.json");
            }
        }
    }

    private async Task B2_6_SerialAsync(CancellationToken ct)
    {
        var before = _parser.Readings.Snapshot();
        _nodeDiagLines.Clear();
        var sw = Stopwatch.StartNew();
        var written = await _transport!.WriteAsync(CommandBuilders.NodeDiag("all").ToJson(), ct).ConfigureAwait(false);
        var frames = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(6))
        {
            var line = await _transport.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(50, ct).ConfigureAwait(false); continue; }
            if (Track(line) == ParseOutcome.Updated)
            {
                frames++;
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(options.OutputDirectory, "nodediag-serial.log"), _nodeDiagLines, ct).ConfigureAwait(false);

        var parsed = _nodeDiagLines.Select(HubNodeDiagClient.Parse).Where(p => p is { Nodes.Count: 1 }).Select(p => p!.Nodes[0]).ToList();
        var devs = parsed.Select(p => p.Device).Distinct().Count();
        var under1k = _nodeDiagLines.All(l => Encoding.UTF8.GetByteCount(l) < 1024);
        Add("B2", "B2.6", "serial nodeDiag all → 5 linhas", $"escrito={written}; linhas={_nodeDiagLines.Count}; nós distintos={devs}; < 1 KB={under1k}; quadros no meio={frames}",
            "5 linhas, 5 nós, < 1 KB, telemetria continua", written && _nodeDiagLines.Count == 5 && devs == 5 && under1k && frames >= 1 ? "PASS" : "FAIL", "nodediag-serial.log");

        // Evaluate the same B2.2–B2.4 criteria on the serial answers.
        var dir = new HubNodeDiagDirectory(null, parsed);
        EvaluateDiagDirectory(dir, "{\"nodes\":[" + string.Join(",", _nodeDiagLines.Select(l => l["{\"NodeDiag\":".Length..^1])) + "]}", "USB");

        _nodeDiagLines.Clear();
        await _transport.WriteAsync("{\"nodeDiag\":\"xyz\"}", ct).ConfigureAwait(false);
        sw.Restart();
        var next = 0L;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var line = await _transport.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(50, ct).ConfigureAwait(false); continue; }
            if (Track(line) == ParseOutcome.Updated && next == 0)
            {
                next = sw.ElapsedMilliseconds;
            }
        }
        var unknown = _nodeDiagLines.Count == 1 && HubNodeDiagClient.Parse(_nodeDiagLines[0]) is { Nodes: [{ Code: 404 }] };
        Add("B2", "B2.7", "serial nodeDiag desconhecido", $"linhas={_nodeDiagLines.Count}; 404={unknown}; próximo quadro em {next} ms",
            "1 linha code 404; quadro ≤ 3 s", unknown && next is > 0 and <= 3000 ? "PASS" : "FAIL", "nodediag-serial.log");

        var after = _parser.Readings.Snapshot();
        var changed = new List<string>();
        if (Math.Abs(after.FlowSetpoint - before.FlowSetpoint) > 1e-6)
        {
            changed.Add("flowSetpoint");
        }

        if (after.PumpCommEnabled != before.PumpCommEnabled)
        {
            changed.Add("pumpComm");
        }

        if (after.DistanceOffsetMm != before.DistanceOffsetMm)
        {
            changed.Add("DistanceOffsetMm");
        }

        if (after.FlowKp != before.FlowKp)
        {
            changed.Add("FlowKp");
        }

        Add("B2", "B2.8", "pedido não muda estado", changed.Count == 0 ? "0 diferenças" : string.Join(",", changed), "0", changed.Count == 0 ? "PASS" : "FAIL", "");
    }

    // ──────────────────────────────────────────────────────────────────────
    // B3 — ecos e comandos de configuração
    // ──────────────────────────────────────────────────────────────────────

    private async Task SuiteB3Async(CancellationToken ct)
    {
        Log("== B3 ecos ==");
        var latencies = new List<string>();

        // Distância
        var r = _parser.Readings;
        if (r.DistanceOffsetMm is { } offset0)
        {
            var target = Math.Round(offset0 + 5.5, 2);
            Remember("offset da distância", c => Send(CommandBuilders.DistanceConfig(offsetMm: offset0), c));
            var (ok, ms, pendingSeen) = await EchoAsync(CommandBuilders.DistanceConfig(offsetMm: target),
                s => s.DistanceOffsetMm is { } v && Math.Abs(v - target) < 0.01, TimeSpan.FromSeconds(40),
                s => s.DistanceCommandPending == true, ct).ConfigureAwait(false);
            latencies.Add($"distance,DistanceOffsetMm,{ms}");
            Add("B3", "B3.1", "offset da distância ecoado", $"{(ok ? "sim" : "não")} em {ms} ms; pending visto={pendingSeen}", "≤ 20 000 ms; pending true→false",
                ok && ms <= 20_000 ? "PASS" : ok ? "WARN" : "FAIL", "echo-latency.csv");

            if (!options.IsUsb && r.DistanceNode.Ip is { } dip)
            {
                var cfg = await GetAsync($"http://{dip}/config", ct, absolute: true).ConfigureAwait(false);
                var direct = cfg is not null && cfg.Contains(target.ToString("F2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                // No answer is not a contract failure: the PC may simply not route to the node's subnet.
                Add("B3", "B3.2", "GET /config do nó confere com o eco", cfg is null ? "sem resposta (PC alcança o nó?)" : (direct ? "igual" : "diferente"), "offset_mm igual", cfg is null ? "WARN" : direct ? "PASS" : "FAIL", "");
            }
            else
            {
                Skip("B3", "B3.2", "GET /config do nó", "só em Wi-Fi");
            }
            Skip("B3", "B3.3", "persistência após reboot", "manual (OTA/relé)");

            if (r.DistanceSamplePeriodMs is { } sp && r.DistanceSendPeriodMs is { } sd)
            {
                Remember("períodos da distância", c => Send(CommandBuilders.DistanceConfig(offsetMm: offset0, samplePeriodMs: sp, sendPeriodMs: sd), c));
                var (ok4, ms4, _) = await EchoAsync(CommandBuilders.DistanceConfig(offsetMm: offset0, samplePeriodMs: sp + 10, sendPeriodMs: sd + 10),
                    s => s.DistanceOffsetMm is { } v && Math.Abs(v - offset0) < 0.01 && s.DistanceSamplePeriodMs == sp + 10 && s.DistanceSendPeriodMs == sd + 10,
                    TimeSpan.FromSeconds(40), null, ct).ConfigureAwait(false);
                latencies.Add($"distance,offset+sample+send,{ms4}");
                Add("B3", "B3.4", "três campos numa frame, ecoados juntos", ok4 ? $"sim em {ms4} ms" : "não", "mesmo quadro", ok4 ? "PASS" : "FAIL", "echo-latency.csv");
            }
            else
            {
                Skip("B3", "B3.4", "três campos numa frame", "períodos sem eco");
            }
        }
        else
        {
            Skip("B3", "B3.1–B3.4", "distância", "DistanceOffsetMm sem eco no quadro");
        }

        // Fluxômetro (setpoint 0 guaranteed by P7)
        if (r.FlowKp is { } kp0)
        {
            Remember("kp do fluxômetro", c => Send(CommandBuilders.FlowTuning(kp: kp0), c));
            var kp1 = Math.Round(kp0 + 0.01, 4);
            var (ok, ms, _) = await EchoAsync(CommandBuilders.FlowTuning(kp: kp1), s => s.FlowKp is { } v && Math.Abs(v - kp1) < 1e-4, TimeSpan.FromSeconds(10), null, ct).ConfigureAwait(false);
            latencies.Add($"flowmeter,FlowKp,{ms}");
            Add("B3", "B3.5", "FlowKp ecoado", ok ? $"sim em {ms} ms" : "não", "≤ 5000 ms", ok && ms <= 5000 ? "PASS" : ok ? "WARN" : "FAIL", "echo-latency.csv");

            var others = new List<string>();
            var allOk = true;
            foreach (var (name, get, make) in new (string, Func<SensorSnapshot, double?>, Func<double, OpenTECCommand>)[]
            {
                ("FlowKi", s => s.FlowKi, v => CommandBuilders.FlowTuning(ki: v)),
                ("FlowFfGain", s => s.FlowFfGain, v => CommandBuilders.FlowTuning(ffGain: v)),
                ("FlowFfOffset", s => s.FlowFfOffset, v => CommandBuilders.FlowTuning(ffOffset: v)),
                ("FlowRampRate", s => s.FlowRampRate, v => CommandBuilders.FlowTuning(rampRate: v)),
            })
            {
                var cur = get(_parser.Readings.Snapshot());
                if (cur is not { } c0) { others.Add($"{name}: sem eco"); allOk = false; continue; }
                Remember(name, c => Send(make(c0), c));
                var t = Math.Round(c0 + 0.01, 4);
                var (o, m, _) = await EchoAsync(make(t), s => get(s) is { } v && Math.Abs(v - t) < 1e-4, TimeSpan.FromSeconds(10), null, ct).ConfigureAwait(false);
                latencies.Add($"flowmeter,{name},{m}");
                others.Add($"{name}: {(o ? m + " ms" : "não")}");
                allOk &= o && m <= 5000;
            }
            Add("B3", "B3.6", "Ki, FF gain, FF offset, rampa ecoados", string.Join("; ", others), "≤ 5000 ms cada", allOk ? "PASS" : "FAIL", "echo-latency.csv");
        }
        else
        {
            Skip("B3", "B3.5–B3.6", "sintonia do fluxômetro", "FlowKp sem eco no quadro");
        }

        var bootIds = new HashSet<long>();
        foreach (var f in await ReadFramesAsync(TimeSpan.FromSeconds(30), 15, ct).ConfigureAwait(false))
        {
            if (f.FlowmeterBootId is { } b)
            {
                bootIds.Add(b);
            }
        }
        Add("B3", "B3.7", "FlowmeterBootId estável", $"{bootIds.Count} valor(es) em 15 quadros", "1", bootIds.Count == 1 ? "PASS" : "FAIL", "");

        // Bomba
        if (options.Pump && _parser.Readings.PumpCommEnabled == true)
        {
            var (ok, ms, _) = await EchoAsync(CommandBuilders.PumpResetVolume(), s => s.PumpVolume is >= 0 and < 0.05, TimeSpan.FromSeconds(10), null, ct).ConfigureAwait(false);
            latencies.Add($"pump,reset_volume,{ms}");
            Add("B3", "B3.8", "reset_volume → PumpVol < 0,05 mL", ok ? $"sim em {ms} ms" : FormattableString.Invariant($"não (PumpVol={_parser.Readings.PumpVolume:F3})"), "quadro seguinte ao ack", ok ? "PASS" : "FAIL", "echo-latency.csv");
        }
        else
        {
            Skip("B3", "B3.8", "reset_volume", options.Pump ? "pumpComm desligado" : "passe --pump");
        }

        Skip("B3", "B3.9", "calibração polinomial da bomba", "validada pelo fluxo dedicado da tela (nove coeficientes, ACK, eco e CRC)");

        {
            var malformedBefore = _malformed;
            await Send(CommandBuilders.PumpPid(0.5, 0.05, 0.001), ct).ConfigureAwait(false);
            var frames = await ReadRawAsync(TimeSpan.FromSeconds(8), 3, ct).ConfigureAwait(false);
            var leaked = frames.Any(f => f.line.Contains("PumpPidKp", StringComparison.Ordinal));
            Add("B3", "B3.10", "pid_* sem eco (3.9), sem falha", $"eco vazou={leaked}; malformed={_malformed - malformedBefore}", "sem chave, 0 malformed", !leaked && _malformed == malformedBefore ? "PASS" : "FAIL", "");
        }

        if (options.Pump && _parser.Readings.PumpCommEnabled == true)
        {
            Remember("pumpComm", c => Send(CommandBuilders.PumpEnable(), c));
            await Send(CommandBuilders.PumpStopProfile(), ct).ConfigureAwait(false);
            await Send(CommandBuilders.PumpRoutingDisabled(), ct).ConfigureAwait(false);
            var frames = await ReadFramesAsync(TimeSpan.FromSeconds(10), int.MaxValue, ct).ConfigureAwait(false);
            var stopped = frames.Count > 0 && frames.All(f => f.PumpFlow <= 0.0001) && frames.Last().PumpCommEnabled == false;
            Add("B3", "B3.11", "quadro de segurança da bomba", stopped ? "parada, rota off" : "não confirmado", "PumpFlow 0 por 10 s, pumpComm off", stopped ? "PASS" : "FAIL", "");
        }
        else
        {
            Skip("B3", "B3.11", "quadro de segurança da bomba", "passe --pump com pumpComm ligado");
        }

        // Biomassa
        {
            var snap = _parser.Readings.Snapshot();
            if (snap.BiomassGear is { } gear0 && snap.BiomassPwmPercent > 0 && snap.BiomassIntegrationTimeMs > 0)
            {
                var pwm0 = snap.BiomassPwmPercent;
                var itCode = ItCode(snap.BiomassIntegrationTimeMs);
                Remember("aquisição da biomassa", async c =>
                {
                    await Send(CommandBuilders.BiomassGear(gear0), c).ConfigureAwait(false);
                    await WaitPendingClearAsync(c).ConfigureAwait(false);
                    await Send(CommandBuilders.BiomassPwm(pwm0), c).ConfigureAwait(false);
                });
                var transitions = 0; var okAll = true; var sw = Stopwatch.StartNew();
                foreach (var cmd in new[] { CommandBuilders.BiomassGear(gear0), CommandBuilders.BiomassIt(itCode), CommandBuilders.BiomassPwm(Math.Min(100, pwm0 + 1)) })
                {
                    var (o, _, pending) = await EchoAsync(cmd, s => s.BiomassCommandPending == false, TimeSpan.FromSeconds(12), s => s.BiomassCommandPending == true, ct).ConfigureAwait(false);
                    okAll &= o; if (pending)
                    {
                        transitions++;
                    }
                }
                var pwmEchoed = Math.Abs(_parser.Readings.BiomassPwmPercent - Math.Min(100, pwm0 + 1)) < 0.5;
                latencies.Add($"biomass,gear+it+pwm,{sw.ElapsedMilliseconds}");
                Add("B3", "B3.12", "três comandos em sequência (gear, IT, PWM)", $"pendências vistas={transitions}/3; todos confirmados={okAll}; PWM ecoado={pwmEchoed}; {sw.ElapsedMilliseconds} ms",
                    "3 pendências, ecos ≤ 10 s cada", okAll && pwmEchoed ? "PASS" : "FAIL", "echo-latency.csv");
            }
            else
            {
                Skip("B3", "B3.12", "aquisição da biomassa", "BiomassGear/PWM/IT sem eco");
            }

            if (_parser.Readings.BiomassEma is { } ema0 && _parser.Readings.BiomassProbePeriodMs is { } per0)
            {
                Remember("EMA/período da biomassa", async c =>
                {
                    await Send(CommandBuilders.BiomassEma(ema0), c).ConfigureAwait(false);
                    await WaitPendingClearAsync(c).ConfigureAwait(false);
                    await Send(CommandBuilders.BiomassProbePeriod(per0), c).ConfigureAwait(false);
                });
                var (o1, m1, _) = await EchoAsync(CommandBuilders.BiomassEma(0.5), s => s.BiomassEma is { } v && Math.Abs(v - 0.5) < 1e-3, TimeSpan.FromSeconds(12), null, ct).ConfigureAwait(false);
                await WaitPendingClearAsync(ct).ConfigureAwait(false);
                var (o2, m2, _) = await EchoAsync(CommandBuilders.BiomassProbePeriod(100), s => s.BiomassProbePeriodMs is { } v && v >= 100 && v != per0, TimeSpan.FromSeconds(12), null, ct).ConfigureAwait(false);
                var floor = _parser.Readings.BiomassProbePeriodMs;
                latencies.Add($"biomass,BiomassEma,{m1}"); latencies.Add($"biomass,BiomassProbePeriodMs,{m2}");
                Add("B3", "B3.13", "EMA 0,5 e período 100 ms (piso térmico)", $"ema={(o1 ? m1 + " ms" : "não")}; período ecoado={floor} ms ({(o2 ? m2 + " ms" : "não")})", "ema 0,5; período ≥ 100", o1 && o2 ? "PASS" : "FAIL", "echo-latency.csv");
            }
            else
            {
                Skip("B3", "B3.13", "EMA/período da biomassa", "sem eco");
            }
        }

        Skip("B3", "B3.14", "eco não é sticky ao derrubar a rota", "manual (rota/nó)");

        {
            var before = _parser.Readings.Snapshot();
            var malformedBefore = _malformed;
            await _transport!.WriteAsync("{\"biomassIt\":9}", ct).ConfigureAwait(false);
            await ReadFramesAsync(TimeSpan.FromSeconds(6), int.MaxValue, ct).ConfigureAwait(false);
            var after = _parser.Readings.Snapshot();
            var unchanged = after.BiomassIntegrationTimeMs == before.BiomassIntegrationTimeMs;
            Add("B3", "B3.15", "fora de faixa não muda nada", $"inalterado={unchanged}; malformed={_malformed - malformedBefore}", "sem mudança, 0 malformed", unchanged && _malformed == malformedBefore ? "PASS" : "FAIL", "");
        }

        await WriteCsvAsync("echo-latency.csv", "node,key,latency_ms", latencies).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // B5 — soak e rajadas
    // ──────────────────────────────────────────────────────────────────────

    private async Task SuiteB5Async(CancellationToken ct)
    {
        Log("== B5 soak ==");
        Skip("B5", "B5.1", "linha de base Wi-Fi", "rodar `opentec-harness wifi-test <ip>` e copiar o relatório");

        if (options.SoakMinutes > 0)
        {
            var id = options.IsUsb ? "B5.2" : "B5.3";
            var file = options.IsUsb ? "soak-usb.csv" : "soak-wifi.csv";
            var rows = new List<string>();
            var sw = Stopwatch.StartNew();
            var frames = 0; var gaps = 0; long lastFrame = 0; var maxStreak = 0; long rttMax = 0; var rtts = new List<long>();
            var streakBefore = _malformed;
            var nextDiag = 0L;
            using var diag = options.IsUsb ? null : new HubNodeDiagClient();
            while (sw.Elapsed < TimeSpan.FromMinutes(options.SoakMinutes) && !ct.IsCancellationRequested)
            {
                var t0 = sw.ElapsedMilliseconds;
                var line = await _transport!.ReadAsync(ct).ConfigureAwait(false);
                if (line is not null && Track(line) == ParseOutcome.Updated)
                {
                    frames++;
                    if (lastFrame > 0 && t0 - lastFrame > 5000)
                    {
                        gaps++;
                    }

                    lastFrame = t0;
                    if (!options.IsUsb) { var rtt = sw.ElapsedMilliseconds - t0; rtts.Add(rtt); rttMax = Math.Max(rttMax, rtt); }
                }
                if (diag is not null && sw.ElapsedMilliseconds >= nextDiag)
                {
                    nextDiag = sw.ElapsedMilliseconds + 10_000;
                    var d = await diag.FetchAsync(_hubIp, ct).ConfigureAwait(false);
                    if (d is not null)
                    {
                        var streak = d.Nodes.Max(n => n.HubFailStreak ?? 0);
                        maxStreak = Math.Max(maxStreak, streak);
                        rows.Add(FormattableString.Invariant($"{sw.ElapsedMilliseconds},{frames},{gaps},{streak}"));
                    }
                }
                await Task.Delay(options.IsUsb ? 50 : 200, ct).ConfigureAwait(false);
            }
            await WriteCsvAsync(file, "t_ms,frames,gaps,hub_fail_streak_max", rows).ConfigureAwait(false);
            var expected = options.SoakMinutes * 30;
            var p95 = rtts.Count == 0 ? 0 : rtts.Order().ElementAt((int)(rtts.Count * 0.95));
            var measured = options.IsUsb
                ? $"{frames} quadros (esperado ≈ {expected}); gaps={gaps}; malformed={_malformed - streakBefore}"
                : $"{frames} quadros; gaps={gaps}; RTT p95={p95} ms; hub_fail_streak máx={maxStreak}";
            var pass = gaps == 0 && _malformed - streakBefore == 0 && frames >= expected * 0.9 && (options.IsUsb || (p95 < 250 && maxStreak <= 2));
            Add("B5", id, $"soak {options.SoakMinutes} min {(options.IsUsb ? "USB" : "Wi-Fi + varredura")}", measured,
                options.IsUsb ? "0 gaps, 0 malformed, ≥ 90 % dos quadros" : "0 gaps, p95 < 250 ms, streak ≤ 2", pass ? "PASS" : "FAIL", file);
            if (!options.IsUsb)
            {
                Add("B2", "B2.10", "varredura não custa push", $"hub_fail_streak máx={maxStreak}; gaps={gaps}", "≤ 2; 0", maxStreak <= 2 && gaps == 0 ? "PASS" : "FAIL", file);
            }
        }
        else
        {
            Skip("B5", "B5.2/B5.3", "soak", "passe --soak-min 30");
        }

        Skip("B5", "B5.4", "reconexão com estado", "manual (cabo) — `opentec-harness reset-test` cobre o pulso");
        Skip("B5", "B5.5", "curva do fluxômetro por USB (~300 B)", "usar o app: Calibrações › Vazão de ar (recibo de 11/09)");

        {
            var acksBefore = _acks;
            var okWrites = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                if (await _transport!.WriteAsync(CommandBuilders.DataDelay(2000).ToJson(), ct).ConfigureAwait(false))
                {
                    okWrites++;
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            var frames = await ReadFramesAsync(TimeSpan.FromSeconds(6), int.MaxValue, ct).ConfigureAwait(false);
            var acks = options.IsUsb ? _acks - acksBefore : okWrites;
            Add("B5", "B5.6", "rajada de 10 comandos em 2 s", $"aceitos={okWrites}; acks={acks}; quadros depois={frames.Count}; malformed={_malformed}", "10 OK, telemetria segue",
                okWrites == 10 && acks >= 10 && frames.Count >= 1 ? "PASS" : okWrites == 10 && frames.Count >= 1 ? "WARN" : "FAIL", "");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static ExternalNodeIdentity NodeOf(SensorReadings r, string dev) => dev switch
    {
        "distance" => r.DistanceNode,
        "agitator" => r.AgitatorNode,
        "pump" => r.PumpNode,
        "flowmeter" => r.FlowmeterNode,
        "biomass" => r.BiomassNode,
        _ => ExternalNodeIdentity.Empty,
    };

    private static string DescribePresence(List<SensorSnapshot> frames)
    {
        if (frames.Count == 0)
        {
            return "nenhum quadro";
        }

        var last = frames[^1];
        return $"distance={last.DistanceOnline} agitator={last.AgitatorOnline} pump={last.PumpOnline} flowmeter={last.FlowmeterOnline} biomass={last.BiomassOnline} ({frames.Count} quadros)";
    }

    private static int ItCode(int ms) => ms switch { 25 => 0, 50 => 1, 100 => 2, 200 => 3, 400 => 4, 800 => 5, _ => 2 };

    private static long? ExtractNumber(string line, string marker)
    {
        var i = line.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var start = i + marker.Length;
        var end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
        {
            end++;
        }

        return long.TryParse(line.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private ParseOutcome Track(string line)
    {
        var outcome = _parser.Parse(line);
        switch (outcome)
        {
            case ParseOutcome.Updated: _lastRawFrame = line; break;
            case ParseOutcome.NodeDiag: _nodeDiagLines.Add(line); break;
            case ParseOutcome.CommandAck: _acks++; break;
            case ParseOutcome.Malformed: _malformed++; Log($"  malformed: {Truncate(line)}"); break;
        }
        return outcome;
    }

    private async Task<List<SensorSnapshot>> ReadFramesAsync(TimeSpan window, int maxFrames, CancellationToken ct)
    {
        var list = new List<SensorSnapshot>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window && list.Count < maxFrames)
        {
            var line = await _transport!.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(options.IsUsb ? 50 : 200, ct).ConfigureAwait(false); continue; }
            if (Track(line) == ParseOutcome.Updated)
            {
                list.Add(_parser.Readings.Snapshot());
            }
        }
        return list;
    }

    private async Task<List<(long ms, string line)>> ReadRawAsync(TimeSpan window, int maxFrames, CancellationToken ct)
    {
        var list = new List<(long, string)>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window && list.Count < maxFrames)
        {
            var line = await _transport!.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(options.IsUsb ? 50 : 200, ct).ConfigureAwait(false); continue; }
            if (Track(line) == ParseOutcome.Updated)
            {
                list.Add((sw.ElapsedMilliseconds, line));
            }
        }
        return list;
    }

    private Task<bool> Send(OpenTECCommand command, CancellationToken ct)
    {
        Log($"  → {command.ToJson()}");
        return _transport!.WriteAsync(command.ToJson(), ct);
    }

    /// <summary>Sends, then waits for <paramref name="matches"/>; also reports whether <paramref name="pendingProbe"/> was ever true.</summary>
    private async Task<(bool ok, long ms, bool pendingSeen)> EchoAsync(
        OpenTECCommand command,
        Func<SensorSnapshot, bool> matches,
        TimeSpan timeout,
        Func<SensorSnapshot, bool>? pendingProbe,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!await Send(command, ct).ConfigureAwait(false))
        {
            return (false, sw.ElapsedMilliseconds, false);
        }
        var pendingSeen = false;
        while (sw.Elapsed < timeout)
        {
            var line = await _transport!.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(options.IsUsb ? 50 : 200, ct).ConfigureAwait(false); continue; }
            if (Track(line) != ParseOutcome.Updated)
            {
                continue;
            }

            var s = _parser.Readings.Snapshot();
            if (pendingProbe is not null && pendingProbe(s))
            {
                pendingSeen = true;
            }

            if (matches(s))
            {
                return (true, sw.ElapsedMilliseconds, pendingSeen);
            }
        }
        return (false, sw.ElapsedMilliseconds, pendingSeen);
    }

    private async Task WaitPendingClearAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(12))
        {
            var line = await _transport!.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
            if (Track(line) == ParseOutcome.Updated && _parser.Readings.BiomassCommandPending != true)
            {
                return;
            }
        }
    }

    private void Remember(string what, Func<CancellationToken, Task> restore) => _restores.Add((what, restore));

    private async Task RestoreAllAsync()
    {
        if (_restores.Count == 0 || _transport is null || !_transport.IsConnected)
        {
            return;
        }

        Log("== restaurando ==");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        for (var i = _restores.Count - 1; i >= 0; i--)
        {
            var (what, restore) = _restores[i];
            try
            {
                await restore(cts.Token).ConfigureAwait(false);
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
                Log($"  restaurado: {what}");
            }
            catch (Exception ex)
            {
                Log($"  NÃO restaurado: {what} — {ex.Message}");
                Add("—", "R", $"restaurar {what}", ex.Message, "restaurado", "FAIL", "");
            }
        }
    }

    private async Task<string?> GetAsync(string pathOrUrl, CancellationToken ct, bool absolute = false)
    {
        if (_http is null)
        {
            return null;
        }

        try
        {
            var url = absolute ? pathOrUrl : $"http://{_hubIp}{pathOrUrl}";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"  GET {pathOrUrl}: {ex.Message}");
            return null;
        }
    }

    private void Add(string suite, string id, string name, string measured, string limit, string verdict, string evidence)
    {
        _rows.Add(new Row(suite, id, name, measured, limit, verdict, evidence));
        Log($"[{verdict,-4}] {id,-7} {name}: {measured}  (limite: {limit})");
    }

    private void Skip(string suite, string id, string name, string why) => Add(suite, id, name, "—", "—", "SKIP", why);

    private void Log(string text)
    {
        Console.WriteLine(text);
        _log.AppendLine(text);
    }

    private static string Truncate(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private Task WriteCsvAsync(string name, string header, IEnumerable<string> rows)
        => File.WriteAllLinesAsync(Path.Combine(options.OutputDirectory, name), [header, .. rows]);

    private Task WriteJsonAsync(string name, object value)
        => File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private async Task WriteReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Recibo de bancada — bench-test");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Data:** {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Alvo:** `{options.Target}` ({(options.IsUsb ? "USB" : "Wi-Fi")}) · Hub `{_parser.Readings.HubFirmwareVersion ?? "?"}` · protocolo {_parser.Readings.HubProtocolVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Máquina:** {Environment.MachineName} · cultura {CultureInfo.CurrentCulture.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Resultado:** {_rows.Count(r => r.Verdict == "PASS")} PASS · {_rows.Count(r => r.Verdict == "WARN")} WARN · {_rows.Count(r => r.Verdict == "FAIL")} FAIL · {_rows.Count(r => r.Verdict == "SKIP")} SKIP");
        sb.AppendLine();
        sb.AppendLine("Especificação: `docs/processes/TESTES_AUTOMATICOS_BANCADA.md`.");
        foreach (var group in _rows.GroupBy(r => r.Suite))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| # | Teste | Medido | Limite | Veredito | Evidência |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in group)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Id} | {r.Name} | {Md(r.Measured)} | {Md(r.Limit)} | **{r.Verdict}** | {Md(r.Evidence)} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Log");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.Append(_log);
        sb.AppendLine("```");
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report.md"), sb.ToString()).ConfigureAwait(false);

        static string Md(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
