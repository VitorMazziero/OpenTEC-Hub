using TecnalHub.Protocol;
using Xunit;

namespace TecnalHub.Tests;

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

        manager.SendCommand(TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 30.0));
        manager.SendCommand(TecnalCommand.Create().Set(CommandKeys.MotorSetpoint, 500));

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

        fake.Emit("[ESP32_AVISO]: Falha de leitura UART do Modulo TECNAL");
        fake.Emit("OK");

        Assert.True(await WaitForAsync(() =>
            manager.Diagnostics.DeviceLogLines == 1 && manager.Diagnostics.CommandAcks == 1));

        Assert.Equal(0, manager.Diagnostics.ParseFailures);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }
}
