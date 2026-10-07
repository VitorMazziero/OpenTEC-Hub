namespace OpenTECHub.Services.Recipes;

public enum RecipePeriodicSlotState { Started, Completed, Skipped, Cancelled, Failed }
public sealed record RecipePeriodicSlotRecord(PeriodicBlockInvocation Invocation, RecipePeriodicSlotState State,
    double ElapsedSeconds, string? Reason = null);

/// <summary>One monotonic cadence. Target completion includes recovery; missed slots never run later.</summary>
public sealed class RecipePeriodicExecutor(TimeProvider time)
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _paused;
    private CancellationTokenSource? _active;
    private int _started;
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            if (paused) _active?.Cancel();
            var signal = _changed; _changed = NewSignal(); signal.TrySetResult();
        }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(PeriodicBlockInvocation identity,
        Func<PeriodicBlockInvocation, CancellationToken, Task> execute,
        Func<RecipePeriodicSlotRecord, Task> record,
        CancellationToken cancellation, TimeSpan dispatchTolerance, Func<bool>? canDispatch = null)
    {
        identity.Validate();
        ArgumentNullException.ThrowIfNull(execute); ArgumentNullException.ThrowIfNull(record);
        if (identity.SlotIndex != 0 || dispatchTolerance < TimeSpan.Zero ||
            dispatchTolerance.TotalSeconds >= identity.Schedule.PeriodSeconds)
            throw new ArgumentException("Agenda exige slot inicial zero e tolerância menor que o período.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Agenda já utilizada.");
        var began = time.GetTimestamp();
        double Elapsed() => time.GetElapsedTime(began).TotalSeconds;
        long slot = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var invocation = identity with { SlotIndex = slot };
            var due = identity.Schedule.DueAfterSeconds(slot);
            bool paused; Task changed;
            lock (_gate) { paused = _paused; changed = _changed.Task; }
            var remaining = due - Elapsed();
            if (remaining > 0)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var delay = Task.Delay(TimeSpan.FromSeconds(remaining), time, wait.Token);
                await Task.WhenAny(delay, changed).ConfigureAwait(false);
                wait.Cancel();
                try { await delay.ConfigureAwait(false); } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                continue;
            }
            // Re-read pause at dispatch: a wake-up must not use the previous pause state.
            lock (_gate) paused = _paused;
            var ready = canDispatch?.Invoke() ?? true;
            if (paused || !ready || -remaining > dispatchTolerance.TotalSeconds)
            {
                await record(new(invocation, RecipePeriodicSlotState.Skipped, Elapsed(),
                    paused ? "receita pausada" : !ready ? "cascata coordenada ainda não está ativa" :
                    "slot vencido durante indisponibilidade")).ConfigureAwait(false);
                slot = checked(slot + 1);
                continue;
            }
            await record(new(invocation, RecipePeriodicSlotState.Started, Elapsed())).ConfigureAwait(false);
            using var target = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (_gate) { _active = target; if (_paused) target.Cancel(); }
            try
            {
                target.Token.ThrowIfCancellationRequested();
                await execute(invocation, target.Token).ConfigureAwait(false);
                target.Token.ThrowIfCancellationRequested();
                await record(new(invocation, RecipePeriodicSlotState.Completed, Elapsed())).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (target.IsCancellationRequested)
            {
                await record(new(invocation, RecipePeriodicSlotState.Cancelled, Elapsed())).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                await record(new(invocation, RecipePeriodicSlotState.Failed, Elapsed(), error.Message)).ConfigureAwait(false);
                throw;
            }
            finally { lock (_gate) _active = null; }
            // Slots crossed by the target, including recovery, are unavailable even at the exact boundary.
            slot = checked(slot + 1);
            var future = identity.Schedule.FirstFutureSlot(Elapsed());
            while (slot < future)
            {
                cancellation.ThrowIfCancellationRequested();
                await record(new(identity with { SlotIndex = slot }, RecipePeriodicSlotState.Skipped,
                    Elapsed(), "alvo ainda em execução ou recuperação")).ConfigureAwait(false);
                slot = checked(slot + 1);
            }
        }
    }
}
