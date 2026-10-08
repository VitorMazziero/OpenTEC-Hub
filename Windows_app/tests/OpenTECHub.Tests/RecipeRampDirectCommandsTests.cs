using OpenTECHub.Protocol;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampDirectCommandsTests
{
    [Fact]
    public void DirectReferencesReuseWireBuildersAndKeepCascadeDestinationSeparate()
    {
        var commands = new RecipeRampDirectCommands(10, false, GasRigConfiguration.Default, .1);
        Assert.Equal(CommandBuilders.MotorSetpoint(301).ToJson(), commands.Build(new(SetpointVariable.Agitation, null, 300.6, false)).ToJson());
        Assert.Equal(CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, GasRigConfiguration.Default).ToJson(),
            commands.Build(new(SetpointVariable.Flow, null, 2, false)).ToJson());
        Assert.Equal(CommandBuilders.FlowRoute(0, 10, GasRoute.Closed, GasRigConfiguration.Default).ToJson(),
            commands.Build(new(SetpointVariable.Flow, null, 0, true)).ToJson());
        Assert.True(commands.Build(new(SetpointVariable.Temperature, null, 30.123, true)).Contains(CommandKeys.TempSetpoint));
        Assert.True(commands.Build(new(SetpointVariable.Ph, null, 6.8, true)).Contains(CommandKeys.PHError));
        Assert.True(commands.Build(new(SetpointVariable.Pressure, null, 10, true)).Contains(CommandKeys.PressureReference));
        Assert.Throws<ArgumentException>(() => commands.Build(new(SetpointVariable.Oxygen, RampOxygenTarget.MonitorReference, 30, true)));
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampLine { Variable = SetpointVariable.Oxygen,
            OxygenTarget = RampOxygenTarget.MonitorReference, FinalSetpoint = 30, EndAfterSeconds = 60 }.Validate());
        Assert.Throws<ArgumentException>(() => commands.Build(new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 30, true)));
    }

    [Fact]
    public void TrajectoryCannotSilentlyClipToFlowOrFallbackLimits()
    {
        var commands = new RecipeRampDirectCommands(5, true, GasRigConfiguration.Default, .1);
        Assert.Throws<ArgumentException>(() => commands.Quantize(SetpointVariable.Flow, 5.1));
        Assert.Throws<ArgumentException>(() => commands.Quantize(SetpointVariable.Agitation, 966));
        Assert.Equal(965, commands.Quantize(SetpointVariable.Agitation, 964.6));
        Assert.Throws<ArgumentException>(() => commands.Quantize(SetpointVariable.Temperature, double.NaN));
        var definition = new LinearSetpointRampDefinition { Lines = [new() { Variable = SetpointVariable.Flow,
            StartSource = SetpointStartSource.Explicit, InitialSetpoint = 1, FinalSetpoint = 6, EndAfterSeconds = 30 }] };
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampTrajectory(definition,
            new Dictionary<SetpointVariable, double>(), commands.Quantize));
        // Saved maximum flow defaults to 50. The trajectory envelope remains 25 without
        // blocking unrelated parameters or changing the configured route's maximum.
        var defaultLimits = new RecipeRampDirectCommands(50, false, GasRigConfiguration.Default, .1);
        Assert.Equal(30, defaultLimits.Quantize(SetpointVariable.Temperature, 30));
        Assert.Equal(25, defaultLimits.Quantize(SetpointVariable.Flow, 25));
        Assert.Throws<ArgumentException>(() => defaultLimits.Quantize(SetpointVariable.Flow, 26));
    }
}
