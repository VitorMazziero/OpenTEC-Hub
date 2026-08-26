using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TecnalHub.Protocol;

/// <summary>
/// Serial (USB CDC) link settings.
/// </summary>
/// <remarks>
/// The timing values are <b>load-bearing</b>, measured against real hardware in v.6.
/// See <c>docs/PROTOCOL.md</c> section 1.2 before changing any of them.
/// </remarks>
public sealed record SerialTransportConfig
{
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Parity Parity { get; init; } = Parity.None;

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Quiet period after the DTR/RTS reset pulse, before any handshake byte.
    /// </summary>
    /// <remarks>
    /// <b>Only applied when <see cref="PulseResetOnConnect"/> is true</b>, because it
    /// exists purely to wait out the reboot that pulse causes. With no pulse there is
    /// no bootloader to wait for, and measurement showed the handshake succeeding with
    /// a zero settle. Do not shorten it for the pulsed path: below ~1.8 s the
    /// bootloader is still emitting and the handshake reads boot noise.
    /// </remarks>
    public TimeSpan BootSettle { get; init; } = TimeSpan.FromMilliseconds(1800);

    public int HandshakeAttempts { get; init; } = 10;
    public TimeSpan HandshakeRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Hard ceiling on opening the port, which can hang on a wedged driver.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Pulse DTR/RTS on connect, hardware-resetting the ESP32.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defaults to false, unlike v.6.</b> Measured on hardware 2026-08-19
    /// (<c>tecnal-harness reset-test</c>): with the pulse the device clock fell from
    /// 102.1 s to 2.8 s across a reconnect while only 5.1 s of wall time passed - the
    /// board really does reboot, discarding its process state including applied
    /// setpoints. With the pulse suppressed the clock advanced 5.5 s against 5.5 s of
    /// wall time: it kept running.
    /// </para>
    /// <para>
    /// Removing it also removes the reason for the 1.8 s <see cref="BootSettle"/>,
    /// taking a connect from ~1900 ms to ~13 ms.
    /// </para>
    /// <para>
    /// We are attaching to a running application, not flashing firmware, so a reset
    /// is the wrong default. It stays available for the one case that needs it: a
    /// firmware hang, where only a hardware reset recovers the board.
    /// <c>ConnectionManager</c> escalates to a pulsed connect after repeated
    /// handshake failures.
    /// </para>
    /// </remarks>
    public bool PulseResetOnConnect { get; init; }
}

/// <summary>
/// USB CDC transport for the TECNAL ESP32-S3 controller.
/// </summary>
/// <remarks>
/// Port of <c>USBTransport</c> in v.6 <c>communication/transport.py</c>.
/// See <c>docs/PROTOCOL.md</c> sections 1.1-1.2.
/// </remarks>
public sealed class SerialTransport(
    SerialTransportConfig config,
    ILogger<SerialTransport>? logger = null) : ITransport
{
    private const string HandshakePayload = """{"comTest":1}""";
    private const string HandshakeExpected = "OK";

    /// <summary>USB descriptor fragments that suggest an ESP32-class adapter.</summary>
    private static readonly string[] EspKeywords =
        ["CP210", "CH340", "CH910", "USB Serial", "ESP32", "Silicon Labs", "wch"];

    private readonly ILogger _log = logger ?? NullLogger<SerialTransport>.Instance;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private SerialPort? _port;

    public TransportMedium Medium => TransportMedium.Usb;

    public string Endpoint => config.PortName;

    public bool IsConnected => _port?.IsOpen == true;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var port = await OpenPortAsync(cancellationToken).ConfigureAwait(false);
        if (port is null)
        {
            return false;
        }

        try
        {
            if (config.PulseResetOnConnect)
            {
                PulseResetLines(port);

                // The settle exists only to wait out the reboot the pulse just
                // caused. With no pulse there is no bootloader to wait for, and the
                // handshake retries below already cover a board that happens to be
                // booting for some other reason.
                await Task.Delay(config.BootSettle, cancellationToken).ConfigureAwait(false);
            }

            TryDiscardBuffers(port);

            if (await HandshakeAsync(port, cancellationToken).ConfigureAwait(false))
            {
                _port = port;
                _log.LogInformation("USB {Port}: handshake ok", config.PortName);
                return true;
            }

            _log.LogWarning("USB {Port}: handshake failed after {Attempts} attempts",
                config.PortName, config.HandshakeAttempts);
        }
        catch (OperationCanceledException)
        {
            SafeClose(port);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "USB {Port}: connect failed", config.PortName);
        }

        SafeClose(port);
        return false;
    }

    public async Task DisconnectAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SafeClose(_port);
            _port = null;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Drains everything buffered and returns only the <b>newest</b> complete line.
    /// </summary>
    /// <remarks>
    /// Deliberate, and inherited from v.6: the device emits faster than the UI
    /// consumes, so keeping one line per tick would fall progressively further behind
    /// over a multi-hour run. Older lines are discarded rather than queued.
    /// See <c>docs/PROTOCOL.md</c> section 1.2.
    /// </remarks>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = _port;
            if (port is null || !port.IsOpen)
            {
                return null;
            }

            try
            {
                if (port.BytesToRead == 0)
                {
                    return null;
                }

                string? newest = null;
                while (port.BytesToRead > 0)
                {
                    var line = port.ReadLine().Trim();
                    if (line.Length > 0)
                    {
                        newest = line;
                    }
                }

                return newest;
            }
            catch (TimeoutException)
            {
                // A partial line was in flight. Not a fault; next poll picks it up.
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                throw new TransportFaultException($"USB {config.PortName}: read failed.", ex);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<bool> WriteAsync(string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = _port;
            if (port is null || !port.IsOpen)
            {
                return false;
            }

            try
            {
                // USB frames are newline-terminated; Wi-Fi's are not.
                port.Write(payload + "\n");
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
            {
                throw new TransportFaultException($"USB {config.PortName}: write failed.", ex);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            // Touching BytesToRead throws once the port is physically gone, which is
            // the cheapest way to notice an unplugged cable.
            _ = port.BytesToRead;
            return Task.FromResult(port.IsOpen);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ioGate.Dispose();
    }

    // ------------------------------------------------------------------
    // Port discovery
    // ------------------------------------------------------------------

    /// <summary>
    /// Serial ports worth trying, ESP32-like adapters first.
    /// </summary>
    /// <remarks>
    /// .NET exposes only port names, not the USB descriptors pyserial reads, so the
    /// keyword ranking v.6 performs is only possible where a description is
    /// available. Ordering is therefore a hint; probing still decides.
    /// </remarks>
    public static IReadOnlyList<string> ListCandidatePorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (OperatingSystem.IsWindows())
            {
                var portDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
#pragma warning disable CA1416 // Validate platform compatibility
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT Name, Description, Manufacturer FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                    foreach (var obj in searcher.Get())
                    {
                        if (obj["Name"] is string name)
                        {
                            var start = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                            if (start >= 0)
                            {
                                var end = name.IndexOf(')', start);
                                if (end > start)
                                {
                                    var com = name.Substring(start + 1, end - start - 1);
                                    portDescriptions[com] = $"{obj["Description"]} {obj["Manufacturer"]}";
                                }
                            }
                        }
                    }
#pragma warning restore CA1416
                }
                catch (Exception) { /* WMI might be disabled or unavailable */ }

                var ranked = new List<string>();
                var unranked = new List<string>();

                foreach (var port in ports)
                {
                    if (portDescriptions.TryGetValue(port, out var desc) &&
                        EspKeywords.Any(k => desc.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        ranked.Add(port);
                    }
                    else
                    {
                        unranked.Add(port);
                    }
                }

                ranked.Sort(StringComparer.OrdinalIgnoreCase);
                unranked.Sort(StringComparer.OrdinalIgnoreCase);
                return [.. ranked, .. unranked];
            }

            return [.. ports.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Keyword list used to rank adapters; exposed for diagnostics.</summary>
    public static IReadOnlyList<string> EspAdapterKeywords => EspKeywords;

    /// <summary>
    /// Probes ports <b>in parallel</b> and returns the first that completes the
    /// handshake.
    /// </summary>
    /// <remarks>
    /// v.6 probed serially, paying a 1.8 s settle plus up to ten 0.75 s handshake
    /// timeouts per dead port - over 10 s each, with no way to cancel. On a machine
    /// with a few virtual COM ports that is the bulk of the startup delay users
    /// complain about. Probing concurrently makes the wall-clock cost that of the
    /// slowest single port. See <c>docs/MIGRATION.md</c> section 1.
    /// </remarks>
    /// <param name="template">Link settings; <c>PortName</c> is replaced per candidate.</param>
    /// <param name="preferredPort">Tried first and alone; on success nothing else is opened.</param>
    public static async Task<string?> ProbePortsAsync(
        SerialTransportConfig template,
        string? preferredPort = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        var candidates = ListCandidatePorts();

        // The overwhelmingly common case: the device is where it was last time.
        if (!string.IsNullOrWhiteSpace(preferredPort) &&
            candidates.Contains(preferredPort, StringComparer.OrdinalIgnoreCase) &&
            await TryPortAsync(template with { PortName = preferredPort }, loggerFactory, cancellationToken)
                .ConfigureAwait(false))
        {
            return preferredPort;
        }

        var remaining = candidates
            .Where(p => !string.Equals(p, preferredPort, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (remaining.Length == 0)
        {
            return null;
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var attempts = remaining
            .Select(async port =>
            {
                var ok = await TryPortAsync(
                    template with { PortName = port }, loggerFactory, raceCts.Token).ConfigureAwait(false);
                return ok ? port : null;
            })
            .ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);

            string? winner = null;
            try
            {
                winner = await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Lost the race, or the caller cancelled.
            }

            if (winner is not null)
            {
                // Stop the losers; each is holding a port open.
                await raceCts.CancelAsync().ConfigureAwait(false);
                return winner;
            }
        }

        return null;
    }

    private static async Task<bool> TryPortAsync(
        SerialTransportConfig config,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        await using var transport = new SerialTransport(
            config, loggerFactory?.CreateLogger<SerialTransport>());
        try
        {
            return await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private async Task<SerialPort?> OpenPortAsync(CancellationToken cancellationToken)
    {
        // Opening a serial port is a blocking call that can hang indefinitely on a
        // wedged driver, so it gets its own hard deadline.
        var open = Task.Run(() =>
        {
            var port = new SerialPort(config.PortName, config.BaudRate, config.Parity,
                                      config.DataBits, config.StopBits)
            {
                ReadTimeout = (int)config.ReadTimeout.TotalMilliseconds,
                WriteTimeout = (int)config.WriteTimeout.TotalMilliseconds,
                NewLine = "\n",
                Encoding = new UTF8Encoding(false),
            };

            port.Open();
            return port;
        }, cancellationToken);

        var completed = await Task.WhenAny(open, Task.Delay(config.OpenTimeout, cancellationToken))
                                  .ConfigureAwait(false);

        if (completed != open)
        {
            _log.LogWarning("USB {Port}: open timed out after {Timeout}",
                config.PortName, config.OpenTimeout);

            // The open task may still complete later; make sure it does not leak a
            // handle to a port nobody owns.
            _ = open.ContinueWith(
                static t => SafeClose(t.Result),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);

            return null;
        }

        try
        {
            return await open.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "USB {Port}: open failed", config.PortName);
            return null;
        }
    }

    /// <summary>
    /// Brief DTR/RTS low-to-high pulse, which drives the ESP32 reset pin.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed on purpose: not every USB-UART adapter exposes these
    /// lines, and those that do not still work - they just do not get a clean reset.
    /// </remarks>
    private void PulseResetLines(SerialPort port)
    {
        try
        {
            port.DtrEnable = false;
            port.RtsEnable = false;
            Thread.Sleep(50);
            port.DtrEnable = true;
            port.RtsEnable = true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "USB {Port}: adapter does not support DTR/RTS", config.PortName);
        }
    }

    private async Task<bool> HandshakeAsync(SerialPort port, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= config.HandshakeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                port.Write(HandshakePayload + "\n");
            }
            catch (Exception ex)
            {
                _log.LogTrace(ex, "USB {Port}: handshake write failed (attempt {Attempt})",
                    config.PortName, attempt);
            }

            try
            {
                var reply = port.ReadLine().Trim();
                if (string.Equals(reply, HandshakeExpected, StringComparison.Ordinal))
                {
                    return true;
                }

                // Boot banners and log lines are expected here; keep trying.
                _log.LogTrace("USB {Port}: unexpected handshake reply {Reply!r} (attempt {Attempt})",
                    config.PortName, reply, attempt);
            }
            catch (TimeoutException)
            {
                // Silence on this attempt.
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _log.LogDebug(ex, "USB {Port}: link fault during handshake", config.PortName);
                return false;
            }

            await Task.Delay(config.HandshakeRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryDiscardBuffers(SerialPort port)
    {
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (Exception)
        {
            // Nothing actionable; the handshake retries cover a dirty buffer.
        }
    }

    private static void SafeClose(SerialPort? port)
    {
        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (Exception)
        {
            // Disconnect must never throw.
        }
        finally
        {
            port.Dispose();
        }
    }
}
