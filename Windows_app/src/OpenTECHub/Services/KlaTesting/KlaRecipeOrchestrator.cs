using System.Collections.Immutable;
using System.Text.Json;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Unattended matrix over one E6 journal. Each pulse releases its scope before any retry wait.</summary>
public sealed class KlaRecipeOrchestrator(IKlaAssayApi api, KlaRecipeExecutionRouter router,
    IKlaRecipePulsePreparer preparer, IKlaTestStore store, TimeProvider time, bool allowMapImport = false)
{
    private int _started;
    private IKlaRecipePreparedPulse? _active;
    private readonly object _progressGate = new();
    private KlaRecipeProgressStage _progressStage = KlaRecipeProgressStage.Preparing;
    private int _finishedAttempts, _selectedAttempts;
    private KlaQueueItem? _currentItem;
    private bool _isWaiting;
    public RunPhase? CurrentPhase => Volatile.Read(ref _active)?.Phase;
    public KlaReturnSnapshot? CurrentReturnSnapshot => Volatile.Read(ref _active)?.Request.RecipePulse?.Invocation.Restoration.BeforeAssay;
    public KlaQueueItem? CurrentItem
    { get { lock (_progressGate) return _currentItem; } private set { lock (_progressGate) _currentItem = value; } }
    public bool IsWaiting
    { get { lock (_progressGate) return _isWaiting; } private set { lock (_progressGate) _isWaiting = value; } }
    private void SetProgress(KlaRecipeProgressStage stage) { lock (_progressGate) _progressStage = stage; }
    public KlaRecipeOrchestratorProgress ReadProgress()
    {
        lock (_progressGate)
        {
            var phase = CurrentPhase;
            var stage = phase == RunPhase.RestoringCultivation ? KlaRecipeProgressStage.Recovering : _progressStage;
            return new(stage, phase, _currentItem, _finishedAttempts, _selectedAttempts);
        }
    }

    public async Task<KlaRecipeResult> ExecuteAsync(KlaRecipeRequest template, KlaTestDocument preparedDocument,
        CancellationToken cancellation = default, KlaRecipePauseControl? pause = null)
    {
        template = RecipeContractSerializer.Snapshot(template);
        var document = JsonSerializer.Deserialize<KlaTestDocument>(JsonSerializer.Serialize(preparedDocument))!;
        if (!router.IsValidated || document.Runs.Count != 0 || document.SequenceLimits is not null ||
            JsonSerializer.Serialize(KlaAssayDefinition.FromDocument(document)) != JsonSerializer.Serialize(template.Definition))
            throw new ArgumentException("Matriz exige sessão nova, perfil isolado e definição congelada correspondente.");
        if (!allowMapImport && (document.LinkedMap is not null || template.Definition.Conditions.Any(c => c.Origin == ConditionOrigin.Map || c.SourceMapId is not null)))
            throw new ArgumentException("Importação de mapa não está habilitada para esta receita.");
        if (document.NitrogenSourceConfirmedUtc is null ||
            template.Definition.Protocol == KlaAssayProtocol.Biotic && document.NitrogenIsolationConfirmedUtc is null)
            throw new ArgumentException("Confirmações de montagem devem existir antes da execução autônoma.");
        if (document.RecipeRequest is not null &&
            RecipeContractSerializer.Fingerprint(document.RecipeRequest) != RecipeContractSerializer.Fingerprint(template))
            throw new ArgumentException("Sessão pertence a outra solicitação automática.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Orquestrador de invocação já utilizado.");
        document.RecipeRequest = template;
        store.SaveTestManifest(document);
        await store.FlushAsync().ConfigureAwait(false);
        var began = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(template.Retry.MaximumBlockSeconds), time);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var ownedPause = pause is null ? new KlaRecipePauseControl(time) : null;
        var pauseControl = pause ?? ownedPause!;
        var attempts = new List<KlaRecipeAttemptResult>();
        var pulses = new List<KlaAssayApiRequest>();
        long? lastEnd = null;
        bool restored = true, persisted = true;
        double Elapsed() => time.GetElapsedTime(began).TotalSeconds;

        async Task<KlaRecipeResult> Finish(KlaRecipeTerminalStatus status, string? reason = null)
        {
            SetProgress(KlaRecipeProgressStage.RecordingResult);
            preparer.ReleasePendingPreparation();
            CurrentItem = null; IsWaiting = false;
            var result = new KlaRecipeResult { Context = template.Context, PeriodicInvocation = template.PeriodicInvocation,
                SessionId = document.TestId, SessionFolder = document.FolderName, Status = status,
                Attempts = attempts.ToImmutableArray(), Pulses = pulses.ToImmutableArray(),
                PreAssayStateRestored = restored, PersistenceConfirmed = persisted, Reason = reason };
            result.ValidateAgainst(template);
            try { await store.PersistRecipeResultAsync(template, result).ConfigureAwait(false); }
            catch (Exception error) { return result with { Status = KlaRecipeTerminalStatus.PersistenceFailure,
                PersistenceConfirmed = false, Reason = $"Falha ao registrar matriz: {error.Message}" }; }
            return result;
        }

        while (true)
        {
            if (pauseControl.IsPaused)
            {
                preparer.ReleasePendingPreparation();
                SetProgress(KlaRecipeProgressStage.Paused);
                IsWaiting = true;
                try { await pauseControl.WaitUntilResumedAsync(acquisition.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (acquisition.IsCancellationRequested) { }
                finally { IsWaiting = false; }
            }
            if (acquisition.IsCancellationRequested || Elapsed() >= template.Retry.MaximumBlockSeconds || time.GetUtcNow() >= template.AcquisitionDeadlineUtc)
                return await Finish(cancellation.IsCancellationRequested ? KlaRecipeTerminalStatus.Cancelled : KlaRecipeTerminalStatus.Inconclusive,
                    "Execução interrompida ou orçamento de tempo esgotado.").ConfigureAwait(false);
            var next = KlaRecipeSequence.Next(template, attempts);
            if (next.TerminalStatus is { } status) return await Finish(status).ConfigureAwait(false);
            var item = CurrentItem = next.Next!;
            CancellationToken epoch = default;
            if (!pauseControl.TryDispatch(token => epoch = token)) continue;
            using var pulseCancellation = CancellationTokenSource.CreateLinkedTokenSource(acquisition.Token, epoch);
            var candidate = KlaRecipePulseMapper.Create(template, router.Capabilities.InstallationId,
                item.ConditionId, item.ReplicateNumber, item.AttemptNumber, time.GetUtcNow());
            var budget = api.ReadCultivationBudget(candidate);
            if (budget.RemainingAttempts == 0 || budget.RemainingRemovalSeconds < candidate.ReservedRemovalSeconds)
                return await Finish(KlaRecipeTerminalStatus.Inconclusive, "Orçamento persistido do cultivo esgotado.").ConfigureAwait(false);
            if (budget.BlockedReason is not null)
                return await Finish(KlaRecipeTerminalStatus.OperationalFailure, budget.BlockedReason).ConfigureAwait(false);
            var wait = Math.Max(budget.WaitSeconds, lastEnd is { } end
                ? Math.Max(0, template.Retry.MinimumInterAssaySeconds - time.GetElapsedTime(end).TotalSeconds) : 0);
            if (wait > 0)
            {
                preparer.ReleasePendingPreparation();
                if (Elapsed() + wait >= template.Retry.MaximumBlockSeconds || time.GetUtcNow().AddSeconds(wait) >= template.AcquisitionDeadlineUtc)
                    return await Finish(KlaRecipeTerminalStatus.Inconclusive, "Intervalo mínimo excede o tempo restante.").ConfigureAwait(false);
                IsWaiting = true;
                SetProgress(KlaRecipeProgressStage.WaitingBetweenAttempts);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), time, pulseCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { IsWaiting = false; }
                continue; // Re-read global limits before reacquiring any actuator.
            }
            var pulseDeadline = time.GetUtcNow().AddSeconds(template.Retry.MaximumBlockSeconds - Elapsed());
            if (pulseDeadline > template.AcquisitionDeadlineUtc) pulseDeadline = template.AcquisitionDeadlineUtc;
            IKlaRecipePreparedPulse scope;
            try
            {
                SetProgress(KlaRecipeProgressStage.ReservingResources);
                var current = store.LoadTest(document.FolderName) ?? throw new InvalidOperationException("Sessão comum ausente.");
                scope = await preparer.PrepareAsync(template, item, current, pulseDeadline, pulseCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { continue; }
            catch (Exception error) { return await Finish(KlaRecipeTerminalStatus.OperationalFailure, error.Message).ConfigureAwait(false); }
            Volatile.Write(ref _active, scope);
            SetProgress(KlaRecipeProgressStage.Acquiring);
            KlaAssayApiObservation? observation = null;
            Exception? executionError = null;
            bool created = false;
            bool skipped = false;
            try
            {
                using var registration = router.Register(scope.Request, scope.Execution);
                try
                {
                    Task<KlaAssayApiObservation>? starting = null;
                    skipped = !pauseControl.TryDispatch(epoch, () =>
                    {
                        api.Create(scope.Request); created = true;
                        starting = api.StartAsync(scope.Request.RequestId, pulseCancellation.Token);
                    });
                    if (!skipped)
                    {
                        await starting!.ConfigureAwait(false);
                        observation = await api.WaitForCompletionAsync(scope.Request.RequestId).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    executionError = error;
                    if (created) observation = await api.CancelWithRecoveryAsync(scope.Request.RequestId).ConfigureAwait(false);
                }
            }
            catch (Exception error) { executionError = error; }
            finally
            {
                if (scope.HasStarted) pulses.Add(scope.Request);
                restored = !scope.HasStarted || scope.HasReturnedSuccessfully;
                try { scope.Dispose(); }
                catch (Exception error) { executionError = error; restored = false; }
                Volatile.Write(ref _active, null);
            }
            if (skipped)
            {
                if (!restored) return await Finish(KlaRecipeTerminalStatus.RestorationFailure, executionError?.Message).ConfigureAwait(false);
                continue;
            }
            lastEnd = time.GetTimestamp();
            SetProgress(KlaRecipeProgressStage.RecordingDecision);
            var result = observation?.Result;
            if (observation is null || result is not { RunFolder: not null, TestFolder: not null } ||
                observation.State is KlaAssayApiState.PersistenceFailed or KlaAssayApiState.RestorationFailed)
            {
                persisted = !scope.HasStarted && observation?.State == KlaAssayApiState.Cancelled;
                var failedStatus = cancellation.IsCancellationRequested && !scope.HasStarted ? KlaRecipeTerminalStatus.Cancelled :
                    scope.HasStarted && result?.Outcome.Restoration != KlaRestorationState.Confirmed && !restored
                        ? KlaRecipeTerminalStatus.RestorationFailure :
                    observation?.State == KlaAssayApiState.PersistenceFailed || executionError is System.IO.IOException or AggregateException
                        ? KlaRecipeTerminalStatus.PersistenceFailure : KlaRecipeTerminalStatus.OperationalFailure;
                return await Finish(failedStatus, executionError?.Message ?? observation?.Reason).ConfigureAwait(false);
            }
            if (!restored) return await Finish(KlaRecipeTerminalStatus.RestorationFailure, "Reserva não confirmou a devolução dos produtores.").ConfigureAwait(false);
            try
            {
                var remaining = api.ReadCultivationBudget(scope.Request).RemainingAttempts;
                var elapsed = Elapsed();
                var pauseReceipt = observation.State == KlaAssayApiState.Cancelled && !acquisition.IsCancellationRequested
                    ? pauseControl.ReadPause(epoch, template.Context.InvocationId, scope.Request.RequestId) : null;
                var selection = new KlaRecipeSelectionCheckpoint { Observation = observation,
                    Decision = KlaRecipeAttemptDecider.Decide(observation, attempts, remaining, elapsed, time.GetUtcNow(), pauseReceipt),
                    Pause = pauseReceipt,
                    History = attempts.ToImmutableArray(), RemainingCultivationAttempts = remaining, ElapsedBlockSeconds = elapsed };
                await store.PersistRecipeSelectionAsync(selection).ConfigureAwait(false);
                attempts.Add(selection.Decision);
                lock (_progressGate)
                {
                    _finishedAttempts = attempts.Count;
                    _selectedAttempts = attempts.Count(a => a.Decision == KlaAutomaticDecision.Selected);
                }
            }
            catch (Exception error) { persisted = false; return await Finish(KlaRecipeTerminalStatus.PersistenceFailure, error.Message).ConfigureAwait(false); }
        }
    }
}
