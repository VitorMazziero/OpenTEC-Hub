using System.Globalization;
using System.Net;
using System.Text;

namespace OpenTECHub.Simulator;

/// <summary>
/// Serves the ESP32's Wi-Fi surface over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Only three endpoints exist on the real device - <c>/ping</c>, <c>/readData</c> and
/// <c>/command</c>. Everything else answers 404 <c>Not found</c>, confirmed by the
/// hardware validation run.
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

            default:
                Respond(context, 404, "Not found");
                break;
        }
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
