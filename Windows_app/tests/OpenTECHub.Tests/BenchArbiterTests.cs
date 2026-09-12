using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Safety;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Suite B4 of <c>docs/processes/TESTES_AUTOMATICOS_BANCADA.md</c>: what the arbiter does with
/// the node-configuration and health traffic. The arbiter is deterministic and lives in the app,
/// so these run without hardware through the real <see cref="CommandArbiter"/> — the wire half of
/// the same contract (the echoes) is B3 in <c>opentec-harness bench-test</c>.
/// </summary>
public class BenchArbiterTests
{
    private static SensorSnapshot FlowmeterWithTuning(double? kp = 0.8) => new()
    {
        FlowmeterOnline = true,

        FlowKp = kp,
        FlowKi = kp is null ? null : 0.15,
        FlowFfGain = kp is null ? null : 0.106,
        FlowFfOffset = kp is null ? null : 0.0,
        FlowRampRate = kp is null ? null : 1.0,
    };

    [Fact]
    public void B4_1_Tuning_is_refused_while_an_assay_owns_aeration()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: new ManualDispatcher(arbiter), settings: new MemorySettingsService());
        flow.UpdateTelemetry(FlowmeterWithTuning());
        Assert.True(flow.CanEditTuning);

        // A power/kLa assay in capture holds Aeration exactly like this.
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "ensaio de potência em captura");
        device.Sent.Clear();

        flow.KpText = "0.9";
        flow.SendTuningCommand.Execute(null);

        Assert.Empty(device.Sent);
        Assert.Contains("recus", flow.TuningStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void B4_2_Tuning_is_accepted_outside_an_assay_with_the_exact_frame()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: new ManualDispatcher(arbiter), settings: new MemorySettingsService());
        flow.UpdateTelemetry(FlowmeterWithTuning());
        device.Sent.Clear();

        // Staged values are the operator's text, not the echo; stage all five explicitly.
        flow.KpText = "0.9";
        flow.KiText = "0.15";
        flow.FfGainText = "0.106";
        flow.FfOffsetText = "0";
        flow.RampRateText = "1";
        flow.SendTuningCommand.Execute(null);

        Assert.Equal("""{"flowKp":0.9,"flowKi":0.15,"flowFfGain":0.106,"flowFfOffset":0.0,"flowRampRate":1.0}""", Assert.Single(device.Sent));
        Assert.Contains("enviada", flow.TuningStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void B4_3_NodeDiag_request_claims_no_actuator_and_leaves_owners_alone()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration, ActuatorId.Agitation], "cascata engajada");
        var transfers = 0;
        arbiter.OwnershipChanged += _ => transfers++;
        device.Sent.Clear();

        ((IDeviceService)arbiter).RequestNodeDiag("all");

        Assert.Equal("""{"nodeDiag":"all"}""", Assert.Single(device.Sent));
        Assert.Equal(CommandOwner.Automatic, arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal(CommandOwner.Automatic, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(0, transfers);
    }

    [Fact]
    public async Task B4_4_Safe_stop_sends_mode_0_with_flow_0_before_pumpComm_0_and_returns_everything_to_manual()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        var coordinator = new SafetyCoordinator(arbiter, device);
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "cascata engajada");
        device.Sent.Clear();

        // The frames ControlViewModel builds: the core safe frame merged with the pump's
        // {"mode":0}, then the routing disable as its own frame (PROTOCOL §3.5).
        var safeFrame = CommandBuilders.CoreSafeStop(50.0).Merge(CommandBuilders.PumpStopProfile());
        var result = await coordinator.ExecuteGlobalSafeStopAsync(safeFrame, CommandBuilders.PumpRoutingDisabled(), "parada segura");

        Assert.True(result.Accepted);
        Assert.Equal(2, device.Sent.Count);
        Assert.Contains("\"mode\":0", device.Sent[0], StringComparison.Ordinal);
        Assert.Contains("\"flowSetpoint\":0.0", device.Sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("speed", device.Sent[0], StringComparison.Ordinal);
        Assert.Equal("""{"pumpComm":0}""", device.Sent[1]);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
    }

    [Fact]
    public void B4_5_Tuning_fields_follow_the_echo_and_are_not_sticky()
    {
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, settings: new MemorySettingsService());

        flow.UpdateTelemetry(FlowmeterWithTuning(kp: null));
        Assert.False(flow.CanEditTuning);
        Assert.Contains("Aguardando", flow.TuningUnavailableText, StringComparison.Ordinal);

        flow.UpdateTelemetry(FlowmeterWithTuning());
        Assert.True(flow.CanEditTuning);
        Assert.Null(flow.TuningUnavailableText);

        // The node dropped its echo (route down, node gone): the field closes on the next frame.
        flow.UpdateTelemetry(FlowmeterWithTuning(kp: null));
        Assert.False(flow.CanEditTuning);
    }
}
