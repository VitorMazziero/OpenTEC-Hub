using OpenTECHub.Protocol;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The servo card: what it shows, what it sends, and what it refuses to say.
/// </summary>
/// <remarks>
/// The four combinations of presence and routing are the spine of these tests. Only one is
/// a failure, and the card that collapses the others into it reports a fault on a module
/// that is working exactly as configured.
/// </remarks>
public class ServoDriveViewModelTests
{
    private static (ServoDriveViewModel Vm, RecordingDeviceService Device) Build()
    {
        var device = new RecordingDeviceService();
        return (new ServoDriveViewModel(device), device);
    }

    private static SensorSnapshot Operating(
        double rpm = 92.7,
        int state = 2,
        int alarm = 0,
        long ok = 255,
        long err = 1,
        double energyWh = 0.020717) => new()
        {
            HasServoTelemetry = true,
            HasServoSample = true,
            ServoOnline = true,
            ServoCommEnabled = true,
            ServoCommandPending = false,
            ServoCommandQueueDepth = 0,
            MotorControlViaModbus = true,
            ServoMotorRouteAck = 1,
            ServoRpm = rpm,
            ServoTorquePct = 1.4,
            ServoTorqueNm = 0.0178,
            ServoLoadPct = 1,
            ServoPowerW = 0.17,
            ServoEnergyWh = energyWh,
            ServoState = state,
            ServoAlarm = alarm,
            ServoCommOk = ok,
            ServoCommErr = err,
        };

    // ── Readings ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_operating_frame_fills_every_readout()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating());

        Assert.Equal("92,7", vm.RpmText);
        Assert.Equal("1,4", vm.TorquePercentText);
        Assert.Equal("0,0178", vm.TorqueNewtonMetreText);
        Assert.Equal("1", vm.LoadPercentText);
        Assert.Equal("0,17", vm.PowerWattText);
        Assert.Equal("0,0207", vm.EnergyWattHourText);
        Assert.Equal("Energizado", vm.StateText);
        Assert.Equal("Nenhum", vm.AlarmText);
        Assert.False(vm.IsInAlarm);
    }

    /// <summary>
    /// Zero is a reading, and must not be confused with the dash that means no data.
    /// </summary>
    [Fact]
    public void A_stopped_motor_shows_zeros_and_not_dashes()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(rpm: 0.0, state: 1) with { ServoPowerW = 0.0 });

        Assert.Equal("0,0", vm.RpmText);
        Assert.Equal("0,00", vm.PowerWattText);
        Assert.Equal("Pronto", vm.StateText);
    }

    [Theory]
    [InlineData(0, "Desligado")]
    [InlineData(1, "Pronto")]
    [InlineData(2, "Energizado")]
    [InlineData(3, "Alarme")]
    public void The_drive_state_reads_in_words(int state, string expected)
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(state: state));

        Assert.Equal(expected, vm.StateText);
    }

    /// <summary>
    /// The alarm code keeps the drive panel's encoding, which is hexadecimal.
    /// </summary>
    /// <remarks>
    /// <c>0x0011</c> is shown as <c>AL011</c> on the drive's own display. Rendering the code
    /// as decimal gives 17, which matches nothing in the manual and sends whoever looks it
    /// up to the wrong page. Verified on the bench against a real AL011 - encoder
    /// disconnected - on 2026-09-02.
    /// </remarks>
    [Fact]
    public void The_alarm_code_reads_the_way_the_drive_panel_shows_it()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(state: 3, alarm: 0x0011));

        Assert.Equal("AL011", vm.AlarmText);
        Assert.True(vm.IsInAlarm);
    }

    [Fact]
    public void A_nonzero_alarm_code_raises_the_flag_even_without_the_alarm_state()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(state: 2, alarm: 0x0011));

        Assert.True(vm.IsInAlarm);
    }

    // ── Presence and routing: the four combinations ──────────────────────────

    [Fact]
    public void Routing_off_with_the_node_present_is_not_a_failure()
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());

        device.PushTelemetry(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = true,
            ServoCommEnabled = false,
            ServoCommandQueueDepth = 0,
        });

        Assert.True(vm.Status.IsOnline);
        Assert.False(vm.Status.IsOffline);
        Assert.Equal("—", vm.RpmText);
        Assert.Equal("—", vm.PowerWattText);
    }

    [Fact]
    public void An_absent_node_with_routing_on_is_the_failure()
    {
        var (vm, device) = Build();

        device.PushTelemetry(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = false,
            ServoCommEnabled = true,
            ServoCommandQueueDepth = 0,
        });

        Assert.True(vm.Status.IsOffline);
    }

    /// <summary>
    /// A module configured without a servo must not read as a failed device.
    /// </summary>
    /// <remarks>
    /// This is the bench module's permanent state after <c>{"servoComm":0}</c>, persisted in
    /// the Hub's NVS and confirmed across a reboot on 2026-09-02. Reported as a failure it
    /// would raise an event that can never be resolved.
    /// </remarks>
    [Fact]
    public void A_module_without_a_servo_is_offline_but_not_mismatched()
    {
        var (vm, device) = Build();
        vm.IsRoutingEnabled = false;

        device.PushTelemetry(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = false,
            ServoCommEnabled = false,
            ServoCommandQueueDepth = 0,
        });

        Assert.False(vm.Status.HasCommMismatch);
    }

    /// <summary>
    /// An older Hub has claimed nothing, which is not the same as reporting a failure.
    /// </summary>
    [Fact]
    public void An_older_hub_leaves_the_card_awaiting_telemetry()
    {
        var (vm, device) = Build();

        device.PushTelemetry(new SensorSnapshot());

        Assert.False(vm.Status.HasTelemetry);
        Assert.False(vm.Status.IsOffline);
        Assert.Equal("—", vm.RpmText);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Building the view model puts nothing on the wire.
    /// </summary>
    /// <remarks>
    /// The switch is seeded to the Hub's default of routing-on, and seeding must not
    /// dispatch. Otherwise merely constructing the shell would send a frame before the
    /// operator touched anything - which is what happened, and what the whole
    /// <c>ControlViewModel</c> suite caught: every bulk-apply test suddenly saw an extra
    /// <c>servoComm</c> frame at the head of its collection.
    /// </remarks>
    [Fact]
    public void Construction_sends_nothing()
    {
        var (vm, device) = Build();

        Assert.Empty(device.Sent);
        Assert.True(vm.IsRoutingEnabled);
        Assert.True(vm.Status.IsCommRequested);
        Assert.True(vm.IsMotorControlViaModbus);
        Assert.False(vm.IsMotorControlRouteAvailable);
    }

    [Fact]
    public void Route_toggle_in_agitation_selects_uart_cn1_and_requires_a_new_setpoint()
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        device.Sent.Clear();

        vm.IsMotorControlViaModbus = false;

        Assert.Equal("""{"motorControlMode":0}""", Assert.Single(device.Sent));
        Assert.Contains("motor foi desabilitado", vm.StatusMessage, StringComparison.Ordinal);

        device.PushTelemetry(Operating() with
        {
            MotorControlViaModbus = false,
            ServoMotorRouteAck = 0,
            ServoCommandPending = false,
        });

        Assert.Equal("Ativa: módulo original via UART/CN1", vm.MotorControlRouteText);
    }

    [Fact]
    public void Route_toggle_is_not_available_against_an_old_hub()
    {
        var (vm, device) = Build();

        vm.IsMotorControlViaModbus = false;

        Assert.Empty(device.Sent);
        Assert.True(vm.IsMotorControlViaModbus);
        Assert.Contains("protocolo 10", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Routing_off_sends_the_flag_and_says_the_node_is_still_there()
    {
        var (vm, device) = Build();
        device.Sent.Clear();

        vm.IsRoutingEnabled = false;

        Assert.Equal("""{"servoComm":0}""", Assert.Single(device.Sent));
        Assert.Contains("continua presente", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Routing_on_sends_the_flag()
    {
        var (vm, device) = Build();
        vm.IsRoutingEnabled = false;
        device.Sent.Clear();

        vm.IsRoutingEnabled = true;

        Assert.Equal("""{"servoComm":1}""", Assert.Single(device.Sent));
    }

    [Fact]
    public void The_energy_reset_sends_one_and_says_what_confirms_it()
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        device.Sent.Clear();

        vm.ResetEnergyCommand.Execute(null);

        Assert.Equal("""{"resetServoEnergy":1}""", Assert.Single(device.Sent));
        Assert.Contains("queda do acumulado", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_energy_reset_is_unavailable_while_the_node_is_absent()
    {
        var (vm, device) = Build();

        device.PushTelemetry(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = false,
            ServoCommEnabled = true,
        });

        Assert.False(vm.ResetEnergyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("250")]
    [InlineData("1000")]
    [InlineData("10000")]
    public void A_poll_interval_inside_the_band_is_accepted(string text)
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        device.Sent.Clear();

        vm.PollIntervalText = text;
        Assert.True(vm.IsPollIntervalValid);
        Assert.Null(vm.PollIntervalError);

        vm.ApplyPollIntervalCommand.Execute(null);

        Assert.Equal($$"""{"servoPollMs":{{text}}}""", Assert.Single(device.Sent));
    }

    /// <summary>
    /// Out of range is refused in the entry, not clamped and not sent.
    /// </summary>
    /// <remarks>
    /// The Hub refuses these too, but it says so on its own serial port, which nobody is
    /// watching. Clamping would be worse than refusing: the operator would end up with an
    /// interval they never chose and nothing on screen to say so.
    /// </remarks>
    [Theory]
    [InlineData("249")]
    [InlineData("10001")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("")]
    public void A_poll_interval_outside_the_band_is_refused_and_never_sent(string text)
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        device.Sent.Clear();

        vm.PollIntervalText = text;

        Assert.False(vm.IsPollIntervalValid);
        Assert.NotNull(vm.PollIntervalError);
        Assert.False(vm.ApplyPollIntervalCommand.CanExecute(null));

        vm.ApplyPollIntervalCommand.Execute(null);
        Assert.Empty(device.Sent);
    }

    /// <summary>The card never carries a way to move the motor.</summary>
    /// <remarks>
    /// Speed stays exclusively on CN1 through <c>motorSetpoint</c>. A rotation control here
    /// would be a second command path to the same physical quantity, over a link with no
    /// acknowledgement - and <c>motorSetpoint</c> zero disables the module rather than
    /// stopping it, so a stop button here would be actively dangerous.
    /// </remarks>
    [Fact]
    public void No_command_from_this_card_ever_touches_the_motor()
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        device.Sent.Clear();

        vm.IsRoutingEnabled = false;
        vm.IsRoutingEnabled = true;
        vm.ResetEnergyCommand.Execute(null);
        vm.PollIntervalText = "2000";
        vm.ApplyPollIntervalCommand.Execute(null);

        Assert.NotEmpty(device.Sent);
        Assert.All(device.Sent, frame =>
        {
            Assert.DoesNotContain("motorSetpoint", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("servoEnable", frame, StringComparison.Ordinal);
        });
    }

    // ── Queue and error rate ─────────────────────────────────────────────────

    [Fact]
    public void The_queue_depth_is_shown_against_its_capacity()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating() with { ServoCommandQueueDepth = 8 });

        Assert.Equal(8, vm.QueueDepth);
        Assert.Equal("8/8", vm.QueueDepthText);
    }

    /// <summary>
    /// Queue state is visible even with the node absent, because it lives on the Hub.
    /// </summary>
    /// <remarks>
    /// With no acknowledgement on this link, watching a command enter the queue and be
    /// consumed is the only observation of its life. Hiding that while the node is away
    /// would make a command sent to an absent node completely invisible.
    /// </remarks>
    [Fact]
    public void The_queue_stays_visible_with_the_node_absent()
    {
        var (vm, device) = Build();

        device.PushTelemetry(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = false,
            ServoCommEnabled = true,
            ServoCommandQueueDepth = 3,
        });

        Assert.Equal("3/8", vm.QueueDepthText);
    }

    /// <summary>
    /// A single boot-time error must not read as a bad link.
    /// </summary>
    /// <remarks>
    /// The bench saw exactly one error in 256 reads, on the first transaction after boot.
    /// A rate over a window returns to zero once errors stop arriving; a threshold on the
    /// running total would latch on that one forever.
    /// </remarks>
    [Fact]
    public void The_error_rate_falls_back_to_zero_once_errors_stop()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(ok: 255, err: 1));
        for (var i = 1; i <= 10; i++)
        {
            device.PushTelemetry(Operating(ok: 255 + (i * 3), err: 1));
        }

        Assert.Equal("0,0 %", vm.ErrorRateText);
        Assert.Contains("err", vm.CounterText, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuing_errors_keep_the_rate_up()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(ok: 0, err: 0));
        for (var i = 1; i <= 10; i++)
        {
            // One failure for every three good transactions: a link in real trouble.
            device.PushTelemetry(Operating(ok: i * 3, err: i));
        }

        Assert.NotEqual("0,0 %", vm.ErrorRateText);
        Assert.NotEqual("—", vm.ErrorRateText);
    }

    /// <summary>
    /// Counters going backwards mean the node restarted, not a miraculously healthy link.
    /// </summary>
    [Fact]
    public void A_node_restart_drops_the_window_rather_than_computing_a_negative_rate()
    {
        var (vm, device) = Build();

        device.PushTelemetry(Operating(ok: 5000, err: 40));
        device.PushTelemetry(Operating(ok: 5100, err: 40));

        // The node reboots: its counters start again.
        device.PushTelemetry(Operating(ok: 3, err: 1));
        device.PushTelemetry(Operating(ok: 6, err: 1));

        Assert.Equal("0,0 %", vm.ErrorRateText);
    }

    [Fact]
    public void Losing_the_hub_clears_the_readouts()
    {
        var (vm, device) = Build();
        device.PushTelemetry(Operating());
        Assert.Equal("92,7", vm.RpmText);

        device.PushState(ConnectionState.Disconnected);

        Assert.Equal("—", vm.RpmText);
        Assert.Equal("—", vm.QueueDepthText);
        Assert.False(vm.ResetEnergyCommand.CanExecute(null));
    }

    [Fact]
    public void Disposing_unsubscribes_from_telemetry()
    {
        var (vm, device) = Build();
        vm.Dispose();

        device.PushTelemetry(Operating());

        Assert.Equal("—", vm.RpmText);
    }
}
