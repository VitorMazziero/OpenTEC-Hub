using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

public interface IRecipeResourceSuspension
{
    bool CanResume => true;
    ControllerReturnSnapshot? CaptureControllerState() => null;
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
    private KlaReturnSnapshot? _capturedSnapshot;
    private string? _verifiedPersistenceReceipt;
    private RecipeAssayRecoveryResult? _recoveryEvidence;
    private readonly Action<OwnershipTransfer> _onRevoked;
    private bool _emergencyStopped;
    private bool _returned;
    private int _producersStopped;
    public Exception? ProducerStopError { get; private set; }
    public CommandAuthorityLease Authority { get; private set; }

    internal RecipeAssayResourceLease(ICommandAuthorityArbiter arbiter, CommandAuthorityLease authority,
        IReadOnlyList<IRecipeResourceSuspension> suspended, Action releaseAssaySlot)
    {
        _arbiter = arbiter; Authority = authority; _suspended = suspended; _releaseAssaySlot = releaseAssaySlot;
        _onRevoked = transfer =>
        {
            if (!transfer.Actuators.Intersect(authority.Resources).Any()) return;
            Volatile.Write(ref _emergencyStopped, true);
            StopProducersOnce();
        };
        _arbiter.OwnershipRevoked += _onRevoked;
    }

    public bool HasReturnedSuccessfully
    {
        get { lock (_sync) return _returned && !Volatile.Read(ref _emergencyStopped) &&
            Authority.Resources.All(a => _arbiter.OwnerOf(a) == CommandOwner.Recipe); }
    }

    private void StopProducersOnce()
    {
        if (Interlocked.Exchange(ref _producersStopped, 1) != 0) return;
        foreach (var producer in _suspended)
        {
            try { producer.Stop(); }
            catch (Exception error) { ProducerStopError = error; }
        }
    }

    public KlaReturnSnapshot CaptureReturnSnapshot(GasRigConfiguration rig, TimeProvider time,
        SensorSnapshot? observation = null, DateTimeOffset? observationReceivedUtc = null)
    {
        lock (_sync)
        {
            if (_ended || _snapshotId.HasValue || _suspended.Any(p => !p.CanResume))
                throw new InvalidOperationException("Não é possível capturar estado de produtor encerrado ou ensaio já iniciado.");
            var controllers = _suspended.Select(p => p.CaptureControllerState()).OfType<ControllerReturnSnapshot>().ToArray();
            return _capturedSnapshot = RecipeAssayReturnState.Capture(_arbiter, Authority, rig, time, controllers,
                observation, observationReceivedUtc);
        }
    }

    public bool IsAssayAuthorityCurrent
    {
        get { lock (_sync) return !_ended && !Volatile.Read(ref _emergencyStopped) && _snapshotId.HasValue && Authority.Owner == CommandOwner.KlaAssay && _arbiter.IsCurrent(Authority); }
    }

    public CommandDispatchResult DispatchAssay(OpenTECCommand command, bool separateFrame = false)
    {
        lock (_sync)
        {
            if (!IsAssayAuthorityCurrent) return new(false, CommandActuators.ActuatorsIn(command).ToArray(), CommandOwner.KlaAssay);
            return _arbiter.DispatchReserved(Authority, command, separateFrame);
        }
    }

    public Task DrainAssayCommandsAsync(CancellationToken recoveryToken)
    {
        lock (_sync)
        {
            if (!IsAssayAuthorityCurrent) throw new InvalidOperationException("Autoridade do ensaio encerrada ou revogada.");
            return _arbiter.DrainReservedCommandsAsync(Authority, recoveryToken);
        }
    }

    public void ValidateRecoverySnapshot(KlaReturnSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!IsAssayAuthorityCurrent || _snapshotId != snapshot.SnapshotId ||
                _capturedSnapshot is not null && !MatchesCapturedSnapshot(snapshot) ||
                snapshot.Actuators.Length != Authority.Resources.Length ||
                Authority.Resources.Any(a => !snapshot.Actuators.Any(s => s.Actuator == a &&
                    s.Owner == CommandOwner.Recipe && s.OwnerExecutionId == Authority.ExecutionId.ToString())))
                throw new InvalidOperationException("Snapshot não corresponde à autoridade ativa do ensaio.");
        }
    }

    public bool ControllersPreserved(KlaReturnSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_suspended.Any(p => !p.CanResume)) return false;
            var current = _suspended.Select(p => p.CaptureControllerState()).OfType<ControllerReturnSnapshot>()
                .OrderBy(p => p.ControllerId, StringComparer.Ordinal).ToArray();
            return current.SequenceEqual(snapshot.Controllers.OrderBy(p => p.ControllerId, StringComparer.Ordinal));
        }
    }

    private bool MatchesCapturedSnapshot(KlaReturnSnapshot snapshot) => _capturedSnapshot is null ||
        RecipeContractSerializer.Fingerprint(snapshot) == RecipeContractSerializer.Fingerprint(_capturedSnapshot);

    internal void RecordRecoveryEvidence(RecipeAssayRecoveryResult evidence)
    {
        lock (_sync)
        {
            if (!IsAssayAuthorityCurrent || evidence.SnapshotId != _snapshotId ||
                evidence.Restoration != KlaRestorationState.Confirmed || string.IsNullOrWhiteSpace(evidence.ConfirmationJson))
                throw new InvalidOperationException("Evidência de recuperação inválida ou autoridade revogada.");
            _recoveryEvidence = evidence;
        }
    }

    public void BeginAssay(KlaReturnSnapshot snapshot)
    {
        lock (_sync)
        {
            snapshot.Validate();
            if (!MatchesCapturedSnapshot(snapshot))
                throw new InvalidOperationException("Snapshot alterado após captura coordenada.");
            if (_ended || _snapshotId.HasValue || Authority.Owner != CommandOwner.Recipe || _suspended.Any(p => !p.CanResume) ||
                Authority.Resources.Any(a => !snapshot.Actuators.Any(s => s.Actuator == a &&
                    s.Owner == CommandOwner.Recipe && s.OwnerExecutionId == Authority.ExecutionId.ToString())))
                throw new InvalidOperationException("Snapshot não corresponde à reserva da receita.");
            Authority = _arbiter.TransferReserved(Authority, CommandOwner.KlaAssay, "início de kLa após pausa e barreira de transporte");
            _snapshotId = snapshot.SnapshotId;
        }
    }

    public async Task ReturnPersistedAsync(IKlaTestStore store, Guid requestId, string testFolder,
        string runFolder, CancellationToken recoveryToken = default)
    {
        var checkpoint = store.ReadRecipeAttemptCheckpoint(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.Terminal)
            ?? throw new InvalidOperationException("Checkpoint terminal ausente.");
        var receipt = store.ReadRecipeAttemptReceipt(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.Terminal)
            ?? throw new InvalidOperationException("Recibo terminal ausente.");
        var before = store.ReadRecipeAttemptCheckpoint(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.BeforeActuation)
            ?? throw new InvalidOperationException("Preparação persistida ausente.");
        checkpoint.Validate(); before.Validate();
        lock (_sync)
        {
            if (!IsAssayAuthorityCurrent || checkpoint.Authority != Authority &&
                (checkpoint.Authority.ReservationId != Authority.ReservationId || checkpoint.Authority.ExecutionId != Authority.ExecutionId ||
                 checkpoint.Authority.BlockId != Authority.BlockId || checkpoint.Authority.Owner != Authority.Owner ||
                 checkpoint.Authority.Generation != Authority.Generation || !checkpoint.Authority.Resources.SequenceEqual(Authority.Resources)) ||
                before.Authority.ReservationId != Authority.ReservationId || before.TestId != checkpoint.TestId || before.RunId != checkpoint.RunId ||
                System.Text.Json.JsonSerializer.Serialize(before.Request) != System.Text.Json.JsonSerializer.Serialize(checkpoint.Request) ||
                checkpoint.Request.RequestId != requestId || receipt.RequestId != requestId || receipt.TestId != checkpoint.TestId ||
                receipt.RunId != checkpoint.RunId || receipt.SnapshotId != _snapshotId ||
                !MatchesCapturedSnapshot(checkpoint.Request.RecipePulse!.Invocation.Restoration.BeforeAssay))
                throw new InvalidOperationException("Recibo não corresponde à cessão ativa.");
            _verifiedPersistenceReceipt = receipt.ReceiptId;
        }
        await ReturnAsync(checkpoint.Result! with { PersistenceReceiptId = receipt.ReceiptId }, recoveryToken).ConfigureAwait(false);
    }

    public async Task ReturnAsync(KlaAssayApiResult result, CancellationToken recoveryToken = default)
    {
        lock (_sync)
        {
            if (_ended || !_snapshotId.HasValue || _suspended.Any(p => !p.CanResume) || result.Outcome.Restoration != KlaRestorationState.Confirmed ||
                result.ReturnSnapshotId != _snapshotId || string.IsNullOrWhiteSpace(result.PersistenceReceiptId) ||
                _capturedSnapshot is not null && (_recoveryEvidence?.Restoration != KlaRestorationState.Confirmed ||
                    result.PersistenceReceiptId != _verifiedPersistenceReceipt))
                throw new InvalidOperationException("Retomada exige retorno ao snapshot e persistência confirmados.");
        }
        await _arbiter.DrainReservedCommandsAsync(Authority, recoveryToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_ended || _suspended.Any(p => !p.CanResume)) throw new InvalidOperationException("Cessão ou produtor encerrado durante a recuperação.");
            Authority = _arbiter.TransferReserved(Authority, CommandOwner.Recipe, "retorno confirmado ao estado anterior ao kLa");
            _arbiter.ReleaseReservation(Authority);
            _ended = true;
            try
            {
                foreach (var producer in _suspended.AsEnumerable().Reverse())
                {
                    if (Volatile.Read(ref _emergencyStopped)) { StopProducersOnce(); break; }
                    producer.Resume();
                }
                _returned = !Volatile.Read(ref _emergencyStopped);
            }
            catch { StopProducersOnce(); throw; }
            finally { _arbiter.OwnershipRevoked -= _onRevoked; _releaseAssaySlot(); }
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
                if (current) foreach (var producer in _suspended.AsEnumerable().Reverse()) producer.Resume();
                else StopProducersOnce();
            }
            finally { _arbiter.OwnershipRevoked -= _onRevoked; _releaseAssaySlot(); }
        }
    }

    public void Fail()
    {
        lock (_sync)
        {
            if (_ended) return;
            _ended = true;
            try { StopProducersOnce(); }
            finally { _arbiter.OwnershipRevoked -= _onRevoked; _releaseAssaySlot(); }
            // Keep the authority sealed. The recipe safety path revokes it and commands its explicit stop.
        }
    }
}
