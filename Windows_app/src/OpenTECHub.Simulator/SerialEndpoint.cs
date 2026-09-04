using System.IO.Ports;
using System.Text;

namespace OpenTECHub.Simulator;

/// <summary>
/// Serves the ESP32's USB CDC surface over a serial port.
/// </summary>
/// <remarks>
/// <para>
/// Intended for one half of a virtual COM pair (com0com); the app connects to the
/// other half. See <c>docs/SIMULATOR.md</c> section 6.
/// </para>
/// <para>
/// Unlike HTTP, everything shares one stream here: telemetry frames, command
/// acknowledgements and device log lines all arrive interleaved on the same port,
/// newline-terminated. That interleaving is the point - a client that assumes every
/// line is JSON breaks on it, and v.6 does.
/// </para>
/// </remarks>
public sealed class SerialEndpoint(DeviceModel model, string portName, Action<string> log) : IDisposable
{
    private readonly SerialPort _port = new(portName, 115200, Parity.None, 8, StopBits.One)
    {
        NewLine = "\n",
        Encoding = new UTF8Encoding(false),
        ReadTimeout = 250,
        WriteTimeout = 1000,
    };

    private readonly Lock _writeGate = new();

    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _emitLoop;

    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _port.Open();
        log($"Serial listening on {portName} at 115200 8N1");
        log("Connect the app to the OTHER half of the virtual pair.");

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        _emitLoop = Task.Run(() => EmitLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string line;
            try
            {
                line = _port.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"Serial read error: {ex.Message}");
                await Task.Delay(200, token).ConfigureAwait(false);
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (model.Scenario == Scenario.Dropout)
            {
                continue; // deaf, as an unplugged board would be
            }

            if (!WireCodec.ApplyCommand(model, line, out var wasHandshake))
            {
                log($"  <- unparseable: {line}");
                continue;
            }

            log(wasHandshake ? "  <- handshake" : $"  <- {line}");

            // The ack goes back on the same stream the telemetry uses.
            Write("OK");
        }
    }

    private async Task EmitLoopAsync(CancellationToken token)
    {
        var nextLogLine = DateTimeOffset.UtcNow.AddSeconds(17);

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

            if (model.Scenario is Scenario.Dropout or Scenario.Stall)
            {
                continue; // link up, telemetry silent
            }

            // Occasional device log lines, interleaved exactly as the real board does.
            if (DateTimeOffset.UtcNow >= nextLogLine)
            {
                nextLogLine = DateTimeOffset.UtcNow.AddSeconds(17);
                Write(model.SensorModuleOnline
                    ? "[ESP32_INFO]: sincronizando configuracoes do Modulo OpenTEC"
                    : "[ESP32_AVISO]: Falha de leitura UART do Módulo OpenTEC após comando 'b'");
            }

            Write(model.Scenario == Scenario.Garbage
                ? "{malformed,,,"
                : WireCodec.BuildTelemetry(model));
        }
    }

    private void Write(string text)
    {
        lock (_writeGate)
        {
            try
            {
                _port.Write(text + "\n");
            }
            catch (Exception ex)
            {
                log($"Serial write error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            Task.WaitAll([_readLoop ?? Task.CompletedTask, _emitLoop ?? Task.CompletedTask],
                         TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutdown must not throw.
        }

        try
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }

            _port.Dispose();
        }
        catch (Exception)
        {
            // Ditto.
        }

        _cts?.Dispose();
    }
}
