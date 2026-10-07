using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.ViewModels;

public sealed record RecipeAutomaticSessionViewModel(KlaRecipeResult Result)
{
    public string Origin => $"Bloco {Result.Context.NodeId} · cultivo {Result.Context.CultivationId}" +
        (Result.PeriodicInvocation is { } slot ? $" · disparo {slot.SlotIndex + 1}" : "");
    public string Status => Result.Status switch
    {
        KlaRecipeTerminalStatus.Completed => "Concluído",
        KlaRecipeTerminalStatus.CompletedWithWarnings => "Concluído com ressalvas",
        KlaRecipeTerminalStatus.Inconclusive => "Inconclusivo",
        KlaRecipeTerminalStatus.Cancelled => "Cancelado",
        KlaRecipeTerminalStatus.PersistenceFailure => "Falha de gravação",
        KlaRecipeTerminalStatus.RestorationFailure => "Falha de restauração",
        _ => "Falha operacional"
    };
    public string Summary => $"{Status} · {Result.Attempts.Length} tentativa(s) · " +
        $"{Result.Attempts.Count(a => a.Decision == KlaAutomaticDecision.Selected)} selecionada(s)";
    public string ReturnAndSave => $"Retorno: {(Result.PreAssayStateRestored ? "confirmado" : "não confirmado")} · " +
        $"gravação: {(Result.PersistenceConfirmed ? "confirmada" : "não confirmada")}";
    public string Folder => Result.SessionFolder;
    public string Reason => Result.Reason ?? "";
}

public sealed partial class ReceitasViewModel
{
    public ObservableCollection<RecipeAutomaticSessionViewModel> AutomaticSessions { get; } = [];
    public bool HasAutomaticSessions => AutomaticSessions.Count > 0;
    public event Action<string>? OpenAutomaticSessionRequested;

    internal void RefreshAutomaticSessions()
    {
        var results = _engine.AutonomousResults;
        var ids = results.Select(r => r.Context.InvocationId).ToHashSet();
        foreach (var stale in AutomaticSessions.Where(s => !ids.Contains(s.Result.Context.InvocationId)).ToArray())
            AutomaticSessions.Remove(stale);
        foreach (var result in results)
        {
            // Engine results are frozen terminal records. Never re-open a live manifest here.
            if (AutomaticSessions.Any(s => s.Result.Context.InvocationId == result.Context.InvocationId)) continue;
            AutomaticSessions.Add(new(result));
        }
        OnPropertyChanged(nameof(HasAutomaticSessions));
    }

    [RelayCommand]
    private void OpenAutomaticSession(RecipeAutomaticSessionViewModel? session)
    {
        if (session is null || !AutomaticSessions.Contains(session)) return;
        OpenAutomaticSessionRequested?.Invoke(session.Folder);
    }
}
