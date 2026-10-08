using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampRestoreCommandsTests
{
    [Fact]
    public async Task RestoresStatefulBathOptionsWithoutReplayingTransientActionsAndRejectsUnacceptedSettings()
    {
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature], "start");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25.12)
            .Set(CommandKeys.TempControlMode, true).Set(CommandKeys.BathMode, "auto")
            .Set(CommandKeys.BathCascadeKp, .3).Set(CommandKeys.BathSync, true)
            .Set(CommandKeys.BathAbort, true).Set(CommandKeys.BathCascadeReset, true));
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot,
            Lines = [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 }] }, null,
            RampTemperatureRoute.ExternalBath);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp", [ActuatorId.Temperature], TimeSpan.FromSeconds(5));
        await arbiter.DrainReservedCommandsAsync(authority);
        var initial = RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), configuration, initial);
        var frame = RecipeRampRestoreCommands.Build(start, authority);
        var command = OpenTECCommand.Parse(frame.CommandJson);
        Assert.Equal(25.12, Assert.Single(frame.References).Reference);
        Assert.Equal("\"auto\"", command.GetRawValue(CommandKeys.BathMode));
        Assert.False(command.Contains(CommandKeys.BathSync)); Assert.False(command.Contains(CommandKeys.BathAbort));
        Assert.False(command.Contains(CommandKeys.BathCascadeReset));
        Assert.Equal("1", command.GetRawValue(CommandKeys.TempSetpointExact));
        var corrupt = start with { InitialState = initial with { Commands = [initial.Commands[0] with {
            TransportAcceptedCommandJson = initial.Commands[0].TransportAcceptedCommandJson.Replace("0.3", "0.4") }] } };
        Assert.Throws<InvalidOperationException>(() => RecipeRampRestoreCommands.Build(corrupt, authority));
        Assert.Throws<InvalidOperationException>(() => RecipeRampRestoreCommands.Build(start, authority with { ExecutionId = Guid.NewGuid() }));
        arbiter.ReleaseReservation(authority);
    }
}
