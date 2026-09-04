using System.Globalization;
using OpenTECHub.Services.Control;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The actuator-window allocator that splits one control effort across agitation and
/// aeration, including the overlap band.
/// </summary>
public class ActuatorWindowAllocatorTests
{
    [Fact]
    public void An_effort_below_the_start_rests_at_min_and_above_the_end_at_max()
    {
        var window = new ActuatorWindow("agitation", Min: 200, Max: 800, EffortStart: 20, EffortEnd: 80);

        Assert.Equal(200, window.Evaluate(0));
        Assert.Equal(200, window.Evaluate(20));
        Assert.Equal(800, window.Evaluate(80));
        Assert.Equal(800, window.Evaluate(100));
        Assert.Equal(500, window.Evaluate(50)); // halfway across the band
    }

    [Fact]
    public void A_zero_width_window_is_a_threshold()
    {
        var window = new ActuatorWindow("valve", Min: 0, Max: 1, EffortStart: 50, EffortEnd: 50);

        Assert.Equal(0, window.Evaluate(49.9));
        Assert.Equal(1, window.Evaluate(50));
        Assert.Equal(1, window.Evaluate(50.1));
    }

    [Fact]
    public void Overlapping_windows_both_ramp_through_the_overlap()
    {
        // Agitation leads (0-65) and aeration follows (35-100); 35-65 is the overlap where
        // both are moving. This is the "overlap band" the tuning bar draws.
        var allocator = new ActuatorWindowAllocator(
            new ActuatorWindow("agitation", 200, 800, 0, 65),
            new ActuatorWindow("aeration", 0.5, 5.0, 35, 100));

        // At the low end only agitation has begun.
        Assert.Equal(0.5, allocator.Allocate("aeration", 20));
        Assert.True(allocator.Allocate("agitation", 20) > 200);

        // Inside the overlap both are strictly between their limits.
        var agitationMid = allocator.Allocate("agitation", 50);
        var aerationMid = allocator.Allocate("aeration", 50);
        Assert.InRange(agitationMid, 200.0001, 799.9999);
        Assert.InRange(aerationMid, 0.5001, 4.9999);

        // At full effort both are pinned high.
        Assert.Equal(800, allocator.Allocate("agitation", 100));
        Assert.Equal(5.0, allocator.Allocate("aeration", 100));
    }

    [Fact]
    public void Allocate_returns_every_actuator_in_declaration_order()
    {
        var allocator = new ActuatorWindowAllocator(
            new ActuatorWindow("agitation", 200, 800, 0, 65),
            new ActuatorWindow("aeration", 0.5, 5.0, 35, 100));

        var values = allocator.Allocate(0);
        Assert.Equal(2, values.Count);
        Assert.Equal(200, values[0]);
        Assert.Equal(0.5, values[1]);
    }

    [Fact]
    public void An_unknown_actuator_name_throws()
    {
        var allocator = new ActuatorWindowAllocator(
            new ActuatorWindow("agitation", 200, 800, 0, 65));

        Assert.Throws<KeyNotFoundException>(() => allocator.Allocate("nitrogen", 50));
    }

    [Fact]
    public void An_empty_allocator_is_rejected()
        => Assert.Throws<ArgumentException>(() => new ActuatorWindowAllocator());

    [Theory]
    [InlineData("", 0, 100, 0, 50)]        // no name
    [InlineData("a", 100, 0, 0, 50)]       // min above max
    [InlineData("a", 0, 100, -5, 50)]      // effort start below 0
    [InlineData("a", 0, 100, 40, 30)]      // start above end
    [InlineData("a", 0, 100, 0, 120)]      // effort end above 100
    public void Validate_flags_a_malformed_window(
        string name, double min, double max, double start, double end)
    {
        var window = new ActuatorWindow(name, min, max, start, end);
        Assert.NotEmpty(window.Validate());
    }
}

/// <summary>
/// The cascade controller that maps dissolved oxygen onto agitation and aeration and,
/// at the wire boundary, onto the frozen combined actuation frame.
/// </summary>
public class CascadeControllerTests
{
    [Fact]
    public void A_larger_oxygen_deficit_commands_more_actuation()
    {
        // Two independent controllers, one seeing a big deficit, one a small one, stepped
        // identically. The bigger deficit must command at least as much of each actuator.
        var deep = CascadeController.CreateDefault(oxygenSetpoint: 30);
        var shallow = CascadeController.CreateDefault(oxygenSetpoint: 30);

        CascadeActuationResult deepResult = default!;
        CascadeActuationResult shallowResult = default!;
        for (var i = 0; i < 30; i++)
        {
            deepResult = deep.Update(dissolvedOxygenPercent: 5, dtSeconds: 2);
            shallowResult = shallow.Update(dissolvedOxygenPercent: 28, dtSeconds: 2);
        }

        Assert.True(deep.Effort > shallow.Effort,
            $"Deep deficit effort {deep.Effort} should exceed shallow {shallow.Effort}.");
        Assert.True(deepResult.AgitationRpm >= shallowResult.AgitationRpm);
        Assert.True(deepResult.AerationLpm >= shallowResult.AerationLpm);
    }

    [Fact]
    public void The_default_actuator_bands_stay_within_their_limits()
    {
        var cascade = CascadeController.CreateDefault(oxygenSetpoint: 40);

        for (var i = 0; i < 200; i++)
        {
            var result = cascade.Update(dissolvedOxygenPercent: 2, dtSeconds: 2);
            Assert.InRange(result.AgitationRpm, 200, 800);
            Assert.InRange(result.AerationLpm, 0.5, 5.0);
        }
    }

    [Fact]
    public void Closed_loop_tracks_the_oxygen_setpoint()
    {
        var cascade = CascadeController.CreateDefault(oxygenSetpoint: 30);
        var plant = new FirstOrderDeadTimePlant(initialOxygen: 60, deadTimeSeconds: 25, uptake: 1.4);

        double measured = 60;
        for (var i = 0; i < 2000; i++)
        {
            var result = cascade.Update(measured, dtSeconds: 2);
            var kLa = FirstOrderDeadTimePlant.KLaFromActuation(result.AgitationRpm, result.AerationLpm);
            measured = plant.Step(kLa, dt: 2);
        }

        Assert.True(Math.Abs(measured - 30) < 2.0, $"Settled at {measured}, not the 30 % setpoint.");
    }

    /// <summary>
    /// The cascade meets the wire in exactly one place: through
    /// <c>CommandBuilders.CascadeActuation</c>. This pins the mapping to the frozen frame,
    /// including the inverted <c>v_Flow</c> and the v.6 key order.
    /// </summary>
    [Fact]
    public void BuildCommand_produces_the_frozen_cascade_frame()
    {
        var result = new CascadeActuationResult(
            AgitationRpm: 300, AerationLpm: 2.5, OxygenSetpoint: 40.0, Terms: CascadeTerms.Empty);

        Assert.Equal(
            """{"flowSetpoint":2.5,"valve_1":0,"valve_2":0,"v_Flow":0,"oxygenMonitor":40.0,"motorSetpoint":300}""",
            CascadeController.BuildCommand(result).ToJson());
    }

    [Fact]
    public void BuildCommand_marks_the_vent_open_when_aeration_is_zero()
    {
        var result = new CascadeActuationResult(
            AgitationRpm: 300, AerationLpm: 0.0, OxygenSetpoint: 40.0, Terms: CascadeTerms.Empty);

        // v_Flow is inverted: 1 while no flow is commanded.
        Assert.Equal(
            """{"flowSetpoint":0.0,"valve_1":0,"valve_2":0,"v_Flow":1,"oxygenMonitor":40.0,"motorSetpoint":300}""",
            CascadeController.BuildCommand(result).ToJson());
    }
}

/// <summary>
/// The cascade frame the controller builds must not depend on the machine's locale, the
/// same rule every other wire test enforces.
/// </summary>
public class CascadeCultureTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public CascadeCultureTests()
    {
        var hostile = new CultureInfo("pt-BR");
        CultureInfo.CurrentCulture = hostile;
        CultureInfo.CurrentUICulture = hostile;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _original;
        CultureInfo.CurrentUICulture = _original;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_cascade_frame_uses_a_decimal_point_under_pt_BR()
    {
        var result = new CascadeActuationResult(
            AgitationRpm: 300, AerationLpm: 2.5, OxygenSetpoint: 40.5, Terms: CascadeTerms.Empty);

        Assert.Equal(
            """{"flowSetpoint":2.5,"valve_1":0,"valve_2":0,"v_Flow":0,"oxygenMonitor":40.5,"motorSetpoint":300}""",
            CascadeController.BuildCommand(result).ToJson());
    }
}

/// <summary>
/// Validation of the tuning record, mirroring the cascade-form rules in the UI spec.
/// </summary>
public class CascadeTuningTests
{
    [Fact]
    public void The_defaults_are_valid()
        => Assert.True(new CascadeTuning().IsValid);

    [Fact]
    public void An_interval_outside_the_firmware_band_is_rejected()
    {
        Assert.False((new CascadeTuning { IntervalSeconds = 0.05 }).IsValid);
        Assert.False((new CascadeTuning { IntervalSeconds = 90 }).IsValid);
    }

    [Fact]
    public void An_output_window_outside_zero_to_hundred_is_rejected()
    {
        Assert.False((new CascadeTuning { OutputMin = -5 }).IsValid);
        Assert.False((new CascadeTuning { OutputMax = 150 }).IsValid);
        Assert.False((new CascadeTuning { OutputMin = 60, OutputMax = 40 }).IsValid);
    }

    [Fact]
    public void An_inverted_integral_window_is_rejected()
        => Assert.False((new CascadeTuning { IntegralMin = 50, IntegralMax = 10 }).IsValid);

    [Fact]
    public void Negative_gains_are_rejected()
        => Assert.False((new CascadeTuning { Kp = -1 }).IsValid);

    [Fact]
    public void Non_finite_parameters_are_rejected()
        => Assert.False((new CascadeTuning { Kd = double.NaN }).IsValid);
}
