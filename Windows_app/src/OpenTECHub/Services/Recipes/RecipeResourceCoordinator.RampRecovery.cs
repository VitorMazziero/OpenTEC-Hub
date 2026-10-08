using OpenTECHub.Services.Communication;
using System.Text.Json;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeResourceCoordinator
{
    /// <summary>Keep producers quiescent until a confirmed return has a durable terminal receipt.
    /// Cancellation here is a recovery deadline, independent of the cancelled ramp execution.</summary>
    public async Task<RecipeRampTerminalCheckpoint> RecoverRampAsync(RecipeRampStartCheckpoint start,
        RecipeRampResourceProducer producer, RecipeRampCheckpointStore store,
        Func<CommandAuthorityLease, CancellationToken, Task<RecipeRampTerminalCheckpoint>> restore,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(start); ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(restore);
        var persistedStart = store.ReadStart(start.InitialState.ExecutionId, start.InvocationId)
            ?? throw new InvalidOperationException("Recuperação sem captura inicial durável.");
        if (JsonSerializer.Serialize(start) != JsonSerializer.Serialize(persistedStart))
            throw new InvalidOperationException("Captura de recuperação diverge do registro durável.");
        start = persistedStart;
        var resources = RecipeRampInitialState.ResourcesFor(start.Configuration.Definition);
        if (start.InitialState.NodeId != producer.NodeId ||
            !resources.Order().SequenceEqual(producer.Resources.Order()) ||
            start.Configuration.Definition.CancellationPolicy != RampCancellationPolicy.RestoreSnapshot ||
            timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Recuperação exige produtor correspondente, política de retorno e prazo finito.");
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var entered = false;
        CommandAuthorityLease? authority = null;
        IRecipeResourceSuspension? rampPause = null, cascadePause = null;
        try
        {
            await _assay.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            IRecipeResourceProducer? cascade;
            lock (_sync)
            {
                if (!_producers.TryGetValue(producer.NodeId, out var registered) || !ReferenceEquals(registered, producer))
                    throw new InvalidOperationException("Produtor da rampa não está registrado.");
                var overlaps = _producers.Values.Where(item => !ReferenceEquals(item, producer) && item.Resources.Intersect(resources).Any()).ToArray();
                if (overlaps.Any(item => item.NodeId != start.Configuration.CascadeNodeId))
                    throw new InvalidOperationException("Outro produtor ocupa os destinos da recuperação.");
                cascade = overlaps.SingleOrDefault();
                if (start.Configuration.CascadeNodeId is not null && cascade is null)
                    throw new InvalidOperationException("Cascata associada encerrou antes do retorno.");
            }
            rampPause = await producer.SuspendAsync(linked.Token).ConfigureAwait(false);
            if (cascade is not null) cascadePause = await cascade.SuspendAsync(linked.Token).ConfigureAwait(false);
            authority = await arbiter.ReserveAsync(CommandOwner.Recipe, start.InitialState.ExecutionId,
                producer.NodeId, resources, timeout, linked.Token).ConfigureAwait(false);
            await arbiter.DrainReservedCommandsAsync(authority, linked.Token).ConfigureAwait(false);
            var terminal = await restore(authority, linked.Token).ConfigureAwait(false);
            if (terminal.ExecutionId != start.InitialState.ExecutionId || terminal.InvocationId != start.InvocationId ||
                terminal.SnapshotId != start.InitialState.SnapshotId || terminal.NodeId != producer.NodeId ||
                terminal.Status is not (RecipeRampTerminalStatus.Cancelled or RecipeRampTerminalStatus.Faulted) || !terminal.HasVerifiedRecovery)
                throw new InvalidOperationException("Retorno sem evidência terminal correspondente.");
            var durable = await store.PersistTerminalAsync(terminal, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!arbiter.IsCurrent(authority) || cascadePause is { CanResume: false } ||
                resources.Any(resource => arbiter.OwnerOf(resource) != CommandOwner.Recipe))
                throw new InvalidOperationException("Autoridade ou cascata mudou durante a recuperação.");
            await arbiter.DrainReservedCommandsAsync(authority, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!arbiter.IsCurrent(authority) || cascadePause is { CanResume: false } ||
                resources.Any(resource => arbiter.OwnerOf(resource) != CommandOwner.Recipe))
                throw new InvalidOperationException("Autoridade ou cascata mudou durante a drenagem final.");
            arbiter.ReleaseReservation(authority);
            authority = null;
            rampPause.Stop();
            cascadePause?.Resume();
            cascadePause = null;
            lock (_sync)
                if (_producers.TryGetValue(producer.NodeId, out var current) && ReferenceEquals(current, producer))
                    _producers.Remove(producer.NodeId);
            return durable;
        }
        catch
        {
            // An incomplete return must not reopen production or release its unresolved reservation.
            rampPause?.Stop(); cascadePause?.Stop();
            throw;
        }
        finally { if (entered) _assay.Release(); }
    }
}
