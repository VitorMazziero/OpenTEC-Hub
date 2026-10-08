using System.Collections.Immutable;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampInitialStateTests
{
    [Fact]
    public async Task CapturesAllDirectReferencesWithTransportEvidenceInsteadOfProcessMeasurements()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, time);
        arbiter.Claim(CommandOwner.Recipe, CommandActuators.All, "recipe");
        var command = OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 30)
            .Merge(CommandBuilders.MotorSetpoint(300)).Merge(CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, GasRigConfiguration.Default))
            .Set(CommandKeys.PressureReference, 100).Set(CommandKeys.PHSetpoint, 6.8);
        arbiter.Dispatch(CommandOwner.Recipe, command);
        device.PushTelemetry(new() { Temperature = 50, ServoRpm = 900, OxygenCalibrated = 5, FlowRate = 9 });
        var expected = new Dictionary<SetpointVariable, double> { [SetpointVariable.Temperature] = 30,
            [SetpointVariable.Agitation] = 300, [SetpointVariable.Flow] = 2,
            [SetpointVariable.Pressure] = 100, [SetpointVariable.Ph] = 6.8 };
        var definition = new LinearSetpointRampDefinition { Lines = expected.Select(pair => new LinearSetpointRampLine
        { Variable = pair.Key, FinalSetpoint = pair.Value, EndAfterSeconds = 60 }).ToImmutableArray() };
        var configuration = new RecipeRampBlockConfiguration(definition, null);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp",
            RecipeRampInitialState.ResourcesFor(definition), TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => RecipeRampInitialState.Capture(configuration, arbiter, authority, time));
        await arbiter.DrainReservedCommandsAsync(authority);
        var state = RecipeRampInitialState.Capture(configuration, arbiter, authority, time);
        foreach (var pair in expected) Assert.Equal(pair.Value, state.ConfirmedStarts[pair.Key]);
        Assert.All(state.References, reference => Assert.Equal(RampReferenceEvidence.TransportAccepted, reference.Evidence));
        Assert.Equal(5, state.Commands.Length);
        var restored = JsonSerializer.Deserialize<RecipeRampInitialState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(state.SnapshotId, restored.SnapshotId);
        Assert.Equal(state.References.ToArray(), restored.References.ToArray());
        arbiter.ReleaseReservation(authority);
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(400));
        Assert.Equal(300, state.ConfirmedStarts[SetpointVariable.Agitation]);
        Assert.Throws<InvalidOperationException>(() => RecipeRampInitialState.Capture(configuration, arbiter, authority, time));
    }

    [Fact]
    public async Task ExplicitStartDoesNotInventMissingPriorReferenceButRestorePolicyRequiresIt()
    {
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature, ActuatorId.PHDosing], "recipe");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 30));
        var definition = new LinearSetpointRampDefinition { Lines = [
            new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 35, EndAfterSeconds = 60 },
            new() { Variable = SetpointVariable.Ph, StartSource = SetpointStartSource.Explicit,
                InitialSetpoint = 6.5, FinalSetpoint = 6.8, EndAfterSeconds = 120 }] };
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp",
            RecipeRampInitialState.ResourcesFor(definition), TimeSpan.FromSeconds(5));
        await arbiter.DrainReservedCommandsAsync(authority);
        var configuration = new RecipeRampBlockConfiguration(definition, null);
        var state = RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System);
        Assert.Single(state.References);
        Assert.False(state.ConfirmedStarts.ContainsKey(SetpointVariable.Ph));
        var trajectory = new LinearSetpointRampTrajectory(definition, state.ConfirmedStarts, (_, value) => value);
        Assert.Equal(6.5, trajectory.Sample(0).Single(sample => sample.Variable == SetpointVariable.Ph).Reference);
        Assert.Throws<InvalidOperationException>(() => RecipeRampInitialState.Capture(configuration with
        { Definition = definition with { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot } }, arbiter, authority, TimeProvider.System));
        arbiter.ReleaseReservation(authority);
    }

    [Fact]
    public async Task DelayedAcceptanceOfOlderMotorCommandCannotBecomeTheStartingReference()
    {
        var device = new RecordingDeviceService { RaiseCommandSentOnSend = false };
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation], "recipe");
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(400));
        device.PushCommandSent(CommandBuilders.MotorSetpoint(300).ToJson());
        var definition = new LinearSetpointRampDefinition { Lines = [new()
        { Variable = SetpointVariable.Agitation, FinalSetpoint = 500, EndAfterSeconds = 60 }] };
        var configuration = new RecipeRampBlockConfiguration(definition, null);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp",
            [ActuatorId.Agitation], TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<System.IO.IOException>(() => arbiter.DrainReservedCommandsAsync(authority));
        Assert.Throws<InvalidOperationException>(() => RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System));
        device.PushCommandSent(CommandBuilders.MotorSetpoint(400).ToJson());
        device.RaiseCommandSentOnSend = true;
        await arbiter.DrainReservedCommandsAsync(authority);
        Assert.Equal(400, RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System)
            .ConfirmedStarts[SetpointVariable.Agitation]);
        Assert.Throws<ArgumentException>(() => arbiter.CaptureReservedDesiredState(authority, [ActuatorId.Temperature]));
        arbiter.ReleaseReservation(authority);
    }
}
