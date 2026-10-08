using System.Text.Json;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeResourceCoordinator
{
    /// <summary>Serialize preparation with assays; register the producer only after a durable,
    /// quiescent capture. Does not execute the ramp or transfer authority to an assay.</summary>
    public async Task<RecipeRampStartCheckpoint> PrepareRampAsync(Guid executionId, Guid invocationId,
        RecipeRampBlockConfiguration configuration, RecipeRampResourceProducer producer,
        Func<CommandAuthorityLease, RecipeRampStartCheckpoint> capture, RecipeRampCheckpointStore store,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(store);
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        if (executionId == Guid.Empty || invocationId == Guid.Empty ||
            !resources.Order().SequenceEqual(producer.Resources.Order()) ||
            timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Preparação da rampa exige identidade, recursos correspondentes e prazo finito.");
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token, producer.StopToken);
        var reason = $"preparation:{Guid.NewGuid():N}";
        producer.ActiveClock.Suspend(reason);
        var entered = false;
        var registered = false;
        var drained = false;
        CommandAuthorityLease? authority = null;
        IRecipeResourceSuspension? cascadePause = null;
        try
        {
            await _assay.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            IRecipeResourceProducer? cascade;
            lock (_sync)
            {
                if (_producers.ContainsKey(producer.NodeId)) throw new InvalidOperationException("Produtor da rampa já registrado.");
                var conflicting = _producers.Values.Where(item => item.Resources.Intersect(resources).Any()).ToArray();
                if (conflicting.Any(item => item.NodeId != configuration.CascadeNodeId))
                    throw new InvalidOperationException("Destinos da rampa já usados por outro produtor.");
                cascade = conflicting.SingleOrDefault();
                if (configuration.CascadeNodeId is not null && cascade is null)
                    throw new InvalidOperationException("Controle de O₂ associado ainda não está ativo.");
            }
            if (cascade is not null) cascadePause = await cascade.SuspendAsync(linked.Token).ConfigureAwait(false);
            var controller = cascadePause?.CaptureControllerState();
            authority = await arbiter.ReserveAsync(CommandOwner.Recipe, executionId, producer.NodeId,
                resources, timeout, linked.Token).ConfigureAwait(false);
            await arbiter.DrainReservedCommandsAsync(authority, linked.Token).ConfigureAwait(false);
            drained = true;
            var checkpoint = capture(authority);
            if (checkpoint.InvocationId != invocationId || checkpoint.InitialState.ExecutionId != executionId ||
                checkpoint.InitialState.NodeId != producer.NodeId || checkpoint.InitialState.Controller != controller ||
                JsonSerializer.Serialize(checkpoint.Configuration with { TemperatureRoute = configuration.TemperatureRoute }) !=
                JsonSerializer.Serialize(configuration))
                throw new InvalidOperationException("Captura da rampa diverge da preparação reservada.");
            var persisted = await store.PersistStartAsync(checkpoint, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!arbiter.IsCurrent(authority) || cascadePause is { CanResume: false })
                throw new InvalidOperationException("Autoridade ou controle mudou durante a gravação inicial.");
            lock (_sync)
            {
                if (_producers.Values.Any(item => item.Resources.Intersect(resources).Any() && !ReferenceEquals(item, cascade)))
                    throw new InvalidOperationException("Outro produtor entrou durante a preparação da rampa.");
                _producers.Add(producer.NodeId, producer);
                registered = true;
            }
            arbiter.ReleaseReservation(authority);
            authority = null;
            cascadePause?.Resume();
            cascadePause = null;
            return persisted;
        }
        catch (Exception error)
        {
            if (registered)
                lock (_sync)
                    if (_producers.TryGetValue(producer.NodeId, out var current) && ReferenceEquals(current, producer))
                        _producers.Remove(producer.NodeId);
            var safe = (authority is null || drained && arbiter.IsCurrent(authority)) &&
                resources.All(resource => arbiter.OwnerOf(resource) == CommandOwner.Recipe) &&
                cascadePause is not { CanResume: false };
            if (authority is not null && drained && arbiter.IsCurrent(authority)) arbiter.ReleaseReservation(authority);
            if (cascadePause is not null)
            {
                if (safe) cascadePause.Resume(); else cascadePause.Stop();
            }
            producer.Dispose();
            if (error is OperationCanceledException && deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
                throw new TimeoutException("Prazo de preparação da rampa esgotado.", error);
            throw;
        }
        finally
        {
            producer.ActiveClock.Resume(reason);
            if (entered) _assay.Release();
        }
    }
}
