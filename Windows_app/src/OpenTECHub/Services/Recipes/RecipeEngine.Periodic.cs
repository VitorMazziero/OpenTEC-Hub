namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    private readonly IRecipePeriodicWorkSource? _periodicWorkSource;
    private IReadOnlyList<RecipePeriodicWork> _periodicWork = [];
    private readonly Dictionary<string, RecipeCascadePeriodicGroup> _periodicGroups = [];

    private void PreparePeriodicWork(RecipeDocument recipe)
    {
        _periodicWork = _periodicWorkSource?.CreateWork(recipe, ExecutionId, Resources).ToArray() ?? [];
        foreach (var work in _periodicWork)
        {
            work.Identity.Validate();
            ArgumentNullException.ThrowIfNull(work.Execute); ArgumentNullException.ThrowIfNull(work.Record);
            if (work.Identity.SlotIndex != 0 || work.Identity.CoordinatedCascadeNodeId is not { } cascade ||
                recipe.Node(cascade)?.Type != NodeType.CascadeControl || work.DispatchTolerance < TimeSpan.Zero ||
                work.DispatchTolerance.TotalSeconds >= work.Identity.Schedule.PeriodSeconds)
                throw new ArgumentException("Agenda precisa de cascata existente e tolerância qualificada.");
        }
        if (_periodicWork.Select(w => w.Identity.ScheduleRunId).Distinct().Count() != _periodicWork.Count ||
            _periodicWork.Select(w => w.Identity.SchedulerNodeId).Distinct().Count() != _periodicWork.Count)
            throw new ArgumentException("Agenda duplicada na execução da receita.");
    }

    private RecipeCascadePeriodicGroup? StartPeriodicGroup(string cascadeId, CancellationToken ct)
    {
        var work = _periodicWork.Where(w => w.Identity.CoordinatedCascadeNodeId == cascadeId).ToArray();
        if (work.Length == 0) return null;
        lock (_lock)
        {
            var group = new RecipeCascadePeriodicGroup(work, _time, ct, paused: !_pauseGate.IsSet);
            _periodicGroups.Add(cascadeId, group);
            return group;
        }
    }

    private void PausePeriodicGroups(bool paused)
    {
        RecipeCascadePeriodicGroup[] groups;
        lock (_lock) groups = _periodicGroups.Values.ToArray();
        foreach (var group in groups) group.SetPaused(paused);
    }
}
