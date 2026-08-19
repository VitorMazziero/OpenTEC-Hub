using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;

namespace TecnalHub.Harness;

/// <summary>
/// Determines what actually reboots the ESP32 on a USB connect, and whether the
/// 1.8 s boot settle is still needed.
/// </summary>
/// <remarks>
/// <para>
/// v.6 pulses DTR/RTS on every connect. On a textbook ESP32 auto-reset circuit that
/// sequence should do nothing - the circuit responds only to <i>differential</i>
/// states (DTR and RTS opposite), and v.6 drives both lines to the same value at
/// each end. Yet bench traces show the device clock returning to its boot value on
/// every reconnect, so something is resetting the board.
/// </para>
/// <para>
/// Two candidates: the pulse itself (via the transient between the two non-atomic
/// line changes), or simply opening the port (some USB-UART drivers assert the
/// control lines on open). This separates them by measuring the device clock across
/// reconnects with the pulse enabled and disabled.
/// </para>
/// <para>
/// It matters because we are not flashing firmware - we are attaching to a running
/// application. A reset costs 1.8 s on every connect and discards process state,
/// setpoints included, which is what makes a spurious mid-run reconnect dangerous.
/// </para>
/// </remarks>
internal sealed class ResetExperiment(string portName, ILoggerFactory loggerFactory)
{
    /// <summary>A device clock reading, or null if none could be obtained.</summary>
    private sealed record Observation(
        double? DeviceTimeSeconds, double ConnectMs, bool HandshakeOk, DateTimeOffset TakenAt);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine("ESP32-S3 reset behaviour on USB connect");
        Console.WriteLine("======================================================================");
        Console.WriteLine($"port    : {portName}");
        Console.WriteLine("method  : read the device clock (Time, seconds since boot) across");
        Console.WriteLine("          repeated connects. A clock that jumps backwards means the");
        Console.WriteLine("          board rebooted; one that keeps climbing means it did not.");
        Console.WriteLine();

        // ---- A: v.6 behaviour, pulse enabled ----------------------------
        Console.WriteLine("--- A. Pulse ENABLED (current v.6 behaviour) ---");
        var a1 = await ConnectAndReadAsync(pulse: true, bootSettle: null, cancellationToken).ConfigureAwait(false);
        Report("A1", a1);
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        var a2 = await ConnectAndReadAsync(pulse: true, bootSettle: null, cancellationToken).ConfigureAwait(false);
        Report("A2", a2);

        var resetWithPulse = ClockLostTime(a1, a2);
        ReportPair("A", a1, a2, resetWithPulse);
        Console.WriteLine();

        // ---- B: pulse suppressed ----------------------------------------
        Console.WriteLine("--- B. Pulse DISABLED ---");
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        var b1 = await ConnectAndReadAsync(pulse: false, bootSettle: null, cancellationToken).ConfigureAwait(false);
        Report("B1", b1);
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        var b2 = await ConnectAndReadAsync(pulse: false, bootSettle: null, cancellationToken).ConfigureAwait(false);
        Report("B2", b2);

        var resetWithoutPulse = ClockLostTime(b1, b2);
        ReportPair("B", b1, b2, resetWithoutPulse);
        Console.WriteLine();

        // ---- C: how much of the 1.8 s settle is actually required? ------
        // Only meaningful if the board is not rebooting; if it is, the settle is
        // covering a real bootloader window and must not be shortened.
        var settleResults = new List<(int Ms, bool Ok, double ConnectMs)>();
        if (!resetWithoutPulse && b2.HandshakeOk)
        {
            Console.WriteLine("--- C. Minimum boot settle with the pulse disabled ---");
            Console.WriteLine("    (the settle exists to wait out a reboot; without one it may be dead time)");

            foreach (var settleMs in new[] { 1800, 800, 300, 100, 0 })
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                var probe = await ConnectAndReadAsync(
                    pulse: false, bootSettle: settleMs, cancellationToken).ConfigureAwait(false);

                settleResults.Add((settleMs, probe.HandshakeOk, probe.ConnectMs));
                Console.WriteLine(FormattableString.Invariant(
                    $"    settle {settleMs,4} ms -> handshake {(probe.HandshakeOk ? "OK  " : "FAIL")}  connect {probe.ConnectMs,6:F0} ms"));
            }

            Console.WriteLine();
        }

        // ---- Conclusion -------------------------------------------------
        Console.WriteLine("======================================================================");
        Console.WriteLine("Conclusion");
        Console.WriteLine("======================================================================");

        if (!a1.HandshakeOk && !b1.HandshakeOk)
        {
            Console.WriteLine("  Could not connect at all - is the board on this port?");
            return 2;
        }

        if (resetWithPulse && !resetWithoutPulse)
        {
            Console.WriteLine("  The DTR/RTS pulse is what reboots the board.");
            Console.WriteLine("  Suppressing it on reconnect preserves device state across a");
            Console.WriteLine("  recovery, and the boot settle becomes dead time.");
            var fastest = settleResults.Where(r => r.Ok).OrderBy(r => r.Ms).FirstOrDefault();
            if (fastest.Ok)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"  Handshake still succeeds with a {fastest.Ms} ms settle ({fastest.ConnectMs:F0} ms connect)."));
            }
        }
        else if (resetWithPulse && resetWithoutPulse)
        {
            Console.WriteLine("  The board reboots even with the pulse suppressed, so OPENING THE");
            Console.WriteLine("  PORT is what resets it - the driver asserts the control lines on");
            Console.WriteLine("  open. Removing the pulse alone will not help; the fix would be at");
            Console.WriteLine("  the DCB/driver level, if it is achievable at all.");
        }
        else if (!resetWithPulse && !resetWithoutPulse)
        {
            Console.WriteLine("  The board kept running in both configurations - the pulse is not");
            Console.WriteLine("  what reboots it. Check the connect timings in section C: if a short");
            Console.WriteLine("  settle still handshakes, the 1.8 s wait is dead time regardless.");
        }
        else
        {
            Console.WriteLine("  Reset only WITHOUT the pulse, which is not a coherent result.");
            Console.WriteLine("  Re-run; something else perturbed the board.");
        }

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// True when the device clock failed to keep pace with wall-clock time between
    /// two observations, i.e. the board rebooted in between.
    /// </summary>
    /// <remarks>
    /// Comparing for a simple decrease is not enough. When <i>every</i> connect
    /// resets the board, each observation lands the same short interval after boot
    /// and the readings are identical - 2.8 s, 2.8 s, 2.8 s - so nothing ever "goes
    /// backwards" even though the board rebooted every time. What distinguishes the
    /// two cases is whether the device clock advanced by roughly the wall-clock gap.
    /// </remarks>
    private static bool ClockLostTime(Observation first, Observation second)
    {
        if (first.DeviceTimeSeconds is not { } a || second.DeviceTimeSeconds is not { } b)
        {
            return false;
        }

        var wallElapsed = (second.TakenAt - first.TakenAt).TotalSeconds;
        var clockAdvance = b - a;

        // Allow generous slack: the device only emits every ~2 s, so the sampling
        // instant drifts. A reboot loses the entire elapsed interval, which is far
        // larger than that jitter.
        return clockAdvance < wallElapsed - 2.5;
    }

    private static void ReportPair(string label, Observation first, Observation second, bool reset)
    {
        if (first.DeviceTimeSeconds is { } a && second.DeviceTimeSeconds is { } b)
        {
            var wall = (second.TakenAt - first.TakenAt).TotalSeconds;
            Console.WriteLine(FormattableString.Invariant(
                $"  {label}: wall clock advanced {wall:F1} s, device clock advanced {b - a:F1} s"));
        }

        Console.WriteLine($"  => {(reset ? "BOARD REBOOTED between connects" : "board kept running")}");
    }

    private static void Report(string label, Observation o)
    {
        var clock = o.DeviceTimeSeconds is { } t
            ? t.ToString("F1", CultureInfo.InvariantCulture) + " s"
            : "(no frame)";

        Console.WriteLine(FormattableString.Invariant(
            $"  {label}: handshake {(o.HandshakeOk ? "OK" : "FAIL")}, connect {o.ConnectMs:F0} ms, device clock {clock}"));
    }

    /// <summary>
    /// Connects once, reads until a telemetry frame arrives, and reports the device
    /// clock along with how long the connect took.
    /// </summary>
    private async Task<Observation> ConnectAndReadAsync(
        bool pulse, int? bootSettle, CancellationToken cancellationToken)
    {
        var config = new SerialTransportConfig
        {
            PortName = portName,
            PulseResetOnConnect = pulse,
        };

        if (bootSettle is { } ms)
        {
            config = config with { BootSettle = TimeSpan.FromMilliseconds(ms) };
        }

        await using var transport = new SerialTransport(
            config, loggerFactory.CreateLogger<SerialTransport>());

        var stopwatch = Stopwatch.StartNew();
        bool connected;
        try
        {
            connected = await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            connected = false;
        }

        stopwatch.Stop();

        if (!connected)
        {
            return new Observation(null, stopwatch.Elapsed.TotalMilliseconds, false, DateTimeOffset.Now);
        }

        // Read until telemetry arrives. Device logs and the bare "OK" ack share the
        // stream, so skip those rather than treating the first line as the answer.
        var parser = new TelemetryParser();
        var deadline = Environment.TickCount64 + 6000;

        while (Environment.TickCount64 < deadline)
        {
            string? line;
            try
            {
                line = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TransportFaultException)
            {
                break;
            }

            if (line is not null && parser.Parse(line) == ParseOutcome.Updated)
            {
                return new Observation(
                    parser.Readings.TimeRawSeconds, stopwatch.Elapsed.TotalMilliseconds, true, DateTimeOffset.Now);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return new Observation(null, stopwatch.Elapsed.TotalMilliseconds, true, DateTimeOffset.Now);
    }
}
