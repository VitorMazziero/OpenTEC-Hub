using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

public interface IRecipeResourceSuspension
{
    void Resume();
    void Stop();
}

public interface IRecipeResourceProducer
{
    string NodeId { get; }
    IReadOnlyList<ActuatorId> Resources { get; }
    Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct);
}

/// <summary>Coordinates recipe producers before issuing actuator authority for an assay.</summary>
public sealed class RecipeResourceCoordinator(ICommandAuthorityArbiter arbiter, TimeProvider time)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IRecipeResourceProducer> _producers = new();
    // Assays all share N/Q. Keep their complete handshakes serialized; unrelated producers continue.
    private readonly SemaphoreSlim _assay = new(1, 1);

    public void Register(IRecipeResourceProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (string.IsNullOrWhiteSpace(producer.NodeId) || producer.Resources.Count == 0)
            throw new ArgumentException("Produtor sem identidade ou recursos.");
        lock (_sync) _producers.Add(producer.NodeId, producer);
    }

    public void Unregister(string nodeId)
    {
        lock (_sync) _producers.Remove(nodeId);
    }

    public async Task<RecipeAssayResourceLease> ReserveForAssayAsync(RecipeInvocationContext context,
        IReadOnlyList<ActuatorId> resources, TimeSpan timeout, CancellationToken ct = default)
    {
        context.Validate();
        resources = resources.ToArray();
        if (!resources.Contains(ActuatorId.Agitation) || !resources.Contains(ActuatorId.Aeration) || timeout <= TimeSpan.Zero)
            throw new ArgumentException("Ensaio requer N/Q e prazo de cessão positivo.");
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var suspended = new List<IRecipeResourceSuspension>();
        CommandAuthorityLease? authority = null;
        var entered = false;
        try
        {
            await _assay.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            IRecipeResourceProducer[] producers;
            lock (_sync) producers = _producers.Values.Where(p => p.NodeId != context.NodeId &&
                p.Resources.Intersect(resources).Any()).OrderBy(p => p.NodeId, StringComparer.Ordinal).ToArray();
            foreach (var producer in producers)
                suspended.Add(await producer.SuspendAsync(linked.Token).ConfigureAwait(false));
            authority = await arbiter.ReserveAsync(CommandOwner.Recipe, context.RecipeRunId, context.NodeId,
                resources, timeout, linked.Token).ConfigureAwait(false);
            await arbiter.DrainReservedCommandsAsync(authority, linked.Token).ConfigureAwait(false);
            return new RecipeAssayResourceLease(arbiter, authority, suspended, () => _assay.Release());
        }
        catch (Exception error)
        {
            // Failure before any transfer can resume the old producers only while Recipe still owns the resources.
            var canResume = resources.All(a => arbiter.OwnerOf(a) == CommandOwner.Recipe);
            if (authority is not null && arbiter.IsCurrent(authority)) arbiter.ReleaseReservation(authority);
            foreach (var producer in suspended.AsEnumerable().Reverse())
                if (canResume) producer.Resume(); else producer.Stop();
            if (entered) _assay.Release();
            if (error is OperationCanceledException && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException("Prazo da cessão dos atuadores esgotado.", error);
            throw;
        }
    }
}

/// <summary>No disposal path silently restores ownership or resumes a controller.</summary>
public sealed class RecipeAssayResourceLease
{
    private readonly object _sync = new();
    private readonly ICommandAuthorityArbiter _arbiter;
    private readonly IReadOnlyList<IRecipeResourceSuspension> _suspended;
    private readonly Action _releaseAssaySlot;
    private bool _ended;
    private Guid? _snapshotId;
    public CommandAuthorityLease Authority { get; private set; }

    internal RecipeAssayResourceLease(ICommandAuthorityArbiter arbiter, CommandAuthorityLease authority,
        IReadOnlyList<IRecipeResourceSuspension> suspended, Action releaseAssaySlot)
    {
        _arbiter = arbiter; Authority = authority; _suspended = suspended; _releaseAssaySlot = releaseAssaySlot;
    }

    public void BeginAssay(KlaReturnSnapshot snapshot)
    {
        lock (_sync)
        {
        snapshot.Validate();
        if (_ended || _snapshotId.HasValue || Authority.Owner != CommandOwner.Recipe ||
            Authority.Resources.Any(a => !snapshot.Actuators.Any(s => s.Actuator == a &&
                s.Owner == CommandOwner.Recipe && s.OwnerExecutionId == Authority.ExecutionId.ToString())))
            throw new InvalidOperationException("Snapshot não corresponde à reserva da receita.");
        Authority = _arbiter.TransferReserved(Authority, CommandOwner.KlaAssay, "início de kLa após pausa e barreira de transporte");
        _snapshotId = snapshot.SnapshotId;
        }
    }

    public async Task ReturnAsync(KlaAssayApiResult result, CancellationToken recoveryToken = default)
    {
        lock (_sync)
        {
        if (_ended || !_snapshotId.HasValue || result.Outcome.Restoration != KlaRestorationState.Confirmed ||
            result.ReturnSnapshotId != _snapshotId || string.IsNullOrWhiteSpace(result.PersistenceReceiptId))
            throw new InvalidOperationException("Retomada exige retorno ao snapshot e persistência confirmados.");
        }
        await _arbiter.DrainReservedCommandsAsync(Authority, recoveryToken).ConfigureAwait(false);
        lock (_sync)
        {
        if (_ended) throw new InvalidOperationException("Cessão encerrada durante a recuperação.");
        Authority = _arbiter.TransferReserved(Authority, CommandOwner.Recipe, "retorno confirmado ao estado anterior ao kLa");
        _arbiter.ReleaseReservation(Authority);
        _ended = true;
        try { foreach (var producer in _suspended.AsEnumerable().Reverse()) producer.Resume(); }
        finally { _releaseAssaySlot(); }
        }
    }

    public void AbortBeforeAssay()
    {
        lock (_sync)
        {
        if (_ended || _snapshotId.HasValue) throw new InvalidOperationException("Ensaio iniciado precisa de recuperação.");
        var current = _arbiter.IsCurrent(Authority);
        if (current) _arbiter.ReleaseReservation(Authority);
        _ended = true;
        try
        {
            foreach (var producer in _suspended.AsEnumerable().Reverse())
                if (current) producer.Resume(); else producer.Stop();
        }
        finally { _releaseAssaySlot(); }
        }
    }

    public void Fail()
    {
        lock (_sync)
        {
        if (_ended) return;
        _ended = true;
        try { foreach (var producer in _suspended) producer.Stop(); }
        finally { _releaseAssaySlot(); }
        // Keep the authority sealed. The recipe safety path revokes it and commands its explicit stop.
        }
    }
}
