using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

/// <summary>One invocation: durable preparation, guarded trajectory, then durable completion or return.
/// The graph must inspect terminal status before advancing. No persisted invocation is resumed.</summary>
public sealed class RecipeRampBlockRunner(RecipeEngine engine, ICommandAuthorityArbiter arbiter,
    RecipeRampCheckpointStore store, Func<RecipeRampBlockConfiguration, RecipeRampFrameDestination> createDestination)
{
    private int _started;

    public async Task<RecipeRampTerminalCheckpoint> ExecuteAsync(string nodeId, RecipeRampBlockConfiguration configuration,
        TimeSpan preparationTimeout, TimeSpan confirmationTimeout, TimeSpan recoveryTimeout,
        TimeSpan minimumDispatchInterval, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Definition.Validate();
        foreach (var interval in new[] { preparationTimeout, confirmationTimeout, recoveryTimeout, minimumDispatchInterval })
            if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(preparationTimeout), "Prazos devem ser positivos e finitos.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Invocação da rampa já executada.");
        var coordinator = engine.Resources ?? throw new InvalidOperationException("Rampa exige coordenação de recursos.");
        var execution = engine.ExecutionId;
        if (execution == Guid.Empty || engine.State != RecipeRunState.Running)
            throw new InvalidOperationException("Rampa exige receita em execução.");
        var time = engine.RampTimeProvider;
        var invocation = Guid.NewGuid();
        var activeClock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer(nodeId,
            RecipeRampInitialState.ResourcesFor(configuration.Definition), activeClock);
        var pauseGate = new object();
        var detached = false;
        OwnershipTransfer? revocation = null;
        using var revoked = new CancellationTokenSource();
        void ObserveRevocation(OwnershipTransfer transfer)
        {
            if (transfer.To != CommandOwner.Manual || !transfer.Actuators.Intersect(producer.Resources).Any()) return;
            lock (pauseGate)
            {
                if (detached) return;
                revocation = transfer;
                revoked.Cancel();
            }
        }
        void ObserveState()
        {
            lock (pauseGate)
            {
                if (detached) return;
                if (engine.State == RecipeRunState.Paused) activeClock.Suspend("recipe");
                else activeClock.Resume("recipe");
            }
        }
        engine.StateChanged += ObserveState;
        arbiter.OwnershipRevoked += ObserveRevocation;
        ObserveState();
        RecipeRampStartCheckpoint? start = null;
        RecipeRampFrameDestination? destination = null;
        try
        {
            start = await coordinator.PrepareRampAsync(execution, invocation, configuration, producer,
                authority => engine.CaptureRampStartCheckpoint(configuration, authority, invocation),
                store, preparationTimeout, cancellation).ConfigureAwait(false);
            destination = createDestination(start.Configuration);
            var guarded = new RecipeRampGuardedDestination(producer, activeClock, destination, time,
                confirmationTimeout, arbiter, execution, preparationTimeout);
            using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellation, producer.StopToken, revoked.Token);
            return await new LinearSetpointRampExecutor(time).ExecutePersistedAsync(start, store, activeClock,
                guarded, destination, (variable, value) => RecipeRampReferenceQuantization.Quantize(variable, value,
                    start.Configuration.TemperatureRoute), minimumDispatchInterval, running.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (start is not null && destination is not null)
        {
            activeClock.Suspend("ending");
            OwnershipTransfer? lost;
            lock (pauseGate) lost = revocation;
            if (lost is not null)
            {
                var interrupted = new RecipeRampTerminalCheckpoint(1, execution, start.InvocationId,
                    start.InitialState.SnapshotId, nodeId,
                    lost.IsSafeAbort ? RecipeRampTerminalStatus.EmergencyStopped : RecipeRampTerminalStatus.Faulted,
                    lost.IsSafeAbort ? RecipeRampReturnOutcome.SuppressedForEmergency : RecipeRampReturnOutcome.Failed,
                    activeClock.ActiveSeconds, time.GetUtcNow(), lost.Reason, []);
                return await store.PersistTerminalAsync(interrupted, CancellationToken.None).ConfigureAwait(false);
            }
            var status = error is OperationCanceledException ? RecipeRampTerminalStatus.Cancelled : RecipeRampTerminalStatus.Faulted;
            try
            {
                return start.Configuration.Definition.CancellationPolicy == RampCancellationPolicy.RestoreSnapshot
                    ? await coordinator.RecoverRampAsync(start, producer, store, destination, status, error.Message,
                        recoveryTimeout, CancellationToken.None).ConfigureAwait(false)
                    : await coordinator.HoldInterruptedRampAsync(start, producer, store, status, error.Message,
                        recoveryTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception recovery) { throw new AggregateException("Rampa encerrada sem retorno confirmado.", error, recovery); }
        }
        finally
        {
            lock (pauseGate) detached = true;
            engine.StateChanged -= ObserveState;
            arbiter.OwnershipRevoked -= ObserveRevocation;
            destination?.Dispose();
            if (start is not null) coordinator.Unregister(nodeId);
        }
    }
}
