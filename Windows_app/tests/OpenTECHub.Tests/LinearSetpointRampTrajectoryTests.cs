using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class LinearSetpointRampTrajectoryTests
{
    private static double Quantize(SetpointVariable variable, double value) => Math.Round(value,
        variable == SetpointVariable.Agitation ? 0 : 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void IndependentDurationsReachExactQuantizedTargetsAndKeepOxygenDestination()
    {
        var definition = new LinearSetpointRampDefinition { Lines = [
            new() { Variable = SetpointVariable.Agitation, FinalSetpoint = 451.4, EndAfterSeconds = 10 },
            new() { Variable = SetpointVariable.Flow, FinalSetpoint = 1.234, EndAfterSeconds = 20 },
            new() { Variable = SetpointVariable.Oxygen, FinalSetpoint = 40, EndAfterSeconds = 5,
                OxygenTarget = RampOxygenTarget.ActiveCascadeReference }] };
        var starts = new Dictionary<SetpointVariable, double> { [SetpointVariable.Agitation] = 300,
            [SetpointVariable.Flow] = 3, [SetpointVariable.Oxygen] = 40 };
        var trajectory = new LinearSetpointRampTrajectory(definition, starts, Quantize);
        starts[SetpointVariable.Agitation] = 900;
        Assert.Equal(20, trajectory.DurationSeconds);
        var halfway = trajectory.Sample(5);
        Assert.Equal(376, halfway[0].Reference);
        Assert.False(halfway[0].AtFinalTarget);
        Assert.Equal(2.56, halfway[1].Reference);
        Assert.Equal(RampOxygenTarget.ActiveCascadeReference, halfway[2].OxygenTarget);
        Assert.True(halfway[2].AtFinalTarget);
        var final = trajectory.Sample(1000);
        Assert.Equal(new double[] { 451, 1.23, 40 }, final.Select(line => line.Reference));
        Assert.All(final, line => Assert.True(line.AtFinalTarget));
        Assert.Equal(300, trajectory.Sample(0)[0].Reference);
    }

    [Fact]
    public void MissingReferencesOffCrossingsInvalidQuantizationAndTimeAreRejected()
    {
        var definition = new LinearSetpointRampDefinition { Lines = [
            new() { Variable = SetpointVariable.Agitation, FinalSetpoint = 300, EndAfterSeconds = 10 }] };
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampTrajectory(definition,
            new Dictionary<SetpointVariable, double>(), Quantize));
        var starts = new Dictionary<SetpointVariable, double> { [SetpointVariable.Agitation] = 0 };
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampTrajectory(definition, starts, Quantize));
        starts[SetpointVariable.Agitation] = 100;
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampTrajectory(definition, starts, (_, _) => double.NaN));
        var trajectory = new LinearSetpointRampTrajectory(definition, starts, Quantize);
        foreach (var value in new[] { -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => trajectory.Sample(value));
    }
}
