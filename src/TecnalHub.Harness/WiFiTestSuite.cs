using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;

namespace TecnalHub.Harness;

/// <summary>
/// Unattended Wi-Fi validation for Phase 0.
/// </summary>
/// <remarks>
/// <para>
/// Runs standalone: joining the ESP32's access point costs the machine its internet
/// connection, so nobody can be watching. Every step therefore records what it tried,
/// what came back, and keeps going - one failure must not hide the results of the
/// rest, because re-running means another round trip of losing connectivity.
/// </para>
/// <para>
/// Raw HTTP probes run <b>before</b> anything goes through
/// <see cref="HttpTransport"/>, so a failure can be attributed to the network or the
/// device rather than to our transport.
/// </para>
/// </remarks>
internal sealed class WiFiTestSuite(string ipAddress, string outputDirectory, ILoggerFactory loggerFactory)
{
    private const string ExpectedSsid = "Modulo_TECNAL_1";

    private readonly StringBuilder _report = new();

    /// <summary>
    /// Set by step 2. When the device does not answer a single raw request there is
    /// nothing to learn from the later steps, and each of them would sit through its
    /// own timeouts - about four minutes in total. Someone standing at a machine with
    /// no internet should find out in twenty seconds that they are on the wrong
    /// network, not four minutes.
    /// </summary>
    private bool _deviceReachable;

    private int _passed;
    private int _failed;
    private int _warned;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        Header($"TECNAL-Hub Phase 0 - Wi-Fi validation");
        Line($"started      : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"target       : http://{ipAddress}");
        Line($"machine      : {Environment.MachineName}");
        Line($"culture      : {CultureInfo.CurrentCulture.Name} " +
             $"(decimal '{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}')");
        Line($"output       : {outputDirectory}");
        Line("");

        // Order matters: environment, then raw HTTP, then our stack. If the raw
        // probes fail there is no point blaming the transport.
        await Step1_EnvironmentAsync().ConfigureAwait(false);
        await Step2_RawHttpAsync(cancellationToken).ConfigureAwait(false);
        if (_deviceReachable)
        {
            await Step3_EtagBehaviourAsync(cancellationToken).ConfigureAwait(false);
            await Step4_TransportAsync(cancellationToken).ConfigureAwait(false);
            await Step5_LatencyAsync(cancellationToken).ConfigureAwait(false);
            await Step6_SoakAsync(cancellationToken).ConfigureAwait(false);
            await Step7_ReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Header("Steps 3-7 skipped");
            Line("  The device did not answer any raw HTTP request, so the remaining");
            Line("  steps would only time out. Check that:");
            Line("");
            Line($"    - Windows Wi-Fi is connected to {ExpectedSsid}");
            Line("    - Windows has not silently fallen back to a network with internet");
            Line("    - the machine has a 192.168.4.x address (see section 1)");
            Line("    - the device is powered and its access point is up");
            Line("");
            Line("  Then run this test again.");
        }

        Header("Summary");
        Line($"passed  : {_passed}");
        Line($"warned  : {_warned}");
        Line($"failed  : {_failed}");
        Line("");
        Line(_failed == 0
            ? "RESULT: Wi-Fi transport validated."
            : "RESULT: FAILURES PRESENT - see the sections above.");

        var reportPath = Path.Combine(outputDirectory, "wifi-report.txt");
        await File.WriteAllTextAsync(reportPath, _report.ToString(), cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Report written to {reportPath}");

        return _failed == 0 ? 0 : 1;
    }

    // ==================================================================
    // Step 1 - environment
    // ==================================================================

    private async Task Step1_EnvironmentAsync()
    {
        Header("1. Network environment");

        try
        {
            // Windows exposes a filter-driver pseudo-interface per real adapter
            // (QoS Packet Scheduler, WFP MAC Layer, ...). They never carry an
            // address and would bury the one line that matters.
            var wifiInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                .ToList();

            if (wifiInterfaces.Count == 0)
            {
                Warn("no network interface holds an IPv4 address - is Wi-Fi connected at all?");
            }

            foreach (var nic in wifiInterfaces)
            {
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString());
                var gateways = props.GatewayAddresses
                    .Select(g => g.Address.ToString())
                    .Where(a => a != "0.0.0.0" && !a.Contains(':', StringComparison.Ordinal));

                Line($"  {nic.Name} [{nic.NetworkInterfaceType}]");
                Line($"    addresses : {string.Join(", ", v4)}");
                Line($"    gateways  : {string.Join(", ", gateways)}");
            }

            // The SoftAP hands out 192.168.4.x; seeing that is the strongest signal
            // the machine actually joined the right network.
            var onSoftAp = wifiInterfaces
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.ToString().StartsWith("192.168.4.", StringComparison.Ordinal));

            Line("");
            if (onSoftAp)
            {
                Pass($"machine holds a 192.168.4.x address (expected on the {ExpectedSsid} AP)");
            }
            else
            {
                Warn($"no 192.168.4.x address found - is the machine joined to {ExpectedSsid}? " +
                     "The tests below will show whether the device is reachable anyway.");
            }
        }
        catch (Exception ex)
        {
            Warn($"could not enumerate network interfaces: {ex.Message}");
        }

        // A configured system proxy is a classic cause of "works in the browser,
        // fails in the app". v.6 bypasses it explicitly and so do we; this records
        // whether one is present at all.
        try
        {
            var proxy = WebRequest.DefaultWebProxy?.GetProxy(new Uri($"http://{ipAddress}"));
            if (proxy is not null && !proxy.Host.Equals(ipAddress, StringComparison.OrdinalIgnoreCase))
            {
                Warn($"system proxy would route to {proxy} - our HttpClient bypasses it (UseProxy=false)");
            }
            else
            {
                Pass("no system proxy interferes with the device address");
            }
        }
        catch (Exception ex)
        {
            Line($"  proxy check inconclusive: {ex.Message}");
        }

        // ICMP is not part of the protocol, but it separates "no route" from
        // "route exists, HTTP is the problem".
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                Pass($"ICMP reachable in {reply.RoundtripTime} ms");
            }
            else
            {
                Warn($"ICMP did not answer ({reply.Status}) - the ESP32 may simply not reply to ping");
            }
        }
        catch (Exception ex)
        {
            Warn($"ICMP probe failed: {ex.Message}");
        }
    }

    // ==================================================================
    // Step 2 - raw HTTP, bypassing our transport
    // ==================================================================

    private async Task Step2_RawHttpAsync(CancellationToken cancellationToken)
    {
        Header("2. Raw HTTP endpoints (not using HttpTransport)");

        using var client = BareClient(TimeSpan.FromSeconds(5));

        await ProbeAsync(client, HttpMethod.Get, "/ping", null, cancellationToken).ConfigureAwait(false);
        await ProbeAsync(client, HttpMethod.Get, "/readData", null, cancellationToken).ConfigureAwait(false);
        await ProbeAsync(client, HttpMethod.Post, "/command", """{"comTest":1}""", cancellationToken).ConfigureAwait(false);

        // Not part of the contract; recorded only to learn what else the firmware
        // exposes, which may be useful later.
        Line("");
        Line("  Endpoint discovery (informational, 404 is a fine answer):");
        foreach (var path in new[] { "/", "/status", "/info", "/config" })
        {
            await ProbeAsync(client, HttpMethod.Get, path, null, cancellationToken, informational: true)
                .ConfigureAwait(false);
        }
    }

    private async Task ProbeAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? body,
        CancellationToken cancellationToken,
        bool informational = false)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(method, $"http://{ipAddress}{path}");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            stopwatch.Stop();

            var etag = response.Headers.ETag?.Tag ?? "-";
            var preview = text.Length > 400 ? text[..400] + "..." : text;

            Line($"  {method,-4} {path,-12} -> {(int)response.StatusCode} " +
                 FormattableString.Invariant($"in {stopwatch.ElapsedMilliseconds} ms, ETag={etag}"));
            Line($"       body: {preview}");

            if (informational)
            {
                return;
            }

            switch (path)
            {
                case "/ping" when response.IsSuccessStatusCode:
                    _deviceReachable = true;
                    Pass("/ping answers 200");
                    break;
                case "/ping":
                    Fail($"/ping returned {(int)response.StatusCode}");
                    break;

                case "/readData" when response.IsSuccessStatusCode && text.StartsWith('{'):
                    _deviceReachable = true;
                    Pass("/readData returns a JSON object");
                    break;
                case "/readData":
                    Fail($"/readData returned {(int)response.StatusCode} with body: {preview}");
                    break;

                case "/command" when text == "OK":
                    _deviceReachable = true;
                    Pass("""/command accepts {"comTest":1} and answers OK""");
                    break;
                case "/command":
                    Fail($"/command answered '{text}' rather than OK");
                    break;
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Line(FormattableString.Invariant(
                $"  {method,-4} {path,-12} -> EXCEPTION after {stopwatch.ElapsedMilliseconds} ms"));
            Line($"       {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                Line($"       inner: {inner.GetType().Name}: {inner.Message}");
            }

            if (!informational)
            {
                Fail($"{method} {path} threw {ex.GetType().Name}");
            }
        }
    }

    // ==================================================================
    // Step 3 - ETag / 304, the part most likely to be wrong
    // ==================================================================

    private async Task Step3_EtagBehaviourAsync(CancellationToken cancellationToken)
    {
        Header("3. ETag conditional polling");
        Line("  v.6 relies on If-None-Match; a 304 means 'unchanged', not an error.");
        Line("  Getting this wrong shows up as telemetry that silently stops updating.");
        Line("");

        using var client = BareClient(TimeSpan.FromSeconds(5));

        try
        {
            using var first = await client.GetAsync($"http://{ipAddress}/readData", cancellationToken)
                                          .ConfigureAwait(false);
            var etag = first.Headers.ETag;

            if (etag is null)
            {
                Warn("device sent no ETag - conditional polling is unavailable; " +
                     "every poll will transfer the full body (works, just less efficient)");
                return;
            }

            Pass($"device supplies an ETag: {etag.Tag}");

            // Immediately re-request with the tag. The device should either answer
            // 304, or 200 with a new tag if telemetry moved on in the meantime.
            using var conditional = new HttpRequestMessage(HttpMethod.Get, $"http://{ipAddress}/readData");
            conditional.Headers.IfNoneMatch.Add(etag);

            using var second = await client.SendAsync(conditional, cancellationToken).ConfigureAwait(false);

            if (second.StatusCode == HttpStatusCode.NotModified)
            {
                Pass("conditional re-request answers 304 as expected");
            }
            else if (second.IsSuccessStatusCode)
            {
                var newEtag = second.Headers.ETag?.Tag ?? "-";
                Line($"  conditional request answered 200 with ETag={newEtag}");
                if (newEtag != etag.Tag)
                {
                    Pass("200 with a NEW ETag - telemetry advanced between the two reads, which is fine");
                }
                else
                {
                    Warn("200 with the SAME ETag - the device ignores If-None-Match; " +
                         "harmless, but conditional polling saves nothing");
                }
            }
            else
            {
                Fail($"conditional request returned {(int)second.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Fail($"ETag probe threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==================================================================
    // Step 4 - our HttpTransport
    // ==================================================================

    private async Task Step4_TransportAsync(CancellationToken cancellationToken)
    {
        Header("4. HttpTransport");

        var config = new HttpTransportConfig { IpAddress = ipAddress };
        await using var transport = new HttpTransport(config, loggerFactory.CreateLogger<HttpTransport>());

        try
        {
            var connected = await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (connected)
            {
                Pass("handshake succeeded with the default timeouts " +
                     FormattableString.Invariant(
                         $"(connect {config.ConnectTimeout.TotalMilliseconds:F0} ms, read {config.ReadTimeout.TotalMilliseconds:F0} ms)"));
            }
            else
            {
                Fail("handshake failed with the default timeouts");

                // The v.6 defaults are aggressive - 250 ms to connect. If they are
                // simply too tight for this link, that is worth knowing precisely,
                // because it is a one-line config change rather than a redesign.
                Line("  retrying with relaxed timeouts to distinguish 'too tight' from 'unreachable'...");
                var relaxed = config with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    ReadTimeout = TimeSpan.FromSeconds(3),
                    HandshakeAttempts = 5,
                };

                await using var relaxedTransport = new HttpTransport(
                    relaxed, loggerFactory.CreateLogger<HttpTransport>());

                if (await relaxedTransport.ConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    Warn("RELAXED TIMEOUTS WORK - the default 250 ms connect / 750 ms read is too " +
                         "aggressive for this link. Raise HttpTransportConfig defaults.");
                    await relaxedTransport.DisconnectAsync().ConfigureAwait(false);
                }
                else
                {
                    Fail("relaxed timeouts also failed - the device is not answering at all");
                }

                return;
            }

            // Read. The first call can legitimately return null: the poll period has
            // not elapsed yet, or the device answered 304.
            var attempts = 0;
            string? frame = null;
            while (attempts++ < 10 && frame is null)
            {
                frame = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }

            if (frame is not null)
            {
                Pass($"read returned a frame after {attempts} attempt(s)");
                Line($"       {(frame.Length > 300 ? frame[..300] + "..." : frame)}");

                var parser = new TelemetryParser();
                var outcome = parser.Parse(frame);
                if (outcome == ParseOutcome.Updated)
                {
                    Pass("frame parsed as telemetry");
                    Line(FormattableString.Invariant(
                        $"       time={parser.Readings.TimeRawSeconds:F1}s sensorOk={parser.Readings.SensorCommOk}"));
                }
                else
                {
                    Fail($"frame did not parse as telemetry (outcome: {outcome})");
                }
            }
            else
            {
                Fail("read returned null on all 10 attempts over ~5 s");
            }

            // Write path. Deliberately a no-op: dataDelay set to the value already in
            // the field, so nothing on the device changes.
            var written = await transport
                .WriteAsync(CommandBuilders.DataDelay(2000).ToJson(), cancellationToken)
                .ConfigureAwait(false);

            if (written)
            {
                Pass("""write accepted: {"dataDelay":2000} answered OK""");
            }
            else
            {
                Fail("write was not acknowledged with OK");
            }

            if (await transport.TestConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                Pass("liveness probe (/ping) succeeded");
            }
            else
            {
                Fail("liveness probe (/ping) failed");
            }
        }
        catch (Exception ex)
        {
            Fail($"transport step threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==================================================================
    // Step 5 - latency
    // ==================================================================

    private async Task Step5_LatencyAsync(CancellationToken cancellationToken)
    {
        Header("5. Round-trip latency (20 x POST /command)");
        Line("  Informs whether the v.6 default timeouts leave enough headroom.");
        Line("");

        using var client = BareClient(TimeSpan.FromSeconds(5));
        var samples = new List<double>();
        var failures = 0;

        for (var i = 0; i < 20; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/command")
                {
                    Content = new StringContent("""{"comTest":1}""", Encoding.UTF8, "application/json"),
                };

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                else
                {
                    failures++;
                }
            }
            catch (Exception)
            {
                failures++;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        if (samples.Count == 0)
        {
            Fail($"all 20 latency probes failed");
            return;
        }

        samples.Sort();
        var min = samples[0];
        var median = samples[samples.Count / 2];
        var max = samples[^1];
        var p95 = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))];

        Line(FormattableString.Invariant(
            $"  min={min:F0} ms  median={median:F0} ms  p95={p95:F0} ms  max={max:F0} ms  ({samples.Count}/20 ok, {failures} failed)"));

        if (failures > 0)
        {
            Warn($"{failures} of 20 probes failed - the link is lossy");
        }
        else
        {
            Pass("all 20 probes answered");
        }

        // The roadmap budgets <100 ms for a USB command round trip; Wi-Fi is
        // allowed to be slower, but it must comfortably fit the configured timeouts.
        var writeTimeoutMs = new HttpTransportConfig().WriteTimeout.TotalMilliseconds;
        if (p95 > writeTimeoutMs)
        {
            Warn(FormattableString.Invariant(
                $"p95 ({p95:F0} ms) exceeds the configured write timeout ({writeTimeoutMs:F0} ms) - raise it"));
        }
        else
        {
            Pass(FormattableString.Invariant(
                $"p95 ({p95:F0} ms) fits inside the write timeout ({writeTimeoutMs:F0} ms)"));
        }
    }

    // ==================================================================
    // Step 6 - soak through the full stack
    // ==================================================================

    private async Task Step6_SoakAsync(CancellationToken cancellationToken)
    {
        const int soakSeconds = 90;
        Header($"6. {soakSeconds} s soak through ConnectionManager");

        using var trace = new WireTrace(Path.Combine(outputDirectory, "wifi-soak-trace.log"));

        await using var manager = new ConnectionManager(loggerFactory: loggerFactory);

        var states = new List<string>();
        var frames = 0;
        var firstFrameAt = (DateTimeOffset?)null;

        manager.StateChanged += change =>
        {
            states.Add($"{DateTimeOffset.Now:HH:mm:ss} {change.State} {change.Reason}".TrimEnd());
            trace.State(change);
        };

        manager.TelemetryReceived += snapshot =>
        {
            frames++;
            firstFrameAt ??= DateTimeOffset.Now;
            trace.Rx(snapshot);
        };

        manager.DeviceLogReceived += trace.DeviceLog;

        var started = DateTimeOffset.Now;
        manager.ConnectWiFi(new HttpTransportConfig { IpAddress = ipAddress });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(soakSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Line("  soak interrupted");
        }

        manager.Disconnect();
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);

        var diagnostics = manager.Diagnostics;

        Line("  state transitions:");
        foreach (var state in states)
        {
            Line($"    {state}");
        }

        Line("");
        Line($"  frames received : {diagnostics.FramesReceived}");
        Line($"  commands sent   : {diagnostics.CommandsSent}");
        Line($"  parse failures  : {diagnostics.ParseFailures}");
        Line($"  device log lines: {diagnostics.DeviceLogLines}");
        Line($"  command acks    : {diagnostics.CommandAcks}");
        Line($"  connect attempts: {diagnostics.ConnectAttemptsWiFi}");
        Line($"  last error      : {(string.IsNullOrEmpty(diagnostics.LastError) ? "-" : diagnostics.LastError)}");

        if (firstFrameAt is { } first)
        {
            Line(FormattableString.Invariant(
                $"  time to first frame: {(first - started).TotalSeconds:F1} s"));
        }

        Line("");

        if (diagnostics.FramesReceived == 0)
        {
            Fail("no telemetry frames received during the soak");
        }
        else
        {
            // The device emits about every 2 s and the transport floors polling at
            // 1 s, so roughly soakSeconds/2 frames is the realistic ceiling.
            var expected = soakSeconds / 2.0;
            var ratio = diagnostics.FramesReceived / expected;

            Pass(FormattableString.Invariant(
                $"{diagnostics.FramesReceived} frames in {soakSeconds} s ({ratio * 100:F0}% of the ~{expected:F0} expected)"));

            if (ratio < 0.5)
            {
                Warn("frame rate is well below expectation - frames are being dropped");
            }
        }

        if (diagnostics.ParseFailures > 0)
        {
            Fail($"{diagnostics.ParseFailures} parse failures during the soak");
        }
        else
        {
            Pass("no parse failures");
        }

        if (diagnostics.ConnectAttemptsWiFi > 1)
        {
            Warn($"{diagnostics.ConnectAttemptsWiFi} connect attempts - the link dropped during the soak");
        }
        else
        {
            Pass("link stayed up on a single connect");
        }
    }

    // ==================================================================
    // Step 7 - reconnect
    // ==================================================================

    private async Task Step7_ReconnectAsync(CancellationToken cancellationToken)
    {
        Header("7. Disconnect / reconnect");
        Line("  On reconnect the cached ETag must be cleared, or the first poll");
        Line("  answers 304 forever and telemetry never resumes.");
        Line("");

        var config = new HttpTransportConfig { IpAddress = ipAddress };
        await using var transport = new HttpTransport(config, loggerFactory.CreateLogger<HttpTransport>());

        try
        {
            if (!await transport.ConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                Fail("initial connect failed, cannot test reconnect");
                return;
            }

            // Read once so an ETag is definitely cached.
            for (var i = 0; i < 5; i++)
            {
                if (await transport.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    break;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            await transport.DisconnectAsync().ConfigureAwait(false);
            Pass("disconnected cleanly");

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            if (!await transport.ConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                Fail("reconnect failed");
                return;
            }

            Pass("reconnected");

            string? frame = null;
            for (var i = 0; i < 10 && frame is null; i++)
            {
                frame = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }

            if (frame is not null)
            {
                Pass("telemetry resumed after reconnect (ETag correctly cleared)");
            }
            else
            {
                Fail("no telemetry after reconnect - the stale ETag is likely being resent");
            }
        }
        catch (Exception ex)
        {
            Fail($"reconnect step threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>
    /// A plain client for the raw probes, deliberately not the one
    /// <see cref="HttpTransport"/> builds - these steps must be able to succeed even
    /// if our transport is broken.
    /// </summary>
    private static HttpClient BareClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = false,
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private void Header(string text)
    {
        Line("");
        Line(new string('=', 70));
        Line(text);
        Line(new string('=', 70));
    }

    private void Pass(string text)
    {
        _passed++;
        Line($"  [PASS] {text}");
    }

    private void Warn(string text)
    {
        _warned++;
        Line($"  [WARN] {text}");
    }

    private void Fail(string text)
    {
        _failed++;
        Line($"  [FAIL] {text}");
    }

    private void Line(string text)
    {
        Console.WriteLine(text);
        _report.AppendLine(text);
    }
}
