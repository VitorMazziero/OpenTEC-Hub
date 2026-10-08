using System.Security.Cryptography;
using System.Text;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaRecipeActiveInvocation(KlaRecipeRequest Request, string SessionFolder, KlaRecipeOrchestrator Orchestrator);

/// <summary>One qualified host, one API journal and common scientific store for every profile and slot.</summary>
public sealed class KlaRecipeAutonomousWorkSource(KlaRecipeOperationalProfileRegistry profiles,
    KlaRecipeExecutionRouter router, IKlaAssayApi api, KlaRecipeAssayExecutionFactory factory,
    IKlaTestStore store, ISettingsService settings, TimeProvider time, BackgroundFileWriter writer,
    string periodicJournalRoot, Func<string?> activeCultivation) : IRecipeAutonomousWorkSource
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, KlaRecipeActiveInvocation> _active = [];
    private sealed record PendingProgress(RecipeInvocationContext Context, RecipeKlaBlockConfiguration Configuration,
        PeriodicBlockInvocation? Slot, long Began, KlaRecipeProgressStage Stage, KlaAssayBudgetQuery? BudgetQuery = null);
    private readonly Dictionary<Guid, PendingProgress> _progress = [];
    public IReadOnlyList<KlaRecipeActiveInvocation> ActiveInvocations
    { get { lock (_gate) return _active.Values.ToArray(); } }

    public IReadOnlyList<KlaRecipeProgress> ReadProgress()
    {
        PendingProgress[] pending;
        Dictionary<Guid, KlaRecipeActiveInvocation> active;
        lock (_gate) { pending = _progress.Values.ToArray(); active = new(_active); }
        return pending.Select(entry =>
        {
            active.TryGetValue(entry.Context.InvocationId, out var invocation);
            var observed = invocation?.Orchestrator.ReadProgress();
            var elapsed = time.GetElapsedTime(entry.Began).TotalSeconds;
            var remaining = Math.Max(0, entry.Configuration.Retry.MaximumBlockSeconds - elapsed);
            if (invocation is not null)
                remaining = Math.Min(remaining, Math.Max(0, (invocation.Request.AcquisitionDeadlineUtc - time.GetUtcNow()).TotalSeconds));
            KlaCultivationAssayBudget? budget = null;
            DateTimeOffset? budgetUtc = null;
            string? error = null;
            if (entry.BudgetQuery is { } query)
            {
                try { budget = api.ReadCultivationBudget(query); budgetUtc = time.GetUtcNow(); }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or System.IO.IOException)
                { error = failure.Message; }
            }
            return new KlaRecipeProgress(entry.Context, entry.Configuration.Protocol, entry.Slot,
                entry.Configuration.ProfileId, entry.Configuration.ProfileVersion, invocation?.SessionFolder,
                observed?.Stage ?? entry.Stage, observed?.Phase, observed?.Item,
                invocation?.Request.Definition.Conditions.SingleOrDefault(c => c.ConditionId == observed?.Item?.ConditionId),
                observed?.FinishedAttempts ?? 0, observed?.SelectedAttempts ?? 0, elapsed, remaining, budget, budgetUtc, error);
        }).ToArray();
    }

    private void ReportProgress(RecipeInvocationContext context, RecipeKlaBlockConfiguration configuration,
        PeriodicBlockInvocation? slot, long began, KlaRecipeProgressStage stage, KlaAssayBudgetQuery? query = null)
    {
        lock (_gate) _progress[context.InvocationId] = new(context, configuration, slot, began, stage, query);
    }

    public bool CanExecute(RecipeDocument recipe, out string? reason)
    {
        try
        {
            if (!profiles.IsIsolatedEnvironment || !router.IsValidated)
                throw new InvalidOperationException("Perfil operacional não qualificado para atuação física nesta instalação.");
            if (string.IsNullOrWhiteSpace(activeCultivation()))
                throw new InvalidOperationException("Identifique o cultivo antes de iniciar os ensaios automáticos.");
            if (!RecipeValidator.Validate(recipe).IsValid) throw new ArgumentException("Receita possui erros de validação.");
            foreach (var node in recipe.Nodes.Where(n => n.Type == NodeType.KlaAssay))
            {
                var profile = profiles.Resolve(RecipeAutonomousBlockConfiguration.ReadKla(node));
                if (!string.IsNullOrWhiteSpace(profile.Template.Context?.CultivationId) &&
                    profile.Template.Context.CultivationId != activeCultivation())
                    throw new InvalidOperationException("Perfil científico pertence a outro cultivo.");
                if (recipe.IncomingTo(node.Id).Count() != 1)
                    throw new ArgumentException("Ensaio exige uma única entrada; sincronize seus ramos antes do bloco.");
            }
            foreach (var node in recipe.Nodes.Where(n => n.Type == NodeType.Periodic))
            {
                var binding = RecipePeriodicTopology.ReadBinding(recipe, node);
                var profile = profiles.Resolve(RecipeAutonomousBlockConfiguration.ReadKla(recipe.Node(binding.TargetNodeId)!));
                if (profile.DispatchToleranceSeconds >= RecipeAutonomousBlockConfiguration.ReadPeriodic(node).Schedule.PeriodSeconds)
                    throw new ArgumentException("Período deve exceder a tolerância de disparo qualificada.");
            }
            reason = null; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { reason = error.Message; return false; }
    }

    public RecipeAutonomousExecutionPlan CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator resources)
    {
        if (!CanExecute(recipe, out var reason)) throw new InvalidOperationException(reason);
        var cultivation = activeCultivation()!;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RecipeSerializer.Serialize(recipe)))).ToLowerInvariant();
        var journal = new RecipePeriodicJournal(periodicJournalRoot, executionId, hash, writer, time);
        var assays = recipe.Nodes.Where(n => n.Type == NodeType.KlaAssay).Select(node =>
        {
            var configuration = RecipeAutonomousBlockConfiguration.ReadKla(node);
            var profile = profiles.Resolve(configuration);
            return new RecipeKlaWork(node.Id, profile.Capabilities, (slot, ct) => ExecuteAsync(node.Id,
                $"{recipe.Name} — {node.Definition.Title}", configuration, new() { RecipeRunId = executionId, NodeId = node.Id, InvocationId = Guid.NewGuid(),
                    CultivationId = cultivation, RecipeSha256 = hash }, slot, resources, ct));
        }).ToArray();
        var schedules = recipe.Nodes.Where(n => n.Type == NodeType.Periodic).Select(node =>
        {
            var binding = RecipePeriodicTopology.ReadBinding(recipe, node);
            var profile = profiles.Resolve(RecipeAutonomousBlockConfiguration.ReadKla(recipe.Node(binding.TargetNodeId)!));
            return new RecipePeriodicDefinition(new() { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = node.Id,
                TargetNodeId = binding.TargetNodeId, CoordinatedCascadeNodeId = binding.CascadeNodeId,
                SlotIndex = 0, Schedule = RecipeAutonomousBlockConfiguration.ReadPeriodic(node).Schedule },
                TimeSpan.FromSeconds(profile.DispatchToleranceSeconds), journal.RecordAsync);
        }).ToArray();
        return new(assays, schedules);
    }

    private async Task<KlaRecipeResult> ExecuteAsync(string nodeId, string sessionName, RecipeKlaBlockConfiguration configuration,
        RecipeInvocationContext context, PeriodicBlockInvocation? slot, RecipeResourceCoordinator resources, CancellationToken ct)
    {
        var began = time.GetTimestamp();
        ReportProgress(context, configuration, slot, began, KlaRecipeProgressStage.Preparing);
        RecipeAssayResourceLease? initial = null;
        KlaRecipePulsePreparer? preparer = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var profile = profiles.Resolve(configuration);
                var remaining = configuration.Retry.MaximumBlockSeconds - time.GetElapsedTime(began).TotalSeconds;
                if (remaining <= 0) throw new TimeoutException("Prazo do bloco expirou antes de preparar o ensaio.");
                var query = new KlaAssayBudgetQuery(context.CultivationId,
                    new(configuration.Retry.MaximumAttemptsPerCultivation, configuration.Retry.MaximumCumulativeGasOffSecondsPerCultivation,
                        configuration.Retry.MinimumInterAssaySeconds),
                    profile.RemovalSeconds + 2 * profile.Template.ProtocolSettings.CommandConfirmationTimeoutSeconds,
                    profile.Template.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0);
                var budget = api.ReadCultivationBudget(query);
                ReportProgress(context, configuration, slot, began, KlaRecipeProgressStage.Preparing, query);
                if (budget.WaitSeconds > 0 && budget.BlockedReason is null && budget.WaitSeconds < remaining)
                {
                    ReportProgress(context, configuration, slot, began, KlaRecipeProgressStage.WaitingCultivationInterval, query);
                    await Task.Delay(TimeSpan.FromSeconds(budget.WaitSeconds), time, ct).ConfigureAwait(false);
                    continue;
                }
                ReportProgress(context, configuration, slot, began, KlaRecipeProgressStage.ReservingResources, query);
                initial = await resources.ReserveForAssayAsync(context, [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen],
                    TimeSpan.FromSeconds(Math.Min(profile.ReservationTimeoutSeconds, remaining)), ct).ConfigureAwait(false);
                budget = api.ReadCultivationBudget(query);
                if (budget.WaitSeconds > 0 && budget.BlockedReason is null && budget.WaitSeconds < remaining)
                { initial.AbortBeforeAssay(); initial = null; continue; }
                var snapshot = initial.CaptureReturnSnapshot(settings.Current.GasRig.ToConfiguration(), time);
                remaining = configuration.Retry.MaximumBlockSeconds - time.GetElapsedTime(began).TotalSeconds;
                if (remaining <= 0) throw new TimeoutException("Prazo do bloco expirou durante a reserva.");
                var request = KlaRecipeRequestBuilder.Build(context, configuration with { Retry = configuration.Retry with
                    { MaximumBlockSeconds = remaining } }, profile, snapshot, time.GetUtcNow(), slot);
                var document = store.CreateTest(sessionName, request.Definition);
                document.NitrogenSourceConfirmedUtc = profile.NitrogenSourceConfirmedUtc;
                document.NitrogenIsolationConfirmedUtc = profile.NitrogenIsolationConfirmedUtc;
                store.SaveTestManifest(document);
                preparer = new(resources, factory, settings, time, profile.Capabilities, profile.RecoveryCriteria,
                    TimeSpan.FromSeconds(profile.ReservationTimeoutSeconds), initial);
                initial = null; // The preparer now owns cancellation and release of the unused first reservation.
                var orchestrator = new KlaRecipeOrchestrator(api, router, preparer, store, time);
                lock (_gate) _active.Add(context.InvocationId, new(request, document.FolderName, orchestrator));
                return await orchestrator.ExecuteAsync(request, document, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) { _active.Remove(context.InvocationId); _progress.Remove(context.InvocationId); }
            preparer?.Dispose();
            initial?.AbortBeforeAssay();
        }
    }
}
