using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.ViewModels;

/// <summary>Displays the saved policy decision, independently of any later scientific revision.</summary>
public sealed record KlaRecordedAttemptViewModel(KlaTestRunSummary Run, KlaAssayProtocol Protocol)
{
    public string Identity => $"R{Run.ReplicateNumber} · tentativa {Run.AttemptNumber} · {Run.AgitationRpm:0} rpm · {Run.AirflowLpm:0.##} L/min";
    public string Decision => Run.AutomaticDecision?.Decision switch
    {
        KlaAutomaticDecision.Selected => "Selecionada automaticamente",
        KlaAutomaticDecision.Retry => "Não selecionada · nova tentativa autorizada",
        KlaAutomaticDecision.NotSelected => "Não selecionada · limite ou qualidade insuficiente",
        KlaAutomaticDecision.Aborted => "Interrompida",
        _ => "Sem decisão automática confirmada"
    };
    public string Kla => $"kLa: {Number(Run.AutomaticDecision is { } saved ? saved.KlaPerHour : Run.KlaPerHour)} h⁻¹ · " +
        Quality(Run.AutomaticDecision?.KlaQuality ?? Run.EffectiveOutcome.KlaQuality);
    public string Our => Protocol == KlaAssayProtocol.Abiotic ? "OUR: não aplicável" :
        $"OUR: {Number(Run.AutomaticDecision?.OurPercentPointsPerHour)} pp/h · " +
        Quality(Run.AutomaticDecision?.OurQuality ?? Run.EffectiveOutcome.OurQuality) +
        (Run.AutomaticDecision?.OurMmolPerLPerHour is { } molar ? $" · {Number(molar)} mmol/L/h" : " · concentração indisponível sem metadados");
    public string Restoration => (Run.AutomaticDecision?.Restoration ?? Run.EffectiveOutcome.Restoration) switch
    {
        KlaRestorationState.Confirmed => "Estado anterior restaurado",
        KlaRestorationState.Failed => "Falha de restauração",
        KlaRestorationState.Pending => "Restauração pendente",
        _ => "Restauração não confirmada para receita"
    };
    public string Authorship => Run.AutomaticDecision is { } decision
        ? $"Autoria: política automática · versão {decision.PolicyVersion} · " +
            (decision.PersistenceConfirmed ? "decisão gravada" : "gravação não confirmada")
        : "Autoria automática ainda não registrada";
    public string Reasons => string.Join(" · ", Run.AutomaticDecision?.ReasonCodes ?? []);
    public string SavedFolder => Run.FolderName;
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
    private static string Quality(KlaScientificQuality quality) => quality switch
    {
        KlaScientificQuality.Valid => "válido", KlaScientificQuality.Conditional => "condicional",
        KlaScientificQuality.Inconclusive => "inconclusivo", KlaScientificQuality.NotApplicable => "não aplicável",
        _ => "não avaliado"
    };
}

public sealed partial class KlaDeterminationViewModel
{
    partial void OnCurrentTestChanged(KlaTestDocument? value) => RefreshAutomaticResults();
    public bool IsAutomaticSession => CurrentTest?.RecipeRequest is not null || CurrentTest?.Runs.Any(r => r.AutomaticDecision is not null) == true;
    public ObservableCollection<KlaRecordedAttemptViewModel> RecordedAutomaticAttempts { get; } = [];
    public string AutomaticSessionSummary => CurrentTest?.RecipeRequest is { } request
        ? $"Execução automática da receita · bloco {request.Context.NodeId} · cultivo {request.Context.CultivationId} · " +
            $"perfil {request.Quality.ProfileId} · {request.Quality.Version}" +
            (request.PeriodicInvocation is { } slot ? $" · disparo {slot.SlotIndex + 1}" : "")
        : "Histórico de decisões automáticas. A origem completa da receita não foi registrada nesta sessão.";
    public string AutomaticTerminalStatus { get; private set; } = "Resultado terminal ainda não confirmado";

    private void RefreshAutomaticResults()
    {
        RecordedAutomaticAttempts.Clear();
        if (IsAutomaticSession && CurrentTest is { } document)
        {
            foreach (var run in document.Runs.OrderBy(r => r.StartedUtc).ThenBy(r => r.AttemptNumber))
                RecordedAutomaticAttempts.Add(new(run, document.EffectiveProtocol));
            AutomaticTerminalStatus = "Resultado terminal ainda não confirmado";
            if (document.RecipeRequest is { } request)
            {
                try
                {
                    var result = _store.ReadRecipeResult(document.FolderName, request.Context.InvocationId);
                    if (result is not null)
                        AutomaticTerminalStatus = $"{TerminalText(result.Status)} · " +
                            (result.PreAssayStateRestored ? "estado anterior restaurado" : "restauração não confirmada") + " · " +
                            (result.PersistenceConfirmed ? "resultado gravado" : "gravação não confirmada") +
                            (string.IsNullOrWhiteSpace(result.Reason) ? "" : $" · {result.Reason}");
                }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
                { AutomaticTerminalStatus = $"Resultado terminal não verificado: {error.Message}"; }
            }
        }
        OnPropertyChanged(nameof(IsAutomaticSession)); OnPropertyChanged(nameof(AutomaticSessionSummary));
        OnPropertyChanged(nameof(AutomaticTerminalStatus));
    }

    private static string TerminalText(KlaRecipeTerminalStatus status) => status switch
    {
        KlaRecipeTerminalStatus.Completed => "Concluído",
        KlaRecipeTerminalStatus.CompletedWithWarnings => "Concluído com ressalvas",
        KlaRecipeTerminalStatus.Inconclusive => "Inconclusivo",
        KlaRecipeTerminalStatus.Cancelled => "Cancelado",
        KlaRecipeTerminalStatus.OperationalFailure => "Falha operacional",
        KlaRecipeTerminalStatus.PersistenceFailure => "Falha de gravação",
        KlaRecipeTerminalStatus.RestorationFailure => "Falha de restauração",
        _ => "Estado desconhecido"
    };

    [RelayCommand]
    public void LoadRecordedAutomaticAttempt(KlaRecordedAttemptViewModel? attempt)
    {
        if (attempt is null || CurrentTest is null || IsRunning || !RecordedAutomaticAttempts.Contains(attempt)) return;
        LoadStoredRun(attempt.Run, null);
    }

    [RelayCommand]
    public void ExportAutomaticAttempts()
    {
        if (!IsAutomaticSession || CurrentTest is null) return;
        var path = _files.ChooseSavePath("Exportar tentativas automáticas", "resumo-receita-tentativas.csv", "CSV (*.csv)|*.csv", ".csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, KlaAutomaticResultsSummary.Format(CurrentTest));
            StatusMessage = $"Resumo das tentativas exportado: {path}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { StatusMessage = $"Falha ao exportar tentativas: {error.Message}"; }
    }
}
