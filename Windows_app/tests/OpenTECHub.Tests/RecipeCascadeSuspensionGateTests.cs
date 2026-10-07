using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeCascadeSuspensionGateTests
{
    [Fact]
    public async Task Pause_waits_for_in_flight_step_and_rejects_new_commands()
    {
        var gate = new RecipeCascadeSuspensionGate();
        using var step = gate.TryEnterStep();
        Assert.NotNull(step);
        var pause = gate.PauseAsync();
        Assert.False(pause.IsCompleted);
        Assert.Null(gate.TryEnterStep());
        step.Dispose();
        var receipt = await pause.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(gate.IsPaused);
        var waiting = gate.WaitUntilResumedAsync();
        Assert.False(waiting.IsCompleted);
        receipt.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        using var resumed = gate.TryEnterStep();
        Assert.NotNull(resumed);
        Assert.Equal(1, resumed.ResumeVersion);
    }

    [Fact]
    public async Task Cancelled_pause_does_not_leave_cascade_frozen()
    {
        var gate = new RecipeCascadeSuspensionGate();
        using var step = gate.TryEnterStep();
        using var cts = new CancellationTokenSource();
        var pause = gate.PauseAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause);
        Assert.False(gate.IsPaused);
        using var resumed = gate.TryEnterStep();
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task Stop_blocks_late_resume_and_new_step()
    {
        var gate = new RecipeCascadeSuspensionGate();
        var receipt = await gate.PauseAsync();
        var waiting = gate.WaitUntilResumedAsync();
        gate.Stop();
        receipt.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(gate.TryEnterStep());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.PauseAsync());
    }

    [Fact]
    public async Task Old_receipt_cannot_resume_a_new_pause()
    {
        using var gate = new RecipeCascadeSuspensionGate();
        var first = await gate.PauseAsync(); first.Resume();
        var second = await gate.PauseAsync();
        Assert.Throws<InvalidOperationException>(() => first.Resume());
        Assert.True(gate.IsPaused);
        second.Resume();
        Assert.False(gate.IsPaused);
    }
}
