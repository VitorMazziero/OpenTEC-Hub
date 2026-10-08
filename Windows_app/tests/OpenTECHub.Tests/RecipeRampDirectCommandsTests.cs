using OpenTECHub.Protocol;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampDirectCommandsTests
{
    [Theory]
    [InlineData(RampTemperatureRoute.NativeModule, 37.1)]
    [InlineData(RampTemperatureRoute.ExternalBath, 37.12)]
    public void TemperatureTrajectoryAndWireUseCapturedRoutePrecision(RampTemperatureRoute route, double represented)
    {
        var commands = new RecipeRampDirectCommands(10, false, GasRigConfiguration.Default, .1, route);
        var definition = new LinearSetpointRampDefinition { Lines = [new() {
            Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 30, FinalSetpoint = 37.123, EndAfterSeconds = 60 }] };
        var trajectory = new LinearSetpointRampTrajectory(definition, new Dictionary<SetpointVariable, double>(), commands.Quantize);
        var final = Assert.Single(trajectory.Sample(60));
        Assert.Equal(represented, final.Reference);
        Assert.Equal(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, represented)
            .Set(CommandKeys.TempSetpointExact, true).ToJson(), commands.Build(final).ToJson());
        Assert.Equal(37.123, definition.Lines[0].FinalSetpoint);
        foreach (var seconds in new[] { 0d, 10d, 20d, 30d, 60d })
        {
            var sample = Assert.Single(trajectory.Sample(seconds));
            Assert.Equal(sample.Reference, RecipeRampReferenceQuantization.Quantize(sample.Variable, sample.Reference, route));
        }
    }

    [Fact]
    public void TemperaturePrecisionRequiresKnownRouteAndCannotRoundPositiveReferenceToOff()
    {
        Assert.Equal(30, RecipeRampReferenceQuantization.Quantize(SetpointVariable.Temperature, 30));
        Assert.Throws<ArgumentException>(() => RecipeRampReferenceQuantization.Quantize(SetpointVariable.Temperature, 30.12));
        Assert.Throws<ArgumentException>(() => RecipeRampReferenceQuantization.Quantize(SetpointVariable.Temperature, 30, (RampTemperatureRoute)99));
        Assert.Throws<ArgumentException>(() => RecipeRampReferenceQuantization.Quantize(SetpointVariable.Temperature, .004, RampTemperatureRoute.ExternalBath));
    }

    [Fact]
    public void PhQuantizationCannotSilentlyDisableDosingOrCrossTheOffSentinel()
    {
        var commands = new RecipeRampDirectCommands(10, false, GasRigConfiguration.Default, .1);
        Assert.Throws<ArgumentException>(() => commands.Quantize(SetpointVariable.Ph, .004));
        foreach (var values in new[] { (0d, 6.8), (6.8, 0d) })
        {
            var definition = new LinearSetpointRampDefinition { Lines = [new() { Variable = SetpointVariable.Ph,
                StartSource = SetpointStartSource.Explicit, InitialSetpoint = values.Item1, FinalSetpoint = values.Item2, EndAfterSeconds = 60 }] };
            Assert.Throws<ArgumentException>(() => new LinearSetpointRampTrajectory(definition,
                new Dictionary<SetpointVariable, double>(), commands.Quantize));
        }
        Assert.Equal(0, commands.Quantize(SetpointVariable.Ph, 0));
        Assert.Equal(.01, commands.Quantize(SetpointVariable.Ph, .01));
    }
    [Theory]
    [InlineData(SetpointVariable.Pressure, 100.9, 100)]
    [InlineData(SetpointVariable.Ph, 6.805, 6.81)]
    public void TrajectoryAndWireUseRepresentableReference(SetpointVariable variable, double requested, double represented)
    {
        var commands = new RecipeRampDirectCommands(10, false, GasRigConfiguration.Default, .1);
        var definition = new LinearSetpointRampDefinition { Lines = [new() { Variable = variable,
            StartSource = SetpointStartSource.Explicit, InitialSetpoint = variable == SetpointVariable.Ph ? 6.5 : 90,
            FinalSetpoint = requested, EndAfterSeconds = 60 }] };
        var trajectory = new LinearSetpointRampTrajectory(definition, new Dictionary<SetpointVariable, double>(), commands.Quantize);
        var final = Assert.Single(trajectory.Sample(60));
        Assert.Equal(represented, final.Reference);
        var key = variable == SetpointVariable.Ph ? CommandKeys.PHSetpoint : CommandKeys.PressureReference;
        Assert.Equal(OpenTECCommand.Create().Set(key, represented).Set(CommandKeys.PHError, .1).ToJson(),
            (variable == SetpointVariable.Ph ? commands.Build(final) : commands.Build(final).Set(CommandKeys.PHError, .1)).ToJson());
        foreach (var seconds in new[] { 0d, 10d, 20d, 30d, 60d })
        {
            var sample = Assert.Single(trajectory.Sample(seconds));
            Assert.Equal(sample.Reference, commands.Quantize(variable, sample.Reference));
        }
        Assert.Equal(requested, definition.Lines[0].FinalSetpoint);
    }
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
