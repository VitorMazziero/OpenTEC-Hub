using System.Globalization;
using System.Net;
using System.Text;

namespace OpenTECHub.Simulator;

/// <summary>
/// Serves the ESP32's Wi-Fi surface over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The app-facing surface of the real device is <c>/ping</c>, <c>/readData</c>,
/// <c>/command</c>, <c>/nodes</c> and, from Hub 10.2, cached <c>/nodeDiag</c>. Everything else
/// answers 404 <c>Not found</c>, confirmed by the hardware validation run. (The Hub has
/// further routes for the nodes themselves - <c>/nodeHello</c>, <c>/pumpData</c>… - which
/// the app never calls and the simulator does not serve.)
/// </para>
/// <para>
/// Pointing the app at <c>127.0.0.1</c> needs no driver, no administrator rights and
/// no reboot, which is why this is the default way to run the simulator.
/// </para>
/// </remarks>
public sealed class HttpEndpoint(DeviceModel model, int port, Action<string> log) : IDisposable
{
    private readonly HttpListener _listener = new();

    /// <summary>
    /// The response <c>/readData</c> will serve next, in place of telemetry.
    /// </summary>
    /// <remarks>
    /// <b>This reproduces a real firmware quirk.</b> The device appears to serve
    /// <c>/readData</c> from a shared response buffer, so the first GET after a
    /// <c>POST /command</c> returns that command's <c>OK</c> rather than a telemetry
    /// frame. Because the Wi-Fi handshake <i>is</i> a POST, this happens on every
    /// single connect - measured on hardware 2026-08-19. A client that assumes the
    /// first frame after connect is telemetry will report a spurious failure, so the
    /// simulator must reproduce it rather than be polite.
    /// </remarks>
    private string? _bufferedResponse;

    /// <summary>
    /// The telemetry frame currently being served, and its tag.
    /// </summary>
    /// <remarks>
    /// The device publishes a <b>new frame every dataDelay</b>, not per request, and
    /// the ETag identifies that frame. Polls arriving between frames legitimately get
    /// 304. Deriving the tag per response instead is wrong in both directions: it
    /// either defeats conditional polling entirely, or - as an earlier version of this
    /// file did - pins the tag at the first frame and answers 304 forever while the
    /// process keeps moving, which is indistinguishable from a stalled device.
    /// </remarks>
    private string _frame = "{}";

    private string _etag = "\"0\"";
    private int _frameCounter;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _framePump;

    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _listener.Prefixes.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
        _listener.Prefixes.Add($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/");
        _listener.Start();

        log($"HTTP listening on http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
        log("Point the app at 127.0.0.1 (connection chip -> Wi-Fi).");

        PublishFrame();

        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        _framePump = Task.Run(() => FramePumpAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Publishes a fresh telemetry frame every <c>dataDelay</c>.</summary>
    private async Task FramePumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(model.DataDelayMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Stall deliberately stops publishing while the server stays up, so the
            // client sees 304 forever - the exact case the silence timeout exists for.
            if (model.Scenario is Scenario.Stall or Scenario.Dropout)
            {
                continue;
            }

            PublishFrame();
        }
    }

    private void PublishFrame()
    {
        _frame = model.Scenario == Scenario.Garbage
            ? "{malformed,,,"
            : WireCodec.BuildTelemetry(model);

        _etag = "\"" + (++_frameCounter).ToString(CultureInfo.InvariantCulture) + "\"";
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }

            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                log($"HTTP handler error: {ex.Message}");
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        // Dropout: accept the connection then refuse to answer, as an AP that has
        // gone away would.
        if (model.Scenario == Scenario.Dropout)
        {
            context.Response.Abort();
            return;
        }

        switch (path)
        {
            case "/ping":
                Respond(context, 200, "pong");
                break;

            case "/readData":
                HandleReadData(context);
                break;

            case "/command":
                HandleCommand(context);
                break;

            case "/nodes":
                HandleNodes(context);
                break;

            case "/nodeDiag":
                HandleNodeDiag(context);
                break;

            default:
                Respond(context, 404, "Not found");
                break;
        }
    }

    /// <summary>The Hub 10.1 node directory, in the shape <c>WIRE_CONTRACT_V9.md</c> documents.</summary>
    private void HandleNodes(HttpListenerContext context)
    {
        if (!model.PublishesNodeIdentity)
        {
            Respond(context, 404, "Not found");
            return;
        }

        var only = context.Request.QueryString["dev"];
        var now = (long)(model.UptimeSeconds * 1000.0);
        var rows = new List<string>();
        foreach (var (device, _) in DeviceModel.RegistryNodes)
        {
            if (!string.IsNullOrEmpty(only) && only != device)
            {
                continue;
            }

            var registered = model.NodeRegistered(device);
            var lastSeen = registered ? Math.Max(0, now - 400) : 0;
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"{{\"dev\":\"{device}\",\"ip\":\"{model.NodeIp(device)}\",\"mac\":\"{(registered ? DeviceModel.NodeMac(device) : "")}\"," +
                $"\"version\":\"{(registered ? DeviceModel.NodeVersion(device) : "")}\",\"online\":{(registered ? "true" : "false")}," +
                $"\"age_ms\":{(registered ? now - lastSeen : 999999)},\"registered\":{(registered ? "true" : "false")}," +
                $"\"last_hello_ms\":{lastSeen},\"last_data_ms\":{lastSeen}}}"));
        }

        Respond(context, 200, string.Create(CultureInfo.InvariantCulture, $"{{\"hub_time_ms\":{now},\"nodes\":[{string.Join(",", rows)}]}}"));
    }

    private void HandleNodeDiag(HttpListenerContext context)
    {
        if (!model.PublishesNodeIdentity)
        {
            Respond(context, 404, "Not found");
            return;
        }

        var only = context.Request.QueryString["dev"];
        var now = (long)(model.UptimeSeconds * 1000.0);
        var rows = new List<string>();
        foreach (var (device, _) in DeviceModel.RegistryNodes)
        {
            if (!string.IsNullOrEmpty(only) && only != device)
            {
                continue;
            }
            var registered = model.NodeRegistered(device);
            var diag = registered ? SimulatorDiag(device) : "null";
            rows.Add($"{{\"dev\":\"{device}\",\"code\":{(registered ? 200 : 0)},\"age_ms\":{(registered ? 400 : 999999)},\"diag\":{diag}}}");
        }
        Respond(context, 200, string.Create(CultureInfo.InvariantCulture, $"{{\"hub_time_ms\":{now},\"nodes\":[{string.Join(",", rows)}]}}"));
    }

    private string SimulatorDiag(string device)
    {
        var common = string.Create(CultureInfo.InvariantCulture,
            $"\"uptime_s\":{model.UptimeSeconds:F0},\"free_heap\":210000,\"rssi\":-58,\"hub_fail_streak\":0,\"ota\":false");
        var extra = device switch
        {
            "distance" => string.Create(CultureInfo.InvariantCulture, $"\"distance\":118,\"sample_time\":{model.UptimeSeconds:F1},\"offset_mm\":{model.DistanceOffsetMm:F2}"),
            "agitator" => string.Create(CultureInfo.InvariantCulture, $"\"duty\":{model.AgitatorPercent:F1},\"dir\":{(model.AgitatorClockwise ? 1 : 0)},\"pot\":{(model.AgitatorPotActive ? "true" : "false")}"),
            "pump" => string.Create(CultureInfo.InvariantCulture, $"\"flow\":1.250,\"vol\":{model.PumpVolume:F3},\"mode\":{model.PumpMode}"),
            "flowmeter" => string.Create(CultureInfo.InvariantCulture, $"\"flow_rate\":{model.ReadFlow():F4},\"flow_sp\":{model.FlowSetpoint:F4}"),
            "biomass" => "\"absorbance\":0.421,\"raw\":24500,\"state\":1",
            _ => "",
        };
        return $"{{{common},{extra}}}";
    }

    private void HandleReadData(HttpListenerContext context)
    {
        // Serve the buffered command acknowledgement first - see _bufferedResponse.
        if (_bufferedResponse is { } buffered)
        {
            _bufferedResponse = null;
            Respond(context, 200, buffered);
            return;
        }

        // The client already holds the current frame: nothing new to send. Under the
        // Stall scenario no new frame is ever published, so this becomes permanent -
        // which is precisely the failure the app's silence timeout must catch.
        if (MatchesCachedEtag(context))
        {
            Respond(context, 304, "");
            return;
        }

        context.Response.Headers["ETag"] = _etag;
        Respond(context, 200, _frame);
    }

    private void HandleCommand(HttpListenerContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        if (!WireCodec.ApplyCommand(model, body, out var wasHandshake))
        {
            log($"  <- unparseable command: {body}");
            Respond(context, 400, "ERR");
            return;
        }

        log(wasHandshake ? "  <- handshake" : $"  <- {body}");

        // Leave the ack in the shared buffer, as the firmware does.
        _bufferedResponse = "OK";
        Respond(context, 200, "OK");
    }

    private bool MatchesCachedEtag(HttpListenerContext context)
    {
        var ifNoneMatch = context.Request.Headers["If-None-Match"];
        return !string.IsNullOrEmpty(ifNoneMatch) &&
               string.Equals(ifNoneMatch, _etag, StringComparison.Ordinal);
    }

    private static void Respond(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = bytes.Length;

        try
        {
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            // Client hung up mid-response; nothing useful to do.
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
            _listener.Close();
            _loop?.Wait(TimeSpan.FromSeconds(2));
            _framePump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutdown must not throw.
        }

        _cts?.Dispose();
    }
}
