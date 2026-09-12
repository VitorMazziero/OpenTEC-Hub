using OpenTECHub.Services.Control;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>The settled-flow criterion shared by the power and kLa runners while the air is on C.</summary>
public class FlowSettlingTests
{
    [Theory]
    [InlineData(new[] { 3.08, 3.09, 3.07, 3.08 }, 3.0, 4, 0.05, 0.3, true)]   // steady offset inside the error: settled
    [InlineData(new[] { 3.08, 3.09, 3.07 }, 3.0, 4, 0.05, 0.3, false)]        // one reading short of the window
    [InlineData(new[] { 2.6, 3.4, 2.7, 3.3 }, 3.0, 4, 0.05, 0.3, false)]      // mean on target but still swinging
    [InlineData(new[] { 3.5, 3.5, 3.5, 3.5 }, 3.0, 4, 0.05, 0.3, false)]      // flat but too far off
    [InlineData(new[] { 3.08, 3.09, 3.07, 3.08 }, 3.0, 4, 0.0, 0.3, false)]   // criterion disabled
    public void Settled_means_low_spread_and_a_mean_near_the_target(double[] window, double target, int required, double sd, double err, bool expected)
    {
        Assert.Equal(expected, FlowSettling.HasSettled(window, target, required, sd, err, out _));
    }

    [Fact]
    public void Push_keeps_only_the_last_required_readings()
    {
        var window = new System.Collections.Generic.List<double>();
        for (var i = 0; i < 10; i++)
        {
            FlowSettling.Push(window, i, requiredSamples: 3);
        }
        Assert.Equal([7.0, 8.0, 9.0], window);

        // Never below two, so a standard deviation is always defined.
        window.Clear();
        FlowSettling.Push(window, 1.0, requiredSamples: 1);
        FlowSettling.Push(window, 2.0, requiredSamples: 1);
        FlowSettling.Push(window, 3.0, requiredSamples: 1);
        Assert.Equal([2.0, 3.0], window);
    }
}
