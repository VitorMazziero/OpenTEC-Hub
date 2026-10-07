namespace OpenTECHub.Services.Recipes;

/// <summary>The target belongs to the schedule; it is not a normal-flow continuation.</summary>
public sealed record RecipePeriodicBinding(string SchedulerNodeId, string TargetNodeId, string? CascadeNodeId);

public static class RecipePeriodicTopology
{
    public static RecipePeriodicBinding ReadBinding(RecipeDocument recipe, RecipeNode periodic)
    {
        var configuration = RecipeAutonomousBlockConfiguration.ReadPeriodic(periodic);
        var outputs = recipe.Connections.Where(c => c.SourceNodeId == periodic.Id).ToArray();
        if (outputs.Length != 1 || outputs[0].SourceConnector != ConnectorNames.Out ||
            outputs[0].TargetConnector != ConnectorNames.In)
            throw new ArgumentException("A periodicidade exige exatamente um alvo ligado à sua Saída.");
        var target = recipe.Node(outputs[0].TargetNodeId);
        if (target?.Type != NodeType.KlaAssay)
            throw new ArgumentException("O alvo periódico deve ser Determinar kLa; outros alvos exigem contrato de execução qualificado.");
        if (recipe.IncomingTo(target.Id).Count() != 1)
            throw new ArgumentException("O alvo periódico deve pertencer exclusivamente a uma agenda.");
        if (recipe.Connections.Any(c => c.SourceNodeId == target.Id))
            throw new ArgumentException("O alvo periódico devolve o controle à agenda; conecte a continuação à cascata coordenada.");

        if (configuration.CoordinatedCascadeId is { } cascadeId)
        {
            if (recipe.Node(cascadeId)?.Type != NodeType.CascadeControl)
                throw new ArgumentException("A periodicidade deve estar vinculada a uma cascata existente.");
            if (NormalReachable(recipe, periodic.Id).Contains(cascadeId) ||
                NormalReachable(recipe, cascadeId).Contains(periodic.Id) ||
                !HasParallelFork(recipe, periodic.Id, cascadeId))
                throw new ArgumentException("A periodicidade e a cascata coordenada devem ocupar ramos paralelos sem depender do encerramento uma da outra.");
        }
        return new(periodic.Id, target.Id, configuration.CoordinatedCascadeId);
    }

    private static bool HasParallelFork(RecipeDocument recipe, string periodic, string cascade)
    {
        if (recipe.Start is not { } start) return false;
        var reachable = NormalReachable(recipe, start.Id);
        foreach (var fork in recipe.Nodes.Where(n => reachable.Contains(n.Id)))
        {
            var branches = NormalOutputs(recipe, fork.Id).Select(c =>
                NormalReachable(recipe, c.TargetNodeId)).ToArray();
            for (var i = 0; i < branches.Length; i++)
                for (var j = 0; j < branches.Length; j++)
                    if (i != j && branches[i].Contains(periodic) && !branches[i].Contains(cascade) &&
                        branches[j].Contains(cascade) && !branches[j].Contains(periodic)) return true;
        }
        return false;
    }

    private static IEnumerable<RecipeConnection> NormalOutputs(RecipeDocument recipe, string id)
        => recipe.Connections.Where(c => c.SourceNodeId == id &&
            !ConnectorNames.IsLoopOut(c.SourceConnector) && !ConnectorNames.IsLoopIn(c.TargetConnector));

    private static HashSet<string> NormalReachable(RecipeDocument recipe, string start)
    {
        var found = new HashSet<string>();
        var pending = new Stack<string>(); pending.Push(start);
        while (pending.TryPop(out var id))
        {
            if (!found.Add(id)) continue;
            foreach (var edge in NormalOutputs(recipe, id)) pending.Push(edge.TargetNodeId);
        }
        return found;
    }
}
