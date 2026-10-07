using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Durable at-most-once dispatch. Construction and observation never command actuators.</summary>
public sealed class KlaAssayApi : IKlaAssayApi, IDisposable
{
    private readonly object _gate = new();
    private readonly string _journalPath;
    private readonly IKlaAssayExecution _execution;
    private readonly TimeProvider _time;
    private readonly FileStream _lease;
    private bool _disposed;
    private readonly Dictionary<Guid, KlaAssayApiObservation> _requests;
    private readonly Dictionary<Guid, (CancellationTokenSource Cancellation, Task Completion)> _running = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Journal(int SchemaVersion, KlaAssayApiObservation[] Requests);
    private const int JournalVersion = 2;

    public KlaAssayApi(string journalPath, IKlaAssayExecution execution, TimeProvider? time = null)
    {
        _journalPath = Path.GetFullPath(journalPath);
        _execution = execution;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        _lease = new FileStream(_journalPath + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
        _requests = File.Exists(_journalPath) ? ReadJournal().ToDictionary(r => r.Request.RequestId) : new();
        // A persisted reservation could have emitted commands. Never re-dispatch or infer recovery on reopen.
        foreach (var item in _requests.Values.ToArray())
        {
            item.Request.Validate();
            if (!Enum.IsDefined(item.State)) throw new InvalidDataException("Estado desconhecido no registro de solicitações.");
            if (item.State == KlaAssayApiState.Running)
                _requests[item.Request.RequestId] = item with { State = KlaAssayApiState.Interrupted,
                    Reason = "Execução interrompida: verificar o retorno físico; solicitação não será repetida." };
        }
        if (_requests.Count > 0) Save();
        }
        catch { _lease.Dispose(); throw; }
    }

    public KlaAssayApiObservation Create(KlaAssayApiRequest request)
    {
        request.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_requests.TryGetValue(request.RequestId, out var previous))
            {
                if (Fingerprint(previous.Request) != Fingerprint(request))
                    throw new InvalidOperationException("ID já registrado com parâmetros diferentes.");
                return previous;
            }
            var observation = new KlaAssayApiObservation(request, KlaAssayApiState.Created);
            _requests.Add(request.RequestId, observation);
            try { Save(); } catch { _requests.Remove(request.RequestId); throw; }
            return observation;
        }
    }

    public Task<KlaAssayApiObservation> StartAsync(Guid requestId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = Require(requestId);
            if (record.State != KlaAssayApiState.Created) return Task.FromResult(record);
            var now = _time.GetUtcNow();
            if (now > record.Request.StartDeadlineUtc)
            {
                record = record with { State = KlaAssayApiState.Skipped, CompletedUtc = now,
                    Reason = "Prazo para início expirou; pulso atrasado não será acumulado." };
                Persist(record);
                return Task.FromResult(record);
            }
            if (!_execution.IsValidated) throw new InvalidOperationException("Execução por receita requer validação operacional em bancada.");
            if (record.Request.RecipePulse is not null && _execution.Capabilities is null)
                throw new InvalidOperationException("Execução de receita requer capacidades por instalação e perfil.");
            _execution.EnsureAllows(record.Request);
            if (_running.Count > 0) throw new InvalidOperationException("Outro ensaio detém a execução; aguarde a recuperação.");
            var budget = Budget(record.Request, now);
            if (budget.BlockedReason is not null) throw new InvalidOperationException(budget.BlockedReason);
            if (budget.WaitSeconds > 0)
                throw new InvalidOperationException("Intervalo mínimo ainda não cumprido; solicitação permanece adiada.");
            var delay = record.Request.DeadlineUtc - now;
            if (delay > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
                throw new ArgumentException("Deadline excede a janela suportada pelo temporizador.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = _time.CreateTimer(_ =>
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }, null, delay, Timeout.InfiniteTimeSpan);
            record = record with { State = KlaAssayApiState.Running, StartedUtc = now };
            try { Persist(record); } catch { timer.Dispose(); cancellation.Dispose(); throw; }
            // Reserve before dispatch and publish the task before execution can finish synchronously.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running.Add(requestId, (cancellation, completion.Task));
            _ = ExecuteAsync(record, cancellation, timer, completion);
            return Task.FromResult(Require(requestId));
        }
    }

    private async Task ExecuteAsync(KlaAssayApiObservation record, CancellationTokenSource cancellation, ITimer timer, TaskCompletionSource completion)
    {
        // Do not call an adapter while the caller's StartAsync lock is held.
        await Task.Yield();
        KlaAssayApiResult result;
        try { result = await _execution.ExecuteWithRecoveryAsync(record.Request, cancellation.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            result = new(new KlaRunOutcome { Restoration = KlaRestorationState.Failed,
                RestorationReason = error.Message }, null, Reason: "O executor não confirmou recuperação.");
        }
        lock (_gate)
        {
            var binding = record.Request.RecipePulse;
            var restorationConfirmed = result.Outcome.Restoration == KlaRestorationState.Confirmed &&
                (binding is null || result.ReturnSnapshotId == binding.Invocation.Restoration.BeforeAssay.SnapshotId);
            var persisted = binding is null || !string.IsNullOrWhiteSpace(result.PersistenceReceiptId);
            var qualityAccepted = KlaRecipeQualityEvaluator.Accepts(record.Request, result);
            var state = !restorationConfirmed ? KlaAssayApiState.RestorationFailed
                : !persisted ? KlaAssayApiState.PersistenceFailed
                : cancellation.IsCancellationRequested || _time.GetUtcNow() >= record.Request.DeadlineUtc ? KlaAssayApiState.Cancelled
                : qualityAccepted &&
                    result.KlaPerHour is { } value && double.IsFinite(value) && value > 0
                    ? KlaAssayApiState.Completed : KlaAssayApiState.Inconclusive;
            record = record with { State = state, CompletedUtc = _time.GetUtcNow(), Result = result, Reason = result.Reason };
            try
            {
                Persist(record);
                _running.Remove(record.Request.RequestId);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                // Persisted Running reservation remains fail-closed on restart; failed write blocks new requests now too.
                _requests[record.Request.RequestId] = record with { State = KlaAssayApiState.PersistenceFailed,
                    Reason = $"Falha ao registrar término: {error.Message}" };
                _running.Remove(record.Request.RequestId);
                completion.TrySetException(error);
            }
            finally { timer.Dispose(); cancellation.Dispose(); }
        }
    }

    public KlaAssayApiObservation Observe(Guid requestId) { lock (_gate) return Require(requestId); }
    public KlaAssayApiResult? GetResult(Guid requestId) => Observe(requestId).Result;

    public KlaCultivationAssayBudget ReadCultivationBudget(KlaAssayApiRequest request)
    {
        request.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Budget(request, _time.GetUtcNow());
        }
    }

    private KlaCultivationAssayBudget Budget(KlaAssayApiRequest request, DateTimeOffset now)
        => Budget(new KlaAssayBudgetQuery(request.CultivationId, request.Limits, request.ReservedRemovalSeconds,
            request.Definition.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0), now);

    public KlaCultivationAssayBudget ReadCultivationBudget(KlaAssayBudgetQuery query)
    {
        query.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Budget(query, _time.GetUtcNow());
        }
    }

    private KlaCultivationAssayBudget Budget(KlaAssayBudgetQuery request, DateTimeOffset now)
    {
        var cultivation = _requests.Values.Where(r => string.Equals(r.Request.CultivationId.Trim(),
            request.CultivationId.Trim(), StringComparison.OrdinalIgnoreCase) && r.StartedUtc.HasValue).ToArray();
        // A later profile can tighten limits, but cannot erase the previously reserved budget.
        var maxRuns = cultivation.Select(r => r.Request.Limits.MaximumRuns).Append(request.Limits.MaximumRuns).Min();
        var maxExposure = cultivation.Select(r => r.Request.Limits.MaximumReservedRemovalSeconds)
            .Append(request.Limits.MaximumReservedRemovalSeconds).Min();
        var interval = cultivation.Select(r => Math.Max(r.Request.Limits.MinimumIntervalSeconds,
                r.Request.Definition.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0))
            .Append(Math.Max(request.Limits.MinimumIntervalSeconds,
                request.MinimumIntervalSeconds)).Max();
        var remaining = Math.Max(0, maxRuns - cultivation.Length);
        var exposure = Math.Max(0, maxExposure - cultivation.Sum(r => r.Request.ReservedRemovalSeconds));
        var wait = cultivation.Where(r => r.CompletedUtc.HasValue)
            .Select(r => Math.Max(0, interval - (now - r.CompletedUtc!.Value).TotalSeconds)).DefaultIfEmpty(0).Max();
        var blocked = _requests.Values.Any(r => r.State is KlaAssayApiState.Interrupted or KlaAssayApiState.RestorationFailed or KlaAssayApiState.PersistenceFailed)
            ? "Há retorno ou persistência sem confirmação; execução bloqueada."
            : _running.Count > 0 ? "Outro ensaio detém a execução; aguarde a recuperação."
            : remaining == 0 || exposure < request.ReservedRemovalSeconds ? "Limite acumulado por cultivo atingido." : null;
        return new(remaining, exposure, wait, blocked);
    }

    /// <summary>Reconciles historical storage without restarting acquisition or certifying live recovery.</summary>
    public KlaAssayApiObservation ReconcileRecipeAttempt(Guid requestId, IKlaTestStore store, string testFolder, string runFolder)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = Require(requestId);
            if (record.Request.RecipePulse is null || _running.ContainsKey(requestId))
                throw new InvalidOperationException("Reconciliação requer tentativa de receita sem executor ativo.");
            var evidence = KlaAttemptReconciliation.Read(store, record, testFolder, runFolder);
            if (!evidence.ChargeReservedBudget) return record;
            var preparation = store.ReadRecipeAttemptReceipt(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.BeforeActuation);
            var terminal = store.ReadRecipeAttemptReceipt(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.Terminal);
            var uncertain = evidence.RequiresRecoveryVerification || record.State is KlaAssayApiState.Created or KlaAssayApiState.Cancelled or KlaAssayApiState.Skipped;
            var reconciled = record with
            {
                StartedUtc = record.StartedUtc ?? preparation?.PersistedUtc ?? _time.GetUtcNow(),
                CompletedUtc = record.CompletedUtc ?? terminal?.PersistedUtc,
                Result = evidence.PersistedResult ?? record.Result,
                State = uncertain ? KlaAssayApiState.Interrupted : record.State,
                Reason = uncertain ? "Tentativa reconciliada sem repetir atuação; recuperação atual requer verificação." : record.Reason
            };
            Persist(reconciled);
            return reconciled;
        }
    }

    /// <summary>Waiting cancellation stops observation only; use CancelWithRecoveryAsync to cancel acquisition.</summary>
    public async Task<KlaAssayApiObservation> WaitForCompletionAsync(Guid requestId, CancellationToken ct = default)
    {
        Task? completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Require(requestId);
            completion = _running.TryGetValue(requestId, out var active) ? active.Completion : null;
        }
        if (completion is not null) await completion.WaitAsync(ct).ConfigureAwait(false);
        return Observe(requestId);
    }

    public async Task<KlaAssayApiObservation> CancelWithRecoveryAsync(Guid requestId)
    {
        Task? completion = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = Require(requestId);
            if (_running.TryGetValue(requestId, out var active)) { cancellation = active.Cancellation; completion = active.Completion; }
            else if (record.State == KlaAssayApiState.Created)
                Persist(record with { State = KlaAssayApiState.Cancelled, CompletedUtc = _time.GetUtcNow(), Reason = "Cancelado antes do início." });
        }
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { /* execution finished */ }
            await completion!.ConfigureAwait(false);
        }
        return Observe(requestId);
    }

    private KlaAssayApiObservation Require(Guid id) => _requests.TryGetValue(id, out var record)
        ? record : throw new KeyNotFoundException("Solicitação não encontrada.");

    private void Persist(KlaAssayApiObservation record)
    {
        var previous = Require(record.Request.RequestId);
        _requests[record.Request.RequestId] = record;
        try { Save(); } catch { _requests[record.Request.RequestId] = previous; throw; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temporary = _journalPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Journal(JournalVersion, _requests.Values.ToArray()), JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        ReplaceJournal(temporary);
    }

    private void ReplaceJournal(string temporary)
    {
        // Windows readers (including scanners) can briefly deny replacement of an otherwise
        // writable file. Retry only that bounded case; directories/read-only targets and other
        // storage errors remain immediate failures. Never advance before the move succeeds.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, _journalPath, overwrite: true); return; }
            catch (Exception error) when (attempt < 3 && IsTransientReplacementFailure(error))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
            }
        }
    }

    private bool IsTransientReplacementFailure(Exception error)
    {
        if (error is IOException && (error.HResult & 0xffff) is 32 or 33) return true;
        if (error is not UnauthorizedAccessException || Directory.Exists(_journalPath) || !File.Exists(_journalPath)) return false;
        return (File.GetAttributes(_journalPath) & FileAttributes.ReadOnly) == 0;
    }

    private KlaAssayApiObservation[] ReadJournal()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_journalPath));
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.Deserialize<KlaAssayApiObservation[]>(JsonOptions)
                ?? throw new InvalidDataException("Registro legado vazio/inválido.");
        var journal = document.RootElement.Deserialize<Journal>(JsonOptions)
            ?? throw new InvalidDataException("Registro de solicitações vazio/inválido.");
        if (journal.SchemaVersion != JournalVersion || journal.Requests is null)
            throw new InvalidDataException("Versão de diário de kLa desconhecida.");
        return journal.Requests;
    }

    private static string Fingerprint(KlaAssayApiRequest request) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_running.Count > 0) throw new InvalidOperationException("Cancele e aguarde o retorno físico antes de fechar a API.");
            _lease.Dispose();
            _disposed = true;
        }
    }
}
