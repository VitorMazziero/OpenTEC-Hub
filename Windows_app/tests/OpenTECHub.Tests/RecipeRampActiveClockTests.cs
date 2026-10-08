using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampActiveClockTests
{
    [Fact]
    public void RecipePauseAndAssaySuspensionOverlapWithoutJumpOrPrematureResume()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var clock = new RecipeRampActiveClock(time);
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Suspend("recipe");
        clock.Suspend("recipe");
        time.Advance(TimeSpan.FromSeconds(20));
        clock.Suspend("assay");
        clock.Resume("recipe");
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(clock.IsSuspended);
        Assert.Equal(5, clock.ActiveSeconds);
        clock.Resume("unknown");
        clock.Resume("assay");
        clock.Resume("assay");
        Assert.False(clock.IsSuspended);
        Assert.Equal(5, clock.ActiveSeconds);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(7, clock.ActiveSeconds);
        clock.Suspend("recipe");
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(7, clock.ActiveSeconds);
        clock.Resume("recipe");
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(10, clock.ActiveSeconds);
    }

    [Fact]
    public void RampReferenceStaysAtThePreservedPointDuringLongPause()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var clock = new RecipeRampActiveClock(time);
        var trajectory = new LinearSetpointRampTrajectory(new() { Lines = [
            new() { Variable = SetpointVariable.Oxygen, StartSource = SetpointStartSource.Explicit,
                InitialSetpoint = 20, FinalSetpoint = 40, EndAfterSeconds = 10,
                OxygenTarget = RampOxygenTarget.ActiveCascadeReference }] },
            new Dictionary<SetpointVariable, double>(), (_, value) => value);
        time.Advance(TimeSpan.FromSeconds(4));
        clock.Suspend("assay");
        time.Advance(TimeSpan.FromHours(4));
        Assert.Equal(28, trajectory.Sample(clock.ActiveSeconds)[0].Reference);
        clock.Resume("assay");
        Assert.Equal(28, trajectory.Sample(clock.ActiveSeconds)[0].Reference);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(trajectory.Sample(clock.ActiveSeconds)[0].AtFinalTarget);
    }

    [Fact]
    public void CivilClockChangesNeitherAdvanceNorRewindTheRamp()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var clock = new RecipeRampActiveClock(time);
        var trajectory = new LinearSetpointRampTrajectory(new() { Lines = [
            new() { Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit,
                InitialSetpoint = 30, FinalSetpoint = 37, EndAfterSeconds = 3600 }] },
            new Dictionary<SetpointVariable, double>(), (_, value) => value);
        time.Advance(TimeSpan.FromMinutes(20));
        var before = trajectory.Sample(clock.ActiveSeconds)[0].Reference;
        time.ShiftUtc(TimeSpan.FromHours(1));
        Assert.Equal(before, trajectory.Sample(clock.ActiveSeconds)[0].Reference);
        time.ShiftUtc(TimeSpan.FromHours(-3));
        Assert.Equal(before, trajectory.Sample(clock.ActiveSeconds)[0].Reference);
        Assert.Equal(1200, clock.ActiveSeconds);
        time.Advance(TimeSpan.FromMinutes(40));
        Assert.True(trajectory.Sample(clock.ActiveSeconds)[0].AtFinalTarget);
    }
}
