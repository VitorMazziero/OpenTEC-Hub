using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The A/B/C rig: three valves on two outputs, B and C on the same one. Intention → outputs
/// lives in one place (<see cref="GasRouting"/>), the wire builder refuses a dead-ended line,
/// and the wiring is a persisted setting whose absence means the documented default
/// (plan <c>2026-09-12-plano-valvulas-abc-ensaios.md</c>, Etapa 1).
/// </summary>
public class GasRoutingTests
{
    private static readonly GasRigConfiguration AOn2 = GasRigConfiguration.Default;
    private static readonly GasRigConfiguration AOn1 = new(GasInput.Input1);

    [Fact]
    public void Default_wiring_is_the_physical_documents_A_on_2_and_BC_on_1()
    {
        Assert.Equal(GasInput.Input2, GasRigConfiguration.Default.AirInletInput);
        Assert.Equal(GasInput.Input1, GasRigConfiguration.Default.VentAndNitrogenInput);
        Assert.Equal("A na entrada 2 · B/C na entrada 1", GasRigConfiguration.Default.Describe());
    }

    [Fact]
    public void BC_are_always_on_the_input_A_is_not()
    {
        Assert.Equal(GasInput.Input2, AOn1.VentAndNitrogenInput);
        Assert.Equal("A na entrada 1 · B/C na entrada 2", AOn1.Describe());
    }

    // Resolve: 3 routes × 2 wirings.
    [Theory]
    [InlineData(GasRoute.Closed, GasInput.Input2, false, false)]
    [InlineData(GasRoute.Reactor, GasInput.Input2, false, true)]
    [InlineData(GasRoute.VentAndNitrogen, GasInput.Input2, true, false)]
    [InlineData(GasRoute.Closed, GasInput.Input1, false, false)]
    [InlineData(GasRoute.Reactor, GasInput.Input1, true, false)]
    [InlineData(GasRoute.VentAndNitrogen, GasInput.Input1, false, true)]
    public void Resolve_puts_the_route_on_the_wired_input(GasRoute route, GasInput airInlet, bool v1, bool v2)
        => Assert.Equal((v1, v2), GasRouting.Resolve(route, new GasRigConfiguration(airInlet)));

    [Fact]
    public void Resolve_never_opens_both_outputs()
    {
        foreach (var rig in new[] { AOn1, AOn2 })
        {
            foreach (var route in Enum.GetValues<GasRoute>())
            {
                var (v1, v2) = GasRouting.Resolve(route, rig);
                Assert.False(v1 && v2, $"{route} on {rig.Describe()} opened both outputs");
            }
        }
    }

    // Interpret: the nominal pairs read back as the route, on both wirings...
    [Theory]
    [InlineData(false, false, 0.0, GasInput.Input2, ObservedGasRoute.Closed)]
    [InlineData(false, true, 2.5, GasInput.Input2, ObservedGasRoute.Reactor)]
    [InlineData(true, false, 2.5, GasInput.Input2, ObservedGasRoute.VentAndNitrogen)]
    [InlineData(true, false, 0.0, GasInput.Input2, ObservedGasRoute.VentAndNitrogen)]
    [InlineData(true, false, 2.5, GasInput.Input1, ObservedGasRoute.Reactor)]
    [InlineData(false, true, 2.5, GasInput.Input1, ObservedGasRoute.VentAndNitrogen)]
    // ...and the two anomalies have their own names regardless of wiring.
    [InlineData(true, true, 2.5, GasInput.Input2, ObservedGasRoute.BothOpen)]
    [InlineData(true, true, 0.0, GasInput.Input1, ObservedGasRoute.BothOpen)]
    [InlineData(false, false, 2.5, GasInput.Input2, ObservedGasRoute.DeadEnd)]
    [InlineData(false, false, 0.01, GasInput.Input1, ObservedGasRoute.DeadEnd)]
    public void Interpret_reads_the_echo_back(bool v1, bool v2, double setpoint, GasInput airInlet, ObservedGasRoute expected)
        => Assert.Equal(expected, GasRouting.Interpret(v1, v2, setpoint, new GasRigConfiguration(airInlet)));

    [Fact]
    public void Resolve_and_Interpret_round_trip_on_both_wirings()
    {
        foreach (var rig in new[] { AOn1, AOn2 })
        {
            foreach (var route in Enum.GetValues<GasRoute>())
            {
                var (v1, v2) = GasRouting.Resolve(route, rig);
                var setpoint = route == GasRoute.Closed ? 0.0 : 2.5;
                Assert.Equal(Enum.Parse<ObservedGasRoute>(route.ToString()), GasRouting.Interpret(v1, v2, setpoint, rig));
            }
        }
    }

    [Fact]
    public void Texts_use_the_hardware_names()
    {
        Assert.Equal("Fechado", GasRouting.Describe(GasRoute.Closed));
        Assert.Equal("Reator (A)", GasRouting.Describe(GasRoute.Reactor));
        Assert.Equal("Descarga + N₂ (B/C)", GasRouting.Describe(GasRoute.VentAndNitrogen));
        Assert.Equal("Reator (A na entrada 2)", GasRouting.Describe(GasRoute.Reactor, AOn2));
        Assert.Equal("Descarga + N₂ (B/C na entrada 2)", GasRouting.Describe(GasRoute.VentAndNitrogen, AOn1));
        Assert.Equal("A e B/C abertas", GasRouting.Describe(ObservedGasRoute.BothOpen));
        Assert.Equal("Gás sem destino", GasRouting.Describe(ObservedGasRoute.DeadEnd));
        Assert.Equal("valve_1=0 (B/C) · valve_2=1 (A)", GasRouting.DescribeWire(false, true, AOn2));
        Assert.Equal("valve_1=1 (A) · valve_2=0 (B/C)", GasRouting.DescribeWire(true, false, AOn1));
        Assert.True(GasRouting.IsNominal(ObservedGasRoute.Reactor));
        Assert.False(GasRouting.IsNominal(ObservedGasRoute.DeadEnd));
    }

    // ── Builder ──────────────────────────────────────────────────────────────

    [Fact]
    public void FlowRoute_Reactor_on_the_default_wiring_is_valve_2()
        => Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.Reactor, AOn2).ToJson());

    [Fact]
    public void FlowRoute_VentAndNitrogen_on_the_default_wiring_is_valve_1()
        => Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.VentAndNitrogen, AOn2).ToJson());

    [Fact]
    public void FlowRoute_with_A_on_1_inverts_the_pair()
    {
        Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.Reactor, AOn1).ToJson());
        Assert.Equal(
            """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
            CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.VentAndNitrogen, AOn1).ToJson());
    }

    [Fact]
    public void FlowRoute_Closed_with_zero_setpoint_is_the_safe_shape()
        => Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            CommandBuilders.FlowRoute(0.0, 50.0, GasRoute.Closed, AOn2).ToJson());

    [Fact]
    public void FlowRoute_refuses_a_setpoint_with_nowhere_to_go()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.Closed, AOn2));
        Assert.Contains("sem saída", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CommandBuilders.FlowRoute(0.01, 50.0, GasRoute.Closed, AOn1));
    }

    [Fact]
    public void FlowRoute_VentAndNitrogen_with_zero_setpoint_is_nitrogen_only()
        // kLa deoxygenation: B/C open, no air commanded, main shutoff asserted by the zero setpoint.
        => Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":1}""",
            CommandBuilders.FlowRoute(0.0, 50.0, GasRoute.VentAndNitrogen, AOn2).ToJson());

    [Fact]
    public void FlowRoute_clamps_like_FlowSetpoint()
        => Assert.Equal(
            CommandBuilders.FlowSetpoint(999.0, 50.0, valve1: false, valve2: true).ToJson(),
            CommandBuilders.FlowRoute(999.0, 50.0, GasRoute.Reactor, AOn2).ToJson());

    [Fact]
    public void FlowSafeStop_is_unchanged_and_reads_as_Closed()
    {
        Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            CommandBuilders.FlowSafeStop(50.0).ToJson());
        Assert.Equal(ObservedGasRoute.Closed, GasRouting.Interpret(false, false, 0.0, AOn2));
    }

    [Fact]
    public void FlowRoute_is_culture_invariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            Assert.Equal(
                """{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""",
                CommandBuilders.FlowRoute(2.5, 50.0, GasRoute.Reactor, AOn2).ToJson());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void GasRig_setting_defaults_to_the_documented_wiring()
    {
        var settings = new AppSettings();
        Assert.Equal(GasInput.Input2, settings.GasRig.AirInletInput);
        Assert.Equal(GasRigConfiguration.Default, settings.GasRig.ToConfiguration());
    }

    [Fact]
    public void A_settings_file_without_GasRig_migrates_to_the_default_silently()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"Version":2}""", Json)!;
        Assert.Equal(GasRigConfiguration.Default, loaded.GasRig.ToConfiguration());
    }

    [Fact]
    public void GasRig_setting_round_trips_as_a_readable_name()
    {
        var settings = new AppSettings { GasRig = GasRigSettings.From(new GasRigConfiguration(GasInput.Input1)) };
        var json = JsonSerializer.Serialize(settings, Json);
        Assert.Contains("\"AirInletInput\":\"Input1\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<AppSettings>(json, Json)!;
        Assert.Equal(GasInput.Input1, back.GasRig.AirInletInput);
        Assert.Equal(GasInput.Input2, back.GasRig.ToConfiguration().VentAndNitrogenInput);
    }
}
