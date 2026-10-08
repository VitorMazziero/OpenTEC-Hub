using System.Text.Json.Nodes;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaMapping;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CascadeStateRestorationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoresFullControllerAndProducesTheSameNextStep(int mode)
    {
        var original = CascadeController.CreateDefault(30);
        if (mode == 1) original.SetAllocation(SingleActuatorAllocation.Agitation(200, 800, 2));
        if (mode == 2) original.SetAllocation(SingleActuatorAllocation.Aeration(.5, 5, 400));
        if (mode == 3) original.SetAllocation(new KlaPathAllocation([
            new KlaAllocationSample(0, .5, 200), new KlaAllocationSample(50, 2, 500), new KlaAllocationSample(100, 5, 800)]));
        original.Preload(40);
        original.Update(25, 3); original.Update(24, 3);
        var snapshot = original.CaptureStateJson();
        var restored = CascadeController.CreateDefault(50);
        restored.Retune(new CascadeTuning { Kp = .2, Ki = .01 });
        restored.Update(10, 3);
        restored.RestoreStateJson(snapshot);
        Assert.Equal(snapshot, restored.CaptureStateJson());
        Assert.Equal(original.Update(23, 3), restored.Update(23, 3));
        Assert.Equal(original.CaptureStateJson(), restored.CaptureStateJson());
    }

    [Theory]
    [InlineData("allocation")]
    [InlineData("pid")]
    [InlineData("missing")]
    public void InvalidSnapshotDoesNotPartiallyChangeController(string defect)
    {
        var controller = CascadeController.CreateDefault(30);
        controller.Preload(40); controller.Update(25, 3);
        var before = controller.CaptureStateJson();
        var corrupt = JsonNode.Parse(before)!.AsObject();
        if (defect == "allocation") corrupt["Allocation"]!["Windows"]![0]!["Min"] = -1;
        if (defect == "pid") corrupt["Pid"]!["Integral"] = 1e9;
        if (defect == "missing") corrupt["Pid"]!.AsObject().Remove("MeasurementHistory");
        Assert.ThrowsAny<Exception>(() => controller.RestoreStateJson(corrupt.ToJsonString()));
        Assert.Equal(before, controller.CaptureStateJson());
    }

    [Fact]
    public void RestoreFreezesHistoryAndResumeDoesNotCreateDerivativeKick()
    {
        var original = new CascadeTwoLoopPidController(new CascadeTuning(), 30);
        original.Preload(40); original.Update(25, 3); original.Update(24, 3);
        var state = original.CaptureState();
        var restored = new CascadeTwoLoopPidController(new CascadeTuning(), 50);
        restored.RestoreState(state);
        state.MeasurementHistory[0] = 999;
        Assert.NotEqual(999, restored.CaptureState().MeasurementHistory[0]);
        var effort = restored.Output;
        restored.ResumeFromSuspension(15);
        Assert.Equal(effort, restored.Output);
        var terms = restored.Update(15, 3);
        Assert.Equal(0, terms.MeasurementRate);
        Assert.Equal(0, terms.Derivative);
    }
}
