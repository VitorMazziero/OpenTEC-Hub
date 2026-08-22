using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Telemetry;
using TecnalHub.Protocol;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// Gain scheduling (WP8 part 2): the piecewise-linear schedule, the bounded-transition scheduler,
/// and the cascade service driving the controller gains from the live effort.
/// </summary>
public sealed class GainScheduleTests
{
    private static GainSchedule Schedule() => new(
    [
        new GainScheduleBreakpoint(0, 0.10, 0.010, 0.0),
        new GainScheduleBreakpoint(50, 0.30, 0.020, 0.0),
        new GainScheduleBreakpoint(100, 0.50, 0.040, 0.0),
    ]);

    // ── GainSchedule (pure) ──────────────────────────────────────────────────

    [Fact]
    public void Gains_at_a_breakpoint_are_that_breakpoint()
    {
        var g = Schedule().GainsAt(50);
        Assert.Equal(0.30, g.Kp, precision: 12);
        Assert.Equal(0.020, g.Ki, precision: 12);
    }

    [Fact]
    public void Gains_interpolate_linearly_between_breakpoints()
    {
        // Midway between effort 0 (Kp 0.10) and 50 (Kp 0.30).
        var g = Schedule().GainsAt(25);
        Assert.Equal(0.20, g.Kp, precision: 12);
        Assert.Equal(0.015, g.Ki, precision: 12);
    }

    [Fact]
    public void Gains_are_held_flat_outside_the_mapped_range()
    {
        var schedule = Schedule();
        Assert.Equal(0.10, schedule.GainsAt(-20).Kp, precision: 12);
        Assert.Equal(0.50, schedule.GainsAt(140).Kp, precision: 12);
    }

    [Fact]
    public void Segment_maps_effort_to_the_bracketing_interval()
    {
        var schedule = Schedule();
        Assert.Equal(0, schedule.SegmentAt(10));
        Assert.Equal(1, schedule.SegmentAt(60));
        Assert.Equal(1, schedule.SegmentAt(100));
    }

    [Theory]
    [InlineData(1)] // fewer than two breakpoints
    public void Too_few_breakpoints_are_rejected(int count)
    {
        var breakpoints = Enumerable.Range(0, count)
            .Select(i => new GainScheduleBreakpoint(i * 10, 0.2, 0.02, 0)).ToArray();
        Assert.NotEmpty(GainSchedule.ValidateBreakpoints(breakpoints));
    }

    [Fact]
    public void Non_increasing_efforts_and_negative_gains_are_rejected()
    {
        Assert.NotEmpty(GainSchedule.ValidateBreakpoints(
        [
            new GainScheduleBreakpoint(50, 0.2, 0.02, 0),
            new GainScheduleBreakpoint(50, 0.3, 0.03, 0),
        ]));
        Assert.NotEmpty(GainSchedule.ValidateBreakpoints(
        [
            new GainScheduleBreakpoint(0, -0.1, 0.02, 0),
            new GainScheduleBreakpoint(50, 0.3, 0.03, 0),
        ]));
    }

    // ── GainScheduler (pure, bounded transitions) ────────────────────────────

    [Fact]
    public void Effective_gains_slew_toward_the_target_within_the_bound()
    {
        var baseTuning = new CascadeTuning { Kp = 0.10, Ki = 0.010 };
        // Slew 0.05/s: one 1 s step moves Kp by at most 0.05, not the full jump to the target.
        var scheduler = new GainScheduler(Schedule(), baseTuning, maxSlewPerSecond: 0.05,
            initial: new GainSet(0.10, 0.010, 0.0));

        var step = scheduler.Step(effortPercent: 100, dtSeconds: 1.0);

        Assert.Equal(0.15, step.Effective.Kp, precision: 12); // 0.10 + 0.05, bounded
        Assert.Equal(0.50, step.Target.Kp, precision: 12);    // target is the far breakpoint
        Assert.Equal(step.Effective.Kp, step.Tuning.Kp, precision: 12);
        // The non-gain fields of the base tuning are preserved.
        Assert.Equal(baseTuning.PredictionHorizonSeconds, step.Tuning.PredictionHorizonSeconds);
    }

    [Fact]
    public void A_segment_crossing_is_reported_once()
    {
        var scheduler = new GainScheduler(Schedule(), new CascadeTuning(), maxSlewPerSecond: 100,
            initial: new GainSet(0.10, 0.010, 0.0));

        Assert.False(scheduler.Step(10, 1.0).SegmentChanged); // first step, segment 0
        Assert.False(scheduler.Step(20, 1.0).SegmentChanged); // still segment 0
        Assert.True(scheduler.Step(70, 1.0).SegmentChanged);  // crossed into segment 1
        Assert.False(scheduler.Step(80, 1.0).SegmentChanged); // still segment 1
    }

    // ── CascadeService integration ───────────────────────────────────────────

    [Fact]
    public void An_enabled_schedule_drives_the_controller_gains_up_with_effort_and_journals_it()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = new CascadeSettings { OxygenSetpointPercent = 30, Kp = 0.25 },
            GainSchedule = new GainScheduleSettings
            {
                Enabled = true,
                MaxGainSlewPerSecond = 5.0,
                Breakpoints =
                [
                    new GainScheduleBreakpointSettings(0, 0.10, 0.010, 0.0),
                    new GainScheduleBreakpointSettings(50, 0.30, 0.020, 0.0),
                    new GainScheduleBreakpointSettings(100, 0.50, 0.040, 0.0),
                ],
            },
        });
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        using var journal = new EventJournal(arbiter, arbiter, settings);
        using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock, journal);

        Assert.True(service.IsGainSchedulingEnabled);
        service.Arm();

        // Oxygen held far below setpoint drives the effort up across the breakpoint at 50 %.
        for (var i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 1 });
        }

        Assert.True(service.Terms.Output > 50, "The effort should climb past the 50 % breakpoint.");
        // The scheduled gains rose with effort, above the base 0.25.
        Assert.True(service.Tuning.Kp > 0.25, $"Kp {service.Tuning.Kp} should have been scheduled up.");
        Assert.Equal(2, service.GainScheduleSegment); // 1-based: the upper segment
        Assert.Contains(
            journal.Snapshot(),
            e => e.Source == AuditSource.Application && e.Message.Contains("Escalonamento de ganho", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_schedule_leaves_the_single_base_tuning()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = new CascadeSettings { OxygenSetpointPercent = 30, Kp = 0.25 },
            GainSchedule = new GainScheduleSettings { Enabled = false },
        });
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);

        Assert.False(service.IsGainSchedulingEnabled);
        service.Arm();
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 1 });
        }

        Assert.Equal(0.25, service.Tuning.Kp, precision: 12);
    }
}
