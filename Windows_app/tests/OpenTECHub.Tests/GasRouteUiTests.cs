using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Etapa 6 of the A/B/C plan: the Controle drawer picks the flowmeter input to energise (the
/// valves' roles are fixed — A air in, B N₂ or nothing, C purge), reads the echo in the rig's
/// words, and Avançado sends any combination with only a warning.
/// </summary>
public class GasRouteUiTests
{
    private static FlowControlViewModel Build(GasInput airInlet = GasInput.Input2)
        => new(initialMaxFlow: 50.0, settings: new MemorySettingsService(new AppSettings { GasRig = new GasRigSettings { AirInletInput = airInlet } }));

    [Fact]
    public void Selector_labels_say_what_each_input_drives_on_the_configured_wiring()
    {
        var flow = Build();
        Assert.Equal("Entrada 1 · B + C (N₂ ou nada · purga de ar)", flow.Input1Choice);
        Assert.Equal("Entrada 2 · A (ar ao reator)", flow.Input2Choice);
        Assert.Equal("Arranjo: A na entrada 2 · B/C na entrada 1", flow.RigDescription);

        var swapped = Build(GasInput.Input1);
        Assert.Equal("Entrada 1 · A (ar ao reator)", swapped.Input1Choice);
        Assert.Equal("Entrada 2 · B + C (N₂ ou nada · purga de ar)", swapped.Input2Choice);
        Assert.Equal("Entrada 1 (A)", swapped.Input1Label);
        Assert.Equal("Entrada 2 (B + C)", swapped.Input2Label);
    }

    [Fact]
    public void Picking_an_input_stages_that_input_alone_and_the_route_follows_the_wiring()
    {
        var flow = Build();

        flow.IsInput2Requested = true;
        Assert.True(flow.RequestedValve2);
        Assert.False(flow.RequestedValve1);
        Assert.Equal(GasRoute.Reactor, flow.RequestedRoute);
        Assert.Equal("Reator (A)", flow.RequestedRouteText);

        flow.IsInput1Requested = true;
        Assert.True(flow.RequestedValve1);
        Assert.False(flow.RequestedValve2);
        Assert.Equal(GasRoute.VentAndNitrogen, flow.RequestedRoute);

        flow.IsRouteClosed = true;
        Assert.False(flow.RequestedValve1);
        Assert.False(flow.RequestedValve2);
        Assert.Equal(GasRoute.Closed, flow.RequestedRoute);

        // The same pick lands on the other pin on the other wiring.
        var swapped = Build(GasInput.Input1);
        swapped.SelectRoute(GasRoute.Reactor);
        Assert.True(swapped.RequestedValve1);
        Assert.False(swapped.RequestedValve2);
        Assert.True(swapped.IsInput1Requested);
    }

    [Fact]
    public void Advanced_sends_both_inputs_and_a_dead_end_with_a_warning_never_a_refusal()
    {
        var flow = Build();
        flow.HasFlowmeterTelemetry = true;
        flow.IsFlowmeterOnline = true;

        flow.RequestedValve1 = true;
        flow.RequestedValve2 = true;
        Assert.True(flow.IsBothRequested);
        Assert.Null(flow.RequestedRoute);
        Assert.Equal("A e B/C abertas", flow.RequestedRouteText);
        Assert.Contains("Entradas 1 e 2 acionadas", flow.RouteWarningFor(flowEnabled: true, setpoint: 2.0), StringComparison.Ordinal);
        Assert.True(flow.TryBuildRequested(2.0, flowEnabled: true, out var both));
        Assert.Equal("""{"flowSetpoint":2.0,"maxFlow":50.0,"valve_1":1,"valve_2":1,"v_Flow":0}""", both.ToJson());

        flow.RequestedValve1 = false;
        flow.RequestedValve2 = false;
        Assert.Contains("Gás sem destino", flow.RouteWarningFor(flowEnabled: true, setpoint: 2.0), StringComparison.Ordinal);
        Assert.Null(flow.RouteWarningFor(flowEnabled: true, setpoint: 0.0));
        Assert.Null(flow.RouteWarningFor(flowEnabled: false, setpoint: 2.0));
        Assert.True(flow.TryBuildRequested(2.0, flowEnabled: true, out var deadEnd));
        Assert.Equal("""{"flowSetpoint":2.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""", deadEnd.ToJson());

        // Closing the line on purpose is not a dead end: the operator asked for no gas.
        flow.RequestedMainValveClosed = true;
        Assert.Null(flow.RouteWarningFor(flowEnabled: true, setpoint: 2.0));
    }

    [Theory]
    [InlineData(0, 0, 0.0, "Fechado", false)]
    [InlineData(0, 1, 3.0, "Reator (A)", false)]
    [InlineData(1, 0, 3.0, "Descarga + N₂ (B/C)", false)]
    [InlineData(0, 0, 3.0, "Gás sem destino", true)]
    [InlineData(1, 1, 3.0, "A e B/C abertas", true)]
    public void Observed_route_text_names_the_five_states(int valve1, int valve2, double setpoint, string expected, bool anomalous)
    {
        var flow = Build();
        Assert.Equal("—", flow.ObservedRouteText);

        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = valve1, FlowValve2 = valve2, FlowSetpoint = setpoint });

        Assert.Equal(expected, flow.ObservedRouteText);
        Assert.Equal(anomalous, flow.IsObservedRouteAnomalous);
        Assert.Equal($"valve_1={valve1} (B/C) · valve_2={valve2} (A)", flow.WireText);
    }

    [Fact]
    public void A_wiring_change_in_settings_rereads_every_derived_text()
    {
        var settings = new MemorySettingsService();
        var flow = new FlowControlViewModel(initialMaxFlow: 50.0, settings: settings);
        flow.UpdateTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowValve1 = 1, FlowValve2 = 0, FlowSetpoint = 3.0 });
        flow.IsInput1Requested = true;
        Assert.Equal("Descarga + N₂ (B/C)", flow.ObservedRouteText);
        Assert.Equal(GasRoute.VentAndNitrogen, flow.RequestedRoute);

        var changed = new List<string?>();
        flow.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.Update(s => s with { GasRig = new GasRigSettings { AirInletInput = GasInput.Input1 } });

        Assert.Equal("Reator (A)", flow.ObservedRouteText);
        Assert.Equal(GasRoute.Reactor, flow.RequestedRoute);
        Assert.Equal("Entrada 1 · A (ar ao reator)", flow.Input1Choice);
        Assert.Contains(nameof(FlowControlViewModel.RigDescription), changed);
        Assert.Contains(nameof(FlowControlViewModel.ObservedRouteText), changed);
    }
}
