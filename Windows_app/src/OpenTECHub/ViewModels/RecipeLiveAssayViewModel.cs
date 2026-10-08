using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.ViewModels;

public sealed record RecipeLiveAssayViewModel(KlaRecipeProgress Progress)
{
    public string Origin => $"Bloco {Progress.Context.NodeId} · cultivo {Progress.Context.CultivationId}" +
        (Progress.PeriodicInvocation is { } slot ? $" · disparo {slot.SlotIndex + 1}" : "");
    public string Protocol => Progress.Protocol == KlaAssayProtocol.Biotic ? "kLa biótico · OUR avaliado ao final" : "kLa abiótico · OUR não aplicável";
    public string Stage => Progress.Stage switch
    {
        KlaRecipeProgressStage.WaitingCultivationInterval => "Aguardando intervalo do cultivo",
        KlaRecipeProgressStage.WaitingBetweenAttempts => "Aguardando intervalo entre tentativas",
        KlaRecipeProgressStage.ReservingResources => "Aguardando os atuadores e a suspensão dos controles",
        KlaRecipeProgressStage.Recovering => "Restaurando o estado anterior · retorno ainda em verificação",
        KlaRecipeProgressStage.RecordingDecision => "Confirmando a gravação da tentativa e da decisão",
        KlaRecipeProgressStage.RecordingResult => "Confirmando a gravação do resultado da invocação",
        KlaRecipeProgressStage.Paused => "Ensaio pausado; controle devolvido ao cultivo",
        KlaRecipeProgressStage.Acquiring => PhaseText(Progress.Phase),
        _ => "Preparando o ensaio"
    };
    public string Attempt => Progress.Item is { } item ? $"Réplica {item.ReplicateNumber} · tentativa {item.AttemptNumber}" : "";
    public string Condition => Progress.Condition is { } condition
        ? $"Condição {condition.OrderIndex + 1}: {condition.AgitationRpm:N0} rpm · {condition.AirflowLpm:N2} L/min" : "";
    public string Counts => $"{Progress.FinishedAttempts} tentativa(s) com decisão gravada · {Progress.SelectedAttempts} selecionada(s)";
    public string Time => $"Tempo do bloco: {Progress.ElapsedBlockSeconds:N0} s · restante: {Progress.RemainingBlockSeconds:N0} s";
    public string Budget => Progress.CultivationBudget is { } budget
        ? $"Cultivo: {budget.RemainingAttempts} tentativa(s) restantes · {budget.RemainingRemovalSeconds:N1} s de exposição disponíveis"
        : "Orçamento do cultivo ainda não observado";
    public string BudgetDetail => Progress.BudgetObservationError is { } error ? $"Orçamento indisponível: {error}" :
        Progress.CultivationBudget?.BlockedReason is { } blocked ? $"Bloqueio para novas tentativas: {blocked}" :
        Progress.CultivationBudget is { WaitSeconds: > 0 } budget ? $"Intervalo restante: {budget.WaitSeconds:N1} s" : "";
    public string BudgetObservation => Progress.BudgetObservedUtc is { } observed
        ? $"Orçamento observado às {observed.ToLocalTime():HH:mm:ss}" : "";
    public string Profile => $"Perfil {Progress.ProfileId} · {Progress.ProfileVersion}";
    public string Folder => Progress.SessionFolder ?? "Sessão ainda não criada";

    private static string PhaseText(RunPhase? phase) => phase switch
    {
        RunPhase.ClosingAllGas => "Fechando as linhas de gás",
        RunPhase.OpeningNitrogen => "Confirmando a linha de N₂",
        RunPhase.Deoxygenating => "Desoxigenando",
        RunPhase.PrestagingAir => "Estabilizando a vazão de ar",
        RunPhase.SwitchingToReactor => "Confirmando a rota de ar ao reator",
        RunPhase.Reoxygenating => "Medindo a reoxigenação",
        RunPhase.DivertingAir => "Desviando o ar",
        RunPhase.MeasuringConsumption => "Medindo o consumo de oxigênio",
        RunPhase.RestoringCultivation => "Restaurando o estado anterior · retorno ainda em verificação",
        RunPhase.Reviewing or RunPhase.StoppingRun => "Concluindo a aquisição",
        _ => "Preparando e aguardando a observação inicial"
    };
}
