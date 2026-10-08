using OpenTECHub.Protocol;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampResourceProducerTests
{
    [Fact]
    public async Task AssayWaitsForDispatchAndKeepsActiveTimeFrozenUntilReturn()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Agitation], clock);
        time.Advance(TimeSpan.FromSeconds(4));
        using var step = producer.TryEnterStep();
        var pending = producer.SuspendAsync(default);
        Assert.False(pending.IsCompleted);
        Assert.Null(producer.TryEnterStep());
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(4, clock.ActiveSeconds);
        step!.Dispose();
        var receipt = await pending;
        clock.Suspend("recipe");
        receipt.Resume();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(4, clock.ActiveSeconds);
        clock.Resume("recipe");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, clock.ActiveSeconds);
        var second = await producer.SuspendAsync(default);
        Assert.Throws<InvalidOperationException>(() => receipt.Resume());
        Assert.Null(producer.TryEnterStep());
        second.Stop();
        second.Resume();
        Assert.True(clock.IsSuspended);
        Assert.Null(producer.TryEnterStep());
    }

    [Fact]
    public async Task CancelledReservationUnfreezesClockWithoutRemovingRecipePause()
    {
        var clock = new RecipeRampActiveClock(new TestClock(DateTimeOffset.UnixEpoch));
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Aeration], clock);
        using var step = producer.TryEnterStep();
        using var cancellation = new CancellationTokenSource();
        var pending = producer.SuspendAsync(cancellation.Token);
        clock.Suspend("recipe");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(clock.IsSuspended);
        clock.Resume("recipe");
        Assert.False(clock.IsSuspended);
        using var next = producer.TryEnterStep();
        Assert.NotNull(next);
    }
}
