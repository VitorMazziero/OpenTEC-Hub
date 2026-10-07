using System.Text.Json;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>One reserved pulse, using the common runner, scientific engine and session store.</summary>
public sealed class KlaRecipeAssayExecution : IKlaAssayExecution
{
    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly IKlaTestStore _store;
    private readonly IKlaAnalysisEngine _analysis;
    private readonly IKlaDeterministicAnalysisEngine _deterministic;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly RecipeAssayResourceLease _lease;
    private readonly KlaTestDocument _document;
    private readonly RecipeAssayRecoveryCriteria _recoveryCriteria;
    private int _started;
    public KlaAssayExecutionCapabilities Capabilities { get; }
    public bool IsValidated => Capabilities.IsIsolatedSimulation;

    public KlaRecipeAssayExecution(IDeviceService device, ICommandArbiter arbiter, IKlaTestStore store,
        IKlaAnalysisEngine analysis, ISettingsService settings, TimeProvider time, RecipeAssayResourceLease lease,
        KlaTestDocument document, RecipeAssayRecoveryCriteria recoveryCriteria, KlaAssayExecutionCapabilities capabilities)
    {
        _device = device; _arbiter = arbiter; _store = store; _analysis = analysis;
        _deterministic = analysis as IKlaDeterministicAnalysisEngine
            ?? throw new ArgumentException("Análise comum deve suportar o estimador determinístico.");
        _settings = settings; _time = time; _lease = lease;
        _document = JsonSerializer.Deserialize<KlaTestDocument>(JsonSerializer.Serialize(document))
            ?? throw new ArgumentException("Sessão ausente.");
        recoveryCriteria.Validate(); _recoveryCriteria = recoveryCriteria;
        Capabilities = capabilities;
    }

    public async Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken acquisitionCancellation)
    {
        request.Validate();
        if (!IsValidated) throw new InvalidOperationException("Adaptador físico fechado até qualificação R6.2.");
        Capabilities.EnsureAllows(request);
        var binding = request.RecipePulse ?? throw new ArgumentException("Executor requer vínculo de receita.");
        var invocation = binding.Invocation;
        _lease.ValidateRecoverySnapshot(invocation.Restoration.BeforeAssay);
        if (_lease.Authority.ExecutionId != invocation.Context.RecipeRunId || _lease.Authority.BlockId != invocation.Context.NodeId ||
            _document.EffectiveProtocol != request.Definition.Protocol ||
            JsonSerializer.Serialize(_document.Settings) != JsonSerializer.Serialize(request.Definition.Settings) ||
            JsonSerializer.Serialize(_document.ProtocolSettings) != JsonSerializer.Serialize(request.Definition.ProtocolSettings))
            throw new ArgumentException("Sessão e reserva não correspondem ao request congelado.");
        var planned = request.Definition.Conditions.Single();
        var condition = _document.Conditions.Single(c => c.ConditionId == planned.ConditionId);
        if (condition.AgitationRpm != planned.AgitationRpm || condition.AirflowLpm != planned.AirflowLpm)
            throw new ArgumentException("Condição da sessão diverge do pulso.");
        if (request.Definition.Protocol == KlaAssayProtocol.Biotic && _recoveryCriteria.MinimumOxygenPercent is null)
            throw new ArgumentException("Retorno biótico requer faixa e estabilidade de OD explícitas.");
        if (request.Definition.Protocol == KlaAssayProtocol.Biotic && _document.NitrogenIsolationConfirmedUtc is null)
            throw new ArgumentException("Isolamento físico de N₂ deve ser confirmado durante a preparação da sessão.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Executor de pulso já utilizado.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0,
            Math.Min((request.DeadlineUtc - _time.GetUtcNow()).TotalSeconds, invocation.Retry.MaximumBlockSeconds))), _time);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(acquisitionCancellation, deadline.Token);

        KlaAttemptPersistenceCheckpoint? prepared = null;
        using var runner = new KlaTestRunner(_device, _arbiter, _store, _analysis, _settings, _time,
            actuationRelease: new(isIsolatedSimulation: true), recipeLease: _lease,
            recipeReturnSnapshot: invocation.Restoration.BeforeAssay, beforeActuation: async (document, run, token) =>
            {
                var checkpoint = new KlaAttemptPersistenceCheckpoint
                {
                    Request = request, Authority = _lease.Authority, TestId = document.TestId, RunId = run.RunId,
                    TestFolder = document.FolderName, RunFolder = run.FolderName, Phase = KlaAttemptPersistencePhase.BeforeActuation
                };
                await _store.PersistRecipeAttemptAsync(checkpoint, token).ConfigureAwait(false);
                prepared = checkpoint;
            });
        async Task WaitForInitialObservation(CancellationToken token)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTelemetry(OpenTECHub.Protocol.SensorSnapshot _) { if (runner.HasReadyInitialObservation(condition)) ready.TrySetResult(); }
            _device.TelemetryReceived += OnTelemetry;
            try
            {
                if (runner.HasReadyInitialObservation(condition)) return;
                await ready.Task.WaitAsync(token).ConfigureAwait(false);
            }
            finally { _device.TelemetryReceived -= OnTelemetry; }
        }
        var lifecycle = await new KlaRecipePulseLifecycle(runner, _lease, new RecipeAssayRestoration(_device, _time), _time)
            .ExecuteAsync(_document, condition, _document.EffectiveCaptureMode == KlaCaptureMode.Single ? 1 : binding.ReplicateNumber,
                invocation.Restoration, _recoveryCriteria, acquisition.Token, WaitForInitialObservation).ConfigureAwait(false);
        var outcome = new KlaRunOutcome { Restoration = lifecycle.Recovery.Restoration,
            RestorationReason = lifecycle.Recovery.Reason, KlaQuality = KlaScientificQuality.Inconclusive,
            OurQuality = request.Definition.Protocol == KlaAssayProtocol.Abiotic ? KlaScientificQuality.NotApplicable : KlaScientificQuality.NotEvaluated };
        var result = new KlaAssayApiResult(outcome, null, _document.FolderName, runner.CurrentRun?.FolderName,
            lifecycle.Acquisition.Reason) { ReturnSnapshotId = lifecycle.Recovery.SnapshotId };
        try
        {
            var run = runner.CurrentRun;
            if (prepared is null || run?.Definition is null || lifecycle.RecoveryRecordingError is not null)
                throw new InvalidOperationException(lifecycle.RecoveryRecordingError ?? "Preparação durável ou definição da corrida ausente.");
            var points = runner.CurrentRunPoints;
            run.RawDataSha256 = _store.SaveRunRawData(_document.FolderName, run.FolderName, points);
            var scientificRequest = KlaDeterministicRequestFactory.FromRun(run.Definition, points, run.GasEvents,
                invocation.Quality.Analysis, run.RawDataSha256);
            var scientific = _deterministic.AnalyzeDeterministic(scientificRequest);
            var revision = scientific.ToRevision(1, _time.GetUtcNow());
            outcome = revision.Outcome! with
            {
                Restoration = lifecycle.Recovery.Restoration, RestorationReason = lifecycle.Recovery.Reason,
                OperatorDecision = KlaOperatorDecision.Pending,
                KlaQuality = lifecycle.Acquisition.State == KlaRecipeAcquisitionState.Completed
                    ? scientific.KlaQuality : KlaScientificQuality.Inconclusive
            };
            revision.Outcome = outcome;
            run.Outcome = outcome; run.LatestAnalysis = revision; run.CompletedUtc = _time.GetUtcNow();
            _store.SaveRunAnalysis(_document.FolderName, run.FolderName, revision);
            _store.SaveRunResult(_document.FolderName, run.FolderName, run, revision);
            _store.SaveRunPhysicalOutcome(_document.FolderName, run.FolderName, outcome);
            _document.Runs.RemoveAll(r => r.RunId == run.RunId);
            _document.Runs.Add(new()
            {
                RunId = run.RunId, ConditionId = run.ConditionId, ReplicateNumber = run.ReplicateNumber,
                AttemptNumber = run.AttemptNumber, Definition = run.Definition, Context = run.Context,
                FolderName = run.FolderName, AgitationRpm = run.AgitationRpm, AirflowLpm = run.AirflowLpm,
                Phase = RunPhase.Reviewing, Outcome = outcome, KlaPerHour = scientific.KlaPerHour,
                AnalysisR2 = revision.AnalysisR2, StartedUtc = run.StartedUtc, CompletedUtc = run.CompletedUtc
            });
            _store.SaveTestManifest(_document); _store.UpdateResultsSummary(_document.FolderName, _document);
            var reasons = scientific.Reasons;
            if (lifecycle.Acquisition.State != KlaRecipeAcquisitionState.Completed)
                reasons = reasons.Add($"acquisition_{lifecycle.Acquisition.State.ToString().ToLowerInvariant()}");
            result = result with { Outcome = outcome, KlaPerHour = scientific.KlaPerHour, ReasonCodes = reasons };
            var receipt = await _store.PersistRecipeAttemptAsync(prepared with
            {
                Authority = _lease.Authority, Phase = KlaAttemptPersistencePhase.Terminal, Result = result,
                DecisionJson = JsonSerializer.Serialize(new { Author = "AutomaticPolicy", State = "AwaitingSelection",
                    Acquisition = lifecycle.Acquisition.State, ProfileId = invocation.Quality.ProfileId, Version = invocation.Quality.Version })
            }).ConfigureAwait(false);
            result = result with { PersistenceReceiptId = receipt.ReceiptId };
            if (lifecycle.Recovery.Restoration == KlaRestorationState.Confirmed)
            {
                await _lease.ReturnPersistedAsync(_store, request.RequestId, _document.FolderName, run.FolderName).ConfigureAwait(false);
                runner.ConfirmRecipeReturn();
            }
            return result;
        }
        catch (Exception ex)
        {
            _lease.Fail();
            return result with { PersistenceReceiptId = null, Reason = $"Conclusão bloqueada: {ex.Message}" };
        }
    }
}
