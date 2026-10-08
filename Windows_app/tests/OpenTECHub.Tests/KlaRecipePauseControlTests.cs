using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipePauseControlTests
{
    [Fact]
    public void CancellationCallbackMayDisposeController()
    {
        using var pause = new KlaRecipePauseControl();
        CancellationToken token = default;
        pause.TryDispatch(value => token = value);
        using var callback = token.Register(pause.Dispose);
        pause.Pause();
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(pause.Resume);
    }

    [Fact]
    public async Task ResumeDoesNotReviveDispatchedEpoch()
    {
        using var pause = new KlaRecipePauseControl();
        CancellationToken old = default, next = default;
        Assert.True(pause.TryDispatch(token => old = token));
        pause.Pause();
        var wait = pause.WaitUntilResumedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        Assert.False(pause.TryDispatch(_ => Assert.Fail("Paused dispatch")));
        pause.Resume();
        await wait;
        Assert.True(pause.TryDispatch(token => next = token));
        Assert.True(old.IsCancellationRequested);
        Assert.False(next.IsCancellationRequested);
        Assert.NotEqual(old, next);
    }

    [Fact]
    public async Task WaitingCanBeCancelledWithoutResumingInvocation()
    {
        using var pause = new KlaRecipePauseControl();
        using var stop = new CancellationTokenSource();
        pause.Pause();
        var wait = pause.WaitUntilResumedAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(pause.IsPaused);
    }

    [Fact]
    public async Task PauseWaitsForSynchronousDispatchToLeaveBarrier()
    {
        using var pause = new KlaRecipePauseControl();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CancellationToken dispatched = default;
        var dispatch = Task.Run(() => pause.TryDispatch(token =>
        {
            dispatched = token;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var pausing = Task.Run(pause.Pause);
        Assert.False(dispatched.IsCancellationRequested);
        release.Set();
        Assert.True(await dispatch);
        await pausing;
        Assert.True(dispatched.IsCancellationRequested);
        Assert.False(pause.TryDispatch(_ => Assert.Fail("Dispatch after pause")));
    }

    [Fact]
    public async Task DisposalReleasesWaitAndRejectsFurtherDispatch()
    {
        var pause = new KlaRecipePauseControl();
        pause.Pause();
        var wait = pause.WaitUntilResumedAsync(CancellationToken.None);
        pause.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Throws<ObjectDisposedException>(() => pause.TryDispatch(_ => { }));
        pause.Dispose();
    }
}
