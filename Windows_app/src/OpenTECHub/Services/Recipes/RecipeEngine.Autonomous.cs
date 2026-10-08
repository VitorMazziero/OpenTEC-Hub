using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    private readonly IRecipeAutonomousWorkSource? _autonomousWorkSource;
    private readonly Dictionary<string, RecipeKlaWork> _klaWork = [];
    private readonly Dictionary<string, KlaRecipeFailurePolicy> _klaFailurePolicies = [];
    private readonly Dictionary<string, RecipePeriodicBinding> _graphPeriodicBindings = [];
    private readonly Dictionary<string, RecipeCascadePeriodicGroup> _graphSchedulerGroups = [];
    private readonly List<KlaRecipeResult> _autonomousResults = [];
    private string _autonomousRecipeHash = "";
    private readonly object _autonomousPauseTransitionGate = new();

    private void DisposeAutonomousPauseControls()
    {
        lock (_autonomousPauseTransitionGate)
        {
            KlaRecipePauseControl[] controls;
            lock (_lock) controls = _klaWork.Values.Select(w => w.PauseControl).OfType<KlaRecipePauseControl>().Distinct().ToArray();
            foreach (var control in controls) control.Dispose();
        }
    }

    public IReadOnlyList<KlaRecipeResult> AutonomousResults
    {
        get { lock (_lock) return _autonomousResults.ToArray(); }
    }

    private IReadOnlyList<RecipePeriodicWork> PrepareAutonomousWork(RecipeDocument recipe)
    {
        DisposeAutonomousPauseControls();
        _klaWork.Clear(); _klaFailurePolicies.Clear(); _graphPeriodicBindings.Clear();
        lock (_lock) { _autonomousResults.Clear(); _graphSchedulerGroups.Clear(); }
        if (!recipe.Nodes.Any(n => n.Type is NodeType.KlaAssay or NodeType.Periodic)) return [];
        var json = RecipeSerializer.Serialize(recipe);
        _autonomousRecipeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var frozen = RecipeSerializer.Deserialize(json);
        var plan = _autonomousWorkSource!.CreateWork(frozen, ExecutionId, Resources!);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Assays); ArgumentNullException.ThrowIfNull(plan.Schedules);
        foreach (var work in plan.Assays)
        {
            ArgumentNullException.ThrowIfNull(work); ArgumentNullException.ThrowIfNull(work.Execute);
            var node = frozen.Node(work.NodeId);
            if (node?.Type != NodeType.KlaAssay || !_klaWork.TryAdd(work.NodeId, work))
                throw new ArgumentException("Provedor retornou ensaio desconhecido ou duplicado.");
            var configuration = RecipeAutonomousBlockConfiguration.ReadKla(node);
            var capability = work.Capabilities;
            if (capability is null || !capability.IsIsolatedSimulation ||
                string.IsNullOrWhiteSpace(capability.InstallationId) || string.IsNullOrWhiteSpace(capability.EvidenceId) ||
                capability.ProfileId != configuration.ProfileId || capability.ProfileVersion != configuration.ProfileVersion ||
                capability.Protocols.IsDefaultOrEmpty || capability.Protocols.Any(p => !Enum.IsDefined(p)) ||
                capability.Protocols.Distinct().Count() != capability.Protocols.Length || !capability.Protocols.Contains(configuration.Protocol))
                throw new InvalidOperationException("Ensaio sem perfil isolado qualificado para a instalação e protocolo.");
            _klaFailurePolicies.Add(node.Id, configuration.FailurePolicy);
        }
        if (_klaWork.Count != frozen.Nodes.Count(n => n.Type == NodeType.KlaAssay))
            throw new ArgumentException("Provedor não resolveu todos os ensaios da receita.");
        var resolved = new List<RecipePeriodicWork>();
        foreach (var definition in plan.Schedules)
        {
            ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(definition.Record);
            var identity = definition.Identity; identity.Validate();
            var node = frozen.Node(identity.SchedulerNodeId);
            if (node?.Type != NodeType.Periodic || _graphPeriodicBindings.ContainsKey(node.Id))
                throw new ArgumentException("Provedor retornou agenda desconhecida ou duplicada.");
            var binding = RecipePeriodicTopology.ReadBinding(frozen, node);
            if (identity.TargetNodeId != binding.TargetNodeId || identity.CoordinatedCascadeNodeId != binding.CascadeNodeId ||
                identity.Schedule != RecipeAutonomousBlockConfiguration.ReadPeriodic(node).Schedule)
                throw new ArgumentException("Agenda do provedor diverge do grafo congelado.");
            _graphPeriodicBindings.Add(node.Id, binding);
            resolved.Add(new(identity, definition.DispatchTolerance,
                (invocation, token) => ExecuteKlaWorkAsync(Current!.Node(binding.TargetNodeId)!, invocation, token), definition.Record));
        }
        if (_graphPeriodicBindings.Count != frozen.Nodes.Count(n => n.Type == NodeType.Periodic))
            throw new ArgumentException("Provedor não resolveu todas as agendas da receita.");
        return resolved;
    }

    private void InitializeGraphPeriodicGroups(CancellationToken ct)
    {
        var work = _periodicWork.Where(w => _graphPeriodicBindings.ContainsKey(w.Identity.SchedulerNodeId));
        foreach (var members in work.GroupBy(w => w.Identity.CoordinatedCascadeNodeId ?? w.Identity.ScheduleRunId.ToString("N")))
        {
            var group = new RecipeCascadePeriodicGroup(members.ToArray(), _time, ct, paused: !_pauseGate.IsSet,
                startAtSchedulerEntry: true);
            lock (_lock)
            {
                foreach (var member in members) _graphSchedulerGroups.Add(member.Identity.SchedulerNodeId, group);
                if (members.First().Identity.CoordinatedCascadeNodeId is { } cascade) _periodicGroups.Add(cascade, group);
            }
        }
    }

    private async Task ExecutePeriodicNodeAsync(RecipeNode node, CancellationToken ct)
    {
        RecipeCascadePeriodicGroup group;
        lock (_lock) group = _graphSchedulerGroups.GetValueOrDefault(node.Id)
            ?? throw new InvalidOperationException("Agenda sem grupo de execução qualificado.");
        using var stopRegistration = ct.Register(group.RequestStop);
        await group.EnterSchedulerAsync(node.Id).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private async Task ExecuteKlaWorkAsync(RecipeNode node, PeriodicBlockInvocation? invocation, CancellationToken ct)
    {
        if (!_klaWork.TryGetValue(node.Id, out var work))
            throw new InvalidOperationException("Ensaio sem provedor qualificado.");
        SetNodeState(node.Id, NodeState.Evaluating);
        try
        {
            var result = await work.Execute(invocation, ct).ConfigureAwait(false);
            result = JsonSerializer.Deserialize<KlaRecipeResult>(JsonSerializer.Serialize(result))
                ?? throw new InvalidOperationException("Provedor não retornou resultado.");
            result.Validate();
            if (result.Context.RecipeRunId != ExecutionId || result.Context.NodeId != node.Id ||
                result.Context.RecipeSha256 != _autonomousRecipeHash || result.PeriodicInvocation != invocation)
                throw new InvalidOperationException("Resultado pertence a outra execução, bloco ou slot.");
            lock (_lock)
            {
                if (_autonomousResults.Any(r => r.Context.InvocationId == result.Context.InvocationId))
                    throw new InvalidOperationException("Invocação já apresentou resultado.");
                _autonomousResults.Add(result);
            }
            Log(RecipeLogSeverity.Info, $"Ensaio {result.Status}; sessão {result.SessionFolder}; " +
                $"retorno {(result.PreAssayStateRestored ? "confirmado" : "não confirmado")}; " +
                $"gravação {(result.PersistenceConfirmed ? "confirmada" : "não confirmada") }.", node.Id);
            ct.ThrowIfCancellationRequested();
            if (!result.PreAssayStateRestored || !result.PersistenceConfirmed ||
                result.Status is not (KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings) &&
                !(result.Status == KlaRecipeTerminalStatus.Inconclusive &&
                  _klaFailurePolicies[node.Id] == KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration))
                throw new InvalidOperationException(result.Reason ?? $"Ensaio terminou com {result.Status}.");
            SetNodeState(node.Id, NodeState.Completed);
            if (invocation is not null)
                MarkTraversed(Current!.Connections.Single(c => c.SourceNodeId == invocation.SchedulerNodeId && c.TargetNodeId == node.Id));
        }
        catch { SetNodeState(node.Id, NodeState.Error); throw; }
    }

    private async Task StopGraphPeriodicGroupsAsync()
    {
        RecipeCascadePeriodicGroup[] groups;
        lock (_lock) groups = _graphSchedulerGroups.Values.Distinct().ToArray();
        try { await Task.WhenAll(groups.Select(g => g.StopAndWaitAsync())).ConfigureAwait(false); }
        finally
        {
            foreach (var group in groups) group.Dispose();
            lock (_lock) { _graphSchedulerGroups.Clear(); _periodicGroups.Clear(); }
        }
    }
}
