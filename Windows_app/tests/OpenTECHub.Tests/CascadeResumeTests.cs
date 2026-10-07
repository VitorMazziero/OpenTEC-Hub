using OpenTECHub.Services.Control;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CascadeResumeTests
{
    [Fact]
    public void Resume_rebases_probe_history_while_preserving_effort_integral_tuning_and_allocation()
    {
        var controller = CascadeController.CreateDefault();
        controller.Preload(40);
        controller.Update(25, 3);
        controller.Update(24, 3);
        var before = controller.LastTerms;
        var tuning = controller.Tuning; var allocation = controller.Allocation;
        controller.ResumeFromSuspension(15);
        Assert.Equal(before.Output, controller.Effort);
        Assert.Equal(before.Integral, controller.LastTerms.Integral);
        Assert.Equal(0, controller.LastTerms.MeasurementRate);
        Assert.Equal(0, controller.LastTerms.Derivative);
        Assert.Equal(0, controller.LastTerms.DeltaOutput);
        Assert.Same(tuning, controller.Tuning); Assert.Same(allocation, controller.Allocation);
        var next = controller.Update(15, 3);
        Assert.Equal(0, next.Terms.Derivative);
        Assert.Equal(0, next.Terms.MeasurementRate); // no slope through the assay's measurement interval
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ResumeFromSuspension(double.NaN));
    }
}
