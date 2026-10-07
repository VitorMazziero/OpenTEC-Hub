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

    public KlaAssayApi(string journalPath, IKlaAssayExecution execution, TimeProvider? time = null)
    {
        _journalPath = Path.GetFullPath(journalPath);
        _execution = execution;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        _lease = new FileStream(_journalPath + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
        _requests = File.Exists(_journalPath)
            ? (JsonSerializer.Deserialize<KlaAssayApiObservation[]>(File.ReadAllText(_journalPath), JsonOptions)
                ?? throw new InvalidDataException("Registro de solicitações vazio/inválido.")).ToDictionary(r => r.Request.RequestId)
            : new();
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
            if (_running.Count > 0) throw new InvalidOperationException("Outro ensaio detém a execução; aguarde a recuperação.");
            var cultivation = _requests.Values.Where(r => string.Equals(r.Request.CultivationId.Trim(),
                record.Request.CultivationId.Trim(), StringComparison.OrdinalIgnoreCase) && r.StartedUtc.HasValue).ToArray();
            if (_requests.Values.Any(r => r.State is KlaAssayApiState.Interrupted or KlaAssayApiState.RestorationFailed))
                throw new InvalidOperationException("Há um retorno físico sem confirmação; execução bloqueada.");
            // Saved limits can become stricter, never be loosened by a subsequent request in the same cultivation.
            var maxRuns = cultivation.Select(r => r.Request.Limits.MaximumRuns).Append(record.Request.Limits.MaximumRuns).Min();
            var maxExposure = cultivation.Select(r => r.Request.Limits.MaximumReservedRemovalSeconds)
                .Append(record.Request.Limits.MaximumReservedRemovalSeconds).Min();
            var interval = cultivation.Select(r => Math.Max(r.Request.Limits.MinimumIntervalSeconds,
                    r.Request.Definition.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0))
                .Append(Math.Max(record.Request.Limits.MinimumIntervalSeconds,
                    record.Request.Definition.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0)).Max();
            if (cultivation.Length >= maxRuns || cultivation.Sum(r => r.Request.ReservedRemovalSeconds) + record.Request.ReservedRemovalSeconds > maxExposure)
                throw new InvalidOperationException("Limite acumulado por cultivo atingido.");
            if (cultivation.Any(r => r.CompletedUtc is { } end && (now - end).TotalSeconds < interval))
                throw new InvalidOperationException("Intervalo mínimo ainda não cumprido; solicitação permanece adiada.");
            var delay = record.Request.DeadlineUtc - now;
            if (delay > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
                throw new ArgumentException("Deadline excede a janela suportada pelo temporizador.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancellation.CancelAfter(delay);
            record = record with { State = KlaAssayApiState.Running, StartedUtc = now };
            try { Persist(record); } catch { cancellation.Dispose(); throw; }
            // Reserve before dispatch and publish the task before execution can finish synchronously.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running.Add(requestId, (cancellation, completion.Task));
            _ = ExecuteAsync(record, cancellation, completion);
            return Task.FromResult(Require(requestId));
        }
    }

    private async Task ExecuteAsync(KlaAssayApiObservation record, CancellationTokenSource cancellation, TaskCompletionSource completion)
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
            var restorationConfirmed = record.Request.Definition.Protocol == KlaAssayProtocol.Biotic
                ? result.Outcome.Restoration == KlaRestorationState.Confirmed
                : result.Outcome.Restoration is KlaRestorationState.Confirmed or KlaRestorationState.NotRequired;
            var state = !restorationConfirmed ? KlaAssayApiState.RestorationFailed
                : cancellation.IsCancellationRequested || _time.GetUtcNow() >= record.Request.DeadlineUtc ? KlaAssayApiState.Cancelled
                : result.Outcome.KlaQuality is KlaScientificQuality.Valid or KlaScientificQuality.Conditional &&
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
                _requests[record.Request.RequestId] = record with { State = KlaAssayApiState.RestorationFailed,
                    Reason = $"Falha ao registrar término: {error.Message}" };
                _running.Remove(record.Request.RequestId);
                completion.TrySetException(error);
            }
            finally { cancellation.Dispose(); }
        }
    }

    public KlaAssayApiObservation Observe(Guid requestId) { lock (_gate) return Require(requestId); }
    public KlaAssayApiResult? GetResult(Guid requestId) => Observe(requestId).Result;

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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_requests.Values.ToArray(), JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, _journalPath, overwrite: true);
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
