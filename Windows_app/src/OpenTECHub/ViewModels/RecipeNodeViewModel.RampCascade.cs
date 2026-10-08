using System.Text.Json.Nodes;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.ViewModels;

public sealed partial class RecipeNodeViewModel
{
    public bool UsesRampCascadeReference => Type == NodeType.LinearSetpointRamp && Model.Rows("lines")
        .OfType<JsonObject>().Any(row => RecipeParameterFieldViewModel.GuardValue(row, "variable") == "Oxygen" &&
            RecipeParameterFieldViewModel.GuardValue(row, "oxygenTarget") == "ActiveCascadeReference");

    public IReadOnlyList<RecipeOption> AvailableRampCascades { get; private set; } = [];

    public RecipeOption? SelectedRampCascade
    {
        get => AvailableRampCascades.FirstOrDefault(choice => choice.Value == Model.Text("cascadeNodeId"));
        set
        {
            if (!UsesRampCascadeReference || value is null || !AvailableRampCascades.Any(choice => choice.Value == value.Value)) return;
            Model.Set("cascadeNodeId", value.Value);
            OnPropertyChanged();
            OnFieldChanged();
        }
    }

    public string RampCascadeStatus => SelectedRampCascade is not null
        ? "A rampa altera a referência deste Controle de O₂. Durante o teste de kLa, aguarda a devolução do controle para continuar."
        : AvailableRampCascades.Count == 0
            ? "Adicione um bloco Controle de O₂ à receita para usar a referência da cascata."
            : string.IsNullOrWhiteSpace(Model.Text("cascadeNodeId"))
                ? "Selecione o Controle de O₂ cuja referência será alterada pela rampa."
                : "O Controle de O₂ salvo não está nesta receita. Selecione um bloco disponível.";

    public void RefreshRampCascadeChoices(IEnumerable<RecipeNode> nodes)
    {
        if (Type != NodeType.LinearSetpointRamp) return;
        AvailableRampCascades = nodes.Where(node => node.Type == NodeType.CascadeControl)
            .Select((node, index) => new RecipeOption(node.Id, $"Controle de O₂ {index + 1} · referência {node.Number("spO2"):0.##}%"))
            .ToArray();
        OnPropertyChanged(nameof(AvailableRampCascades));
        OnPropertyChanged(nameof(SelectedRampCascade));
        OnPropertyChanged(nameof(RampCascadeStatus));
    }
}
