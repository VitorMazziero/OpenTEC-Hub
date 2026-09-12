using OpenTECHub.Protocol;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Etapa 3 of the A/B/C plan: the simulator is the physical rig. Air transfers oxygen only
/// through A, nitrogen strips it only through B with the source open, a dead-ended line is
/// observable (no flow, rising pressure), and the wiring is a model setting.
/// </summary>
public class SimulatorGasRigTests
{
    private static readonly ServoPowerModelOptions Quiet = ServoPowerModelOptions.Default with
    {
        SpeedTimeConstantSeconds = 0.0,
        TorqueTimeConstantSeconds = 0.0,
        VentFlowPulseDurationSeconds = 0.0,
    };

    /// <summary>Abiotic broth: no uptake, so what the gas does is all that moves DO.</summary>
    private static readonly CultivationProfile Abiotic = new("abiotic",
        [new CultivationPhase("Water", double.PositiveInfinity, 0.0, 12.0, 0.0, 0.0)]);

    private static DeviceModel Build(GasInput airInlet = GasInput.Input2, bool nitrogenSource = true)
        => new(clock: new AcceleratedClock(), profile: Abiotic, probeDeadTime: TimeSpan.Zero, randomSeed: 7, servoPowerModel: Quiet)
        {
            MotorRpm = 400,
            GasRig = new GasRigConfiguration(airInlet),
            NitrogenSourceOpen = nitrogenSource,
        };

    private static void Advance(DeviceModel model, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.5)
        {
            model.Tick(Math.Min(0.5, seconds - elapsed));
        }
    }

    private static void Command(DeviceModel model, double setpoint, GasRoute route)
        => WireCodec.ApplyCommand(model, CommandBuilders.FlowRoute(setpoint, 50.0, route, model.GasRig).ToJson(), out _);

    private static void Deoxygenate(DeviceModel model, double to)
    {
        Command(model, 0.0, GasRoute.VentAndNitrogen);
        for (var i = 0; i < 400 && model.ReportedOxygen > to; i++)
        {
            Advance(model, 5.0);
        }
        Assert.True(model.ReportedOxygen <= to, $"could not deoxygenate below {to}: {model.ReportedOxygen:F1}");
    }

    [Fact]
    public void Observed_route_follows_the_wiring()
    {
        var m = Build();
        Command(m, 3.0, GasRoute.Reactor);
        Assert.Equal((0, 1), (m.Valve1, m.Valve2));
        Assert.Equal(ObservedGasRoute.Reactor, m.ObservedRoute);

        Command(m, 3.0, GasRoute.VentAndNitrogen);
        Assert.Equal((1, 0), (m.Valve1, m.Valve2));
        Assert.Equal(ObservedGasRoute.VentAndNitrogen, m.ObservedRoute);

        var swapped = Build(GasInput.Input1);
        Command(swapped, 3.0, GasRoute.Reactor);
        Assert.Equal((1, 0), (swapped.Valve1, swapped.Valve2));
        Assert.Equal(ObservedGasRoute.Reactor, swapped.ObservedRoute);
    }

    [Fact]
    public void Air_transfers_oxygen_only_through_A()
    {
        var m = Build();
        Deoxygenate(m, 20.0);
        var floor = m.ReportedOxygen;

        // Vent (C) with air: nothing reaches the broth (the nitrogen keeps stripping, in fact).
        Command(m, 3.0, GasRoute.VentAndNitrogen);
        Advance(m, 60.0);
        Assert.True(m.ReportedOxygen <= floor + 0.5, $"vented air must not oxygenate: {floor:F1} → {m.ReportedOxygen:F1}");
        Assert.Equal(0.0, m.ReadReactorFlow());

        // Reactor (A): DO climbs.
        Command(m, 3.0, GasRoute.Reactor);
        Advance(m, 120.0);
        Assert.True(m.ReportedOxygen > floor + 10.0, $"air through A must oxygenate: {floor:F1} → {m.ReportedOxygen:F1}");
        Assert.True(m.ReadReactorFlow() > 2.5);
    }

    [Fact]
    public void Nitrogen_strips_only_through_BC_and_only_with_the_source_open()
    {
        var open = Build(nitrogenSource: true);
        var start = open.ReportedOxygen;
        Command(open, 0.0, GasRoute.VentAndNitrogen);
        Advance(open, 120.0);
        Assert.True(open.ReportedOxygen < start - 10.0, $"N₂ through B must strip: {start:F1} → {open.ReportedOxygen:F1}");
        Assert.True(open.NitrogenFlowing);

        // Same command, source shut at the wall: nothing happens to DO.
        var shut = Build(nitrogenSource: false);
        start = shut.ReportedOxygen;
        Command(shut, 0.0, GasRoute.VentAndNitrogen);
        Advance(shut, 120.0);
        Assert.False(shut.NitrogenFlowing);
        Assert.InRange(shut.ReportedOxygen, start - 2.0, start + 2.0);

        // Source open but B/C closed (reactor route): no stripping either.
        var reactor = Build(nitrogenSource: true);
        start = reactor.ReportedOxygen;
        Command(reactor, 0.0, GasRoute.Closed);
        Advance(reactor, 120.0);
        Assert.False(reactor.NitrogenFlowing);
        Assert.InRange(reactor.ReportedOxygen, start - 2.0, start + 2.0);
    }

    [Fact]
    public void Nitrogen_left_open_scenario_strips_even_when_the_model_says_shut()
    {
        var m = Build(nitrogenSource: false);
        m.Scenario = Scenario.NitrogenLeftOpen;
        var start = m.ReportedOxygen;

        // What a power assay's vent stabilisation looks like with the source forgotten open.
        Command(m, 3.0, GasRoute.VentAndNitrogen);
        Advance(m, 90.0);
        Assert.True(m.NitrogenFlowing);
        Assert.True(m.ReportedOxygen < start - 5.0, $"the guard's trigger: {start:F1} → {m.ReportedOxygen:F1}");
    }

    [Fact]
    public void Nitrogen_left_open_cannot_enter_while_A_is_open()
    {
        // B and C are on the same output: with A on they are off, so a forgotten source is
        // harmless during the reactor phase — it only bites while B/C is open (vent stabilisation).
        var m = Build(nitrogenSource: false);
        m.Scenario = Scenario.NitrogenLeftOpen;
        Deoxygenate(m, 30.0);
        var floor = m.ReportedOxygen;

        Command(m, 3.0, GasRoute.Reactor);
        Assert.False(m.NitrogenFlowing);
        Advance(m, 120.0);
        Assert.True(m.ReportedOxygen > floor + 10.0, $"A open, B shut: DO must recover: {floor:F1} → {m.ReportedOxygen:F1}");

        // Closed (setpoint 0, both shut) is equally immune.
        Command(m, 0.0, GasRoute.Closed);
        Assert.False(m.NitrogenFlowing);
    }

    [Fact]
    public void Dead_ended_line_reads_no_flow_and_rising_pressure()
    {
        var m = Build();
        // Both outputs shut with a setpoint: the raw wire shape a manual operator can produce.
        WireCodec.ApplyCommand(m, """{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""", out _);
        Assert.Equal(ObservedGasRoute.DeadEnd, m.ObservedRoute);

        Advance(m, 30.0);
        Assert.True(m.ReadFlow() < 0.3, $"a dead end passes nothing: {m.ReadFlow():F2}");
        Assert.True(m.ReadPressure() > 20.0, $"a dead end builds pressure: {m.ReadPressure():F1} kPa");
        Assert.Equal(0.0, m.ReadReactorFlow());

        // Giving the gas a destination clears it.
        Command(m, 3.0, GasRoute.Reactor);
        Advance(m, 30.0);
        Assert.True(m.ReadFlow() > 2.5);
        Assert.True(m.ReadPressure() < 10.0);
    }

    [Fact]
    public void Switching_C_to_A_dips_the_flow_briefly_then_recovers()
    {
        var m = Build(nitrogenSource: false);
        Command(m, 4.0, GasRoute.VentAndNitrogen);
        Advance(m, 30.0);
        Assert.InRange(m.ReadFlow(), 3.8, 4.2);

        Command(m, 4.0, GasRoute.Reactor);
        m.Tick(0.2);
        var justAfter = m.ReadFlow();
        Assert.True(justAfter < 3.7, $"the sparger head must show as a dip: {justAfter:F2}");

        Advance(m, 15.0);
        Assert.InRange(m.ReadFlow(), 3.8, 4.2);
    }

    [Fact]
    public void Main_shutoff_is_echoed_as_the_line_state_not_a_vent()
    {
        var m = Build();
        Command(m, 0.0, GasRoute.Closed);
        Assert.True(m.MainLineClosed);
        Command(m, 3.0, GasRoute.Reactor);
        Assert.False(m.MainLineClosed);

        var frame = WireCodec.BuildTelemetry(m);
        Assert.Contains("\"ValveFlow\":0", frame, StringComparison.Ordinal);
        Assert.Contains("\"Valve1\":0,\"Valve2\":1", frame, StringComparison.Ordinal);
    }
}
