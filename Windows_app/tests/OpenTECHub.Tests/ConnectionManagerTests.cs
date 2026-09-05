using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// State-machine behaviour, driven against <see cref="FakeTransport"/>.
/// </summary>
/// <remarks>
/// Timeouts are deliberately small so the suite stays fast; the ratios between them
/// mirror the production defaults (probe at half the silence timeout).
/// </remarks>
public class ConnectionManagerTests
{
    private static ConnectionOptions FastOptions(bool backupEnabled = false) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(20),
        LivenessProbeAfterSilence = TimeSpan.FromMilliseconds(150),
        TelemetrySilenceTimeout = TimeSpan.FromMilliseconds(400),
        BackupEnabled = backupEnabled,
        BackupDelay = TimeSpan.FromMilliseconds(50),

        // The retry loop re-probes the serial ports after this many failures on the same one,
        // and that probe is a real Windows port enumeration - hundreds of ms when the machine is
        // idle, far worse when the whole suite is running in parallel. With the default of 3 and a
        // 50 ms backoff, a test that deliberately fails connects spends its time inside the OS
        // probe instead of inside ConnectAsync, which is how
        // Connect_aborts_the_connect_attempt_in_flight came to fail ~30% of full-suite runs while
        // passing in isolation. No test covers the re-probe path, so it is kept out of the way here.
        FailuresBeforeReprobe = int.MaxValue,

        // The production ceiling is 5 s, sized for a USB round trip measured in
        // milliseconds. Under the full suite the gap between a test dispatching a command
        // and its fake ack being pumped through the request loop is wall-clock, not link
        // latency, and it exceeded 5 s often enough to fail Usb_measures_RTT_on_CommandAck
        // in a full run while passing in isolation. These tests are about the correlation
        // itself, so the ceiling is lifted out of the way here; the one test that is about
        // the ceiling sets its own.
        RoundTripCorrelationWindow = TimeSpan.FromMinutes(1),
    };

    /// <summary>Polls until <paramref name="condition"/> holds, or gives up.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task Connects_and_reports_connected()
    {
        var fake = new FakeTransport();
        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));
        Assert.Equal(1, fake.ConnectCalls);
    }

    [Fact]
    public async Task Telemetry_frames_are_published()
    {
        var fake = new FakeTransport();
        var received = new List<SensorSnapshot>();

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.TelemetryReceived += s => { lock (received) { received.Add(s); } };
        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.EmitTelemetry(10.5);
        fake.EmitTelemetry(12.5);

        Assert.True(await WaitForAsync(() =>
        {
            lock (received) { return received.Count >= 2; }
        }));

        lock (received)
        {
            Assert.Equal(10.5, received[0].TimeRawSeconds);
            Assert.Equal(12.5, received[1].TimeRawSeconds);
        }
    }

    /// <summary>
    /// Zeroing the session clock rebases reported minutes from the next frame on, without
    /// resetting the device clock or rewriting frames already published.
    /// </summary>
    [Fact]
    public async Task Zeroing_the_session_clock_rebases_reported_time_only()
    {
        var fake = new FakeTransport();
        var snapshots = new List<SensorSnapshot>();
        double? zeroedOffset = null;

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.TelemetryReceived += s => { lock (snapshots) { snapshots.Add(s); } };
        manager.SessionTimeZeroed += o => zeroedOffset = o;
        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.EmitTelemetry(600); // 10 minutes since boot
        Assert.True(await WaitForAsync(() =>
        {
            lock (snapshots) { return snapshots.Count >= 1; }
        }));
        lock (snapshots) { Assert.Equal(10.0, snapshots[^1].TimeMinutes); }

        manager.ZeroSessionTime();
        Assert.True(await WaitForAsync(() => zeroedOffset is not null));
        Assert.Equal(10.0, zeroedOffset);

        fake.EmitTelemetry(660); // 11 min since boot, but one minute past the zero
        Assert.True(await WaitForAsync(() =>
        {
            lock (snapshots) { return snapshots.Count >= 2; }
        }));

        lock (snapshots)
        {
            Assert.Equal(1.0, snapshots[^1].TimeMinutes);      // rebased
            Assert.Equal(660, snapshots[^1].TimeRawSeconds);   // device clock untouched
        }
    }

    /// <summary>
    /// The regression this whole change exists for.
    /// </summary>
    /// <remarks>
    /// Silence detection was originally gated on <c>Medium == Usb</c>, on the
    /// reasoning that Wi-Fi cannot distinguish silence from a 304. The consequence
    /// was that a Wi-Fi link whose telemetry stalled while the web server stayed up
    /// would never time out - the app would show frozen readings behind a healthy
    /// "Connected" indicator forever.
    /// </remarks>
    [Fact]
    public async Task WiFi_telemetry_silence_drops_the_link()
    {
        var fake = new FakeTransport(TransportMedium.WiFi)
        {
            IsAlive = true, // the web server keeps answering /ping - this is the trap
        };

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.ConnectWiFi(new HttpTransportConfig { IpAddress = "192.0.2.1" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.EmitTelemetry(10.0);
        Assert.True(await WaitForAsync(() => manager.Diagnostics.FramesReceived == 1));

        // Telemetry stalls. The transport stays "healthy" throughout.
        Assert.True(
            await WaitForAsync(() => manager.State != ConnectionState.Connected),
            "Wi-Fi link stayed Connected despite telemetry going silent - stale data would be shown as live");
    }

    [Fact]
    public async Task Usb_telemetry_silence_drops_the_link()
    {
        var fake = new FakeTransport(TransportMedium.Usb);

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.EmitTelemetry(10.0);
        Assert.True(await WaitForAsync(() => manager.Diagnostics.FramesReceived == 1));

        Assert.True(await WaitForAsync(() => manager.State != ConnectionState.Connected));
    }

    /// <summary>
    /// A failing probe should drop the link before the full silence timeout expires.
    /// </summary>
    [Fact]
    public async Task Failed_liveness_probe_drops_the_link_early()
    {
        var fake = new FakeTransport();

        await using var manager = new ConnectionManager(
            new ConnectionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                LivenessProbeAfterSilence = TimeSpan.FromMilliseconds(100),
                // Deliberately long: if the link drops, it was the probe that did it.
                TelemetrySilenceTimeout = TimeSpan.FromSeconds(30),
                BackupEnabled = false,
            },
            transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.EmitTelemetry(10.0);
        Assert.True(await WaitForAsync(() => manager.Diagnostics.FramesReceived == 1));

        fake.IsAlive = false;

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));
        Assert.True(fake.LivenessProbeCalls > 0);
    }

    /// <summary>
    /// A healthy link must not generate probe traffic. The device shares one UART
    /// with the sensor module; needless chatter is not free.
    /// </summary>
    [Fact]
    public async Task Healthy_telemetry_sends_no_liveness_probes()
    {
        var fake = new FakeTransport();

        await using var manager = new ConnectionManager(
            new ConnectionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                LivenessProbeAfterSilence = TimeSpan.FromMilliseconds(200),
                TelemetrySilenceTimeout = TimeSpan.FromSeconds(30),
                BackupEnabled = false,
            },
            transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        // Feed frames faster than the probe threshold for a while.
        for (var i = 0; i < 12; i++)
        {
            fake.EmitTelemetry(i);
            await Task.Delay(50);
        }

        Assert.Equal(0, fake.LivenessProbeCalls);
        Assert.Equal(0, manager.Diagnostics.LivenessProbes);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    [Fact]
    public async Task Commands_are_written_to_the_transport()
    {
        var fake = new FakeTransport();
        var transmitted = new List<string>();

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);
        manager.CommandSent += json =>
        {
            lock (transmitted)
            {
                transmitted.Add(json);
            }
        };

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(CommandBuilders.MotorSetpoint(790));

        Assert.True(await WaitForAsync(() =>
        {
            lock (fake.Writes) { return fake.Writes.Count > 0; }
        }));

        lock (fake.Writes)
        {
            Assert.Equal("""{"motorSetpoint":790}""", fake.Writes[0]);
        }

        Assert.True(await WaitForAsync(() =>
        {
            lock (transmitted) { return transmitted.Count == 1; }
        }));
        lock (transmitted)
        {
            Assert.Equal("""{"motorSetpoint":790}""", Assert.Single(transmitted));
        }
    }

    [Fact]
    public async Task Rejected_command_is_not_reported_as_transmitted()
    {
        var fake = new FakeTransport { WriteSucceeds = false };
        var transmitted = new List<string>();

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);
        manager.CommandSent += json =>
        {
            lock (transmitted)
            {
                transmitted.Add(json);
            }
        };

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(CommandBuilders.MotorSetpoint(790));

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));
        lock (transmitted)
        {
            Assert.Empty(transmitted);
        }
    }

    /// <summary>
    /// Several commands buffered between flushes must leave as one frame - the point
    /// of buffering at all, given the shared UART.
    /// </summary>
    [Fact]
    public async Task Buffered_commands_coalesce_into_one_write()
    {
        var fake = new FakeTransport();

        await using var manager = new ConnectionManager(
            new ConnectionOptions
            {
                // Slow poll so both commands land in the same flush.
                PollInterval = TimeSpan.FromMilliseconds(300),
                TelemetrySilenceTimeout = TimeSpan.FromSeconds(30),
                LivenessProbeAfterSilence = TimeSpan.FromSeconds(30),
                BackupEnabled = false,
            },
            transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        lock (fake.Writes)
        {
            fake.Writes.Clear();
        }

        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 30.0));
        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.MotorSetpoint, 500));

        Assert.True(await WaitForAsync(() =>
        {
            lock (fake.Writes) { return fake.Writes.Count > 0; }
        }));

        await Task.Delay(200);

        lock (fake.Writes)
        {
            var combined = fake.Writes.FirstOrDefault(w => w.Contains("motorSetpoint", StringComparison.Ordinal));
            Assert.NotNull(combined);
            Assert.Contains("tempSetpoint", combined, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A rejected write must not lose the command - it is a setpoint the operator
    /// asked for.
    /// </summary>
    [Fact]
    public async Task Rejected_write_requeues_the_command()
    {
        var fake = new FakeTransport { WriteSucceeds = false };

        await using var manager = new ConnectionManager(
            new ConnectionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                TelemetrySilenceTimeout = TimeSpan.FromSeconds(30),
                LivenessProbeAfterSilence = TimeSpan.FromSeconds(30),
                BackupEnabled = true,
                BackupDelay = TimeSpan.FromMilliseconds(30),
            },
            transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(CommandBuilders.MotorSetpoint(790));

        // The write is rejected, so the link is torn down and retried. Once writes
        // start succeeding again the setpoint must still go out.
        Assert.True(await WaitForAsync(() =>
        {
            lock (fake.Writes) { return fake.Writes.Count > 0; }
        }));

        fake.WriteSucceeds = true;

        Assert.True(
            await WaitForAsync(() =>
            {
                lock (fake.Writes)
                {
                    return fake.Writes.Count(w => w.Contains("motorSetpoint", StringComparison.Ordinal)) >= 2;
                }
            }),
            "the rejected setpoint was dropped instead of being retried");
    }

    [Fact]
    public async Task Read_fault_drops_the_link()
    {
        var fake = new FakeTransport
        {
            NextReadThrows = new TransportFaultException("simulated cable pull"),
        };

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));
        Assert.Contains("simulated cable pull", manager.Diagnostics.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_handshake_without_backup_goes_to_faulted()
    {
        var fake = new FakeTransport { ConnectSucceeds = false };

        await using var manager = new ConnectionManager(
            FastOptions(), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));
    }

    [Fact]
    public async Task Reconnect_cycle_recovers_when_the_device_returns()
    {
        var fake = new FakeTransport { ConnectSucceeds = false };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: true), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Reconnecting));

        fake.ConnectSucceeds = true;

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));
    }

    [Fact]
    public async Task Disconnect_stops_the_reconnect_cycle()
    {
        var fake = new FakeTransport { ConnectSucceeds = false };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: true), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Reconnecting));

        manager.Disconnect();

        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Disconnected));
    }

    /// <summary>
    /// Parar has to bite while an attempt is in flight, not after it.
    /// </summary>
    /// <remarks>
    /// The retry loop reads its request channel between attempts. With a real port that is
    /// an open plus a handshake and then the backup delay, so an operator watched the app
    /// go on reconnecting for seconds after clicking Parar — and the link-lost alarm stayed
    /// up with it.
    /// </remarks>
    [Fact]
    public async Task Disconnect_aborts_the_connect_attempt_in_flight()
    {
        var fake = new FakeTransport { ConnectSucceeds = false };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: true), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Reconnecting));

        // From here every attempt hangs far longer than this test is willing to wait.
        fake.ConnectDuration = TimeSpan.FromSeconds(30);
        Assert.True(
            await Task.WhenAny(fake.StalledConnectEntered, Task.Delay(3000)) == fake.StalledConnectEntered,
            "the retry loop never entered a stalled attempt");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        manager.Disconnect();

        Assert.True(
            await WaitForAsync(() => manager.State == ConnectionState.Disconnected, timeoutMs: 2000),
            "Disconnect waited out the attempt in flight");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"took {elapsed.ElapsedMilliseconds} ms");
    }

    /// <summary>Connecting is the same kind of operator intervention as stopping.</summary>
    [Fact]
    public async Task Connect_aborts_the_connect_attempt_in_flight()
    {
        var fake = new FakeTransport { ConnectSucceeds = false };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: true), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Reconnecting));

        fake.ConnectDuration = TimeSpan.FromSeconds(30);
        Assert.True(
            await Task.WhenAny(fake.StalledConnectEntered, Task.Delay(3000)) == fake.StalledConnectEntered,
            "the retry loop never entered a stalled attempt");

        fake.ConnectDuration = TimeSpan.Zero;
        fake.ConnectSucceeds = true;
        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });

        Assert.True(
            await WaitForAsync(() => manager.State == ConnectionState.Connected, timeoutMs: 2000),
            "Connect waited out the attempt in flight");
    }

    /// <summary>
    /// Device log lines and command acks are traffic, not telemetry. They must not be
    /// counted as parse failures - but nor should they keep a stalled link alive.
    /// </summary>
    [Fact]
    public async Task Device_logs_and_acks_are_not_parse_failures()
    {
        var fake = new FakeTransport();
        var logs = new List<string>();

        await using var manager = new ConnectionManager(
            new ConnectionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                TelemetrySilenceTimeout = TimeSpan.FromSeconds(30),
                LivenessProbeAfterSilence = TimeSpan.FromSeconds(30),
                BackupEnabled = false,
            },
            transportFactory: _ => fake);

        manager.DeviceLogReceived += l => { lock (logs) { logs.Add(l); } };
        manager.ConnectUsb(new SerialTransportConfig { PortName = "FAKE" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        fake.Emit("[ESP32_AVISO]: Falha de leitura UART do Modulo OpenTEC");
        fake.Emit("OK");

        Assert.True(await WaitForAsync(() =>
            manager.Diagnostics.DeviceLogLines == 1 && manager.Diagnostics.CommandAcks == 1));

        Assert.Equal(0, manager.Diagnostics.ParseFailures);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    [Fact]
    public async Task Busy_port_reports_friendly_error_message()
    {
        var fake = new FakeTransport
        {
            ConnectThrows = new PortBusyException("COM3", new UnauthorizedAccessException("Access denied")),
        };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));

        Assert.Contains("COM3", manager.Diagnostics.LastError);
        Assert.Contains("ocupada por outra aplicação", manager.Diagnostics.LastError);
    }

    [Fact]
    public async Task Wi_Fi_records_synchronous_RTT_on_write()
    {
        var fake = new FakeTransport(TransportMedium.WiFi);

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectWiFi(new HttpTransportConfig { IpAddress = "192.168.1.100" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37));

        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandsSent == 1));
        Assert.NotNull(manager.Diagnostics.LastRoundTripMs);
        Assert.True(manager.Diagnostics.LastRoundTripMs >= 0);
    }

    [Fact]
    public async Task Usb_measures_RTT_on_CommandAck()
    {
        var fake = new FakeTransport(TransportMedium.Usb);

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        // Before any command or ack, LastRoundTripMs should be null.
        Assert.Null(manager.Diagnostics.LastRoundTripMs);

        // Send a non-flow command (e.g. TempSetpoint)
        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37));
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandsSent == 1));

        // Write buffer does not prematurely populate LastRoundTripMs on USB
        Assert.Null(manager.Diagnostics.LastRoundTripMs);

        // Simulate firmware responding with OK
        fake.Emit("OK");
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandAcks == 1));

        // Now LastRoundTripMs has a genuine measured RTT
        Assert.NotNull(manager.Diagnostics.LastRoundTripMs);
        Assert.True(manager.Diagnostics.LastRoundTripMs >= 0);
    }

    [Fact]
    public async Task Usb_measures_RTT_on_FlowCommandAck()
    {
        var fake = new FakeTransport(TransportMedium.Usb);

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        // Baseline telemetry frame
        fake.Emit("""{"Time":1.0,"FlowCommandId":1,"FlowCommandAck":1}""");
        Assert.True(await WaitForAsync(() => manager.Diagnostics.FramesReceived == 1));
        Assert.Null(manager.Diagnostics.LastRoundTripMs);

        // Send flow setpoint command
        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.FlowSetpoint, 2.5));
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandsSent == 1));

        // Before firmware acks, RTT is still null on USB
        Assert.Null(manager.Diagnostics.LastRoundTripMs);

        // Firmware reports frame with FlowCommandAck echoing the new command (id=2)
        fake.Emit("""{"Time":2.0,"FlowCommandId":2,"FlowCommandAck":2}""");
        Assert.True(await WaitForAsync(() => manager.Diagnostics.FramesReceived == 2));

        Assert.NotNull(manager.Diagnostics.LastRoundTripMs);
        Assert.True(manager.Diagnostics.LastRoundTripMs >= 0);
    }

    [Fact]
    public async Task Busy_port_message_does_not_survive_into_the_next_attempt()
    {
        // The port is held by another application, then released. The second attempt fails
        // on the handshake instead, and must say so - announcing the stale "busy" sends the
        // operator hunting for a process that already let go of the port.
        var fake = new FakeTransport
        {
            ConnectThrows = new PortBusyException("COM3", new UnauthorizedAccessException("Access denied")),
        };

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Faulted));
        Assert.Contains("ocupada por outra aplicação", manager.Diagnostics.LastError);

        fake.ConnectThrows = null;
        fake.ConnectSucceeds = false;

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => fake.ConnectCalls == 2));
        Assert.True(await WaitForAsync(
            () => manager.Diagnostics.LastError.Contains("handshake", StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain("ocupada por outra aplicação", manager.Diagnostics.LastError);
    }

    [Fact]
    public async Task Wi_Fi_round_trip_is_not_overwritten_by_a_stray_ack_line()
    {
        // On Wi-Fi the POST is the round trip. Nothing is pending afterwards, so an "OK"
        // that happens to arrive must not be correlated against the last send.
        var fake = new FakeTransport(TransportMedium.WiFi);

        await using var manager = new ConnectionManager(
            FastOptions(backupEnabled: false), transportFactory: _ => fake);

        manager.ConnectWiFi(new HttpTransportConfig { IpAddress = "192.168.1.100" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37));
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandsSent == 1));

        var postRoundTrip = manager.Diagnostics.LastRoundTripMs;
        Assert.NotNull(postRoundTrip);

        fake.Emit("OK");
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandAcks == 1));

        Assert.Equal(postRoundTrip, manager.Diagnostics.LastRoundTripMs);
    }

    [Fact]
    public async Task Late_ack_is_not_reported_as_link_latency()
    {
        // A dropped ack leaves the send timestamp armed. Whatever answers long afterwards is
        // not this command's round trip, and inventing one is worse than reporting nothing.
        var options = FastOptions(backupEnabled: false) with
        {
            RoundTripCorrelationWindow = TimeSpan.FromMilliseconds(1),
        };

        var fake = new FakeTransport(TransportMedium.Usb);
        await using var manager = new ConnectionManager(options, transportFactory: _ => fake);

        manager.ConnectUsb(new SerialTransportConfig { PortName = "COM3" });
        Assert.True(await WaitForAsync(() => manager.State == ConnectionState.Connected));

        manager.SendCommand(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37));
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandsSent == 1));

        await Task.Delay(50);
        fake.Emit("OK");
        Assert.True(await WaitForAsync(() => manager.Diagnostics.CommandAcks == 1));

        Assert.Null(manager.Diagnostics.LastRoundTripMs);
    }
}
