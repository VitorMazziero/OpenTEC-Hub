using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

// Pumps: the dosing-pump blocks, re-targeted to the ESP32 dosing keys (§5.3.6). ReceitasOpenTEC drove
// these by clicking a VNC screen; here they are ordinary owned commands on the shared queue.
public sealed partial class RecipeEngine
{
    private void ExecutePump(RecipeNode node)
    {
        switch (node.Type)
        {
            case NodeType.PhPump:
                ExecutePhPump(node);
                break;

            case NodeType.AntifoamPump:
                ExecuteAntifoamPump(node);
                break;

            case NodeType.NutrientPump:
                ExecuteNutrientPump(node);
                break;

        }
    }

    private void ExecutePhPump(RecipeNode node)
    {
        var off = node.Enum<PumpManualAction>("acaoManual") == PumpManualAction.Off;
        var intensity = off ? 0.0 : node.Number("intensidade");

        // Maps to pHOperation/pHMix/pHIntensity. pH carries the x10 intensity quirk (see PHControl).
        var command = OpenTECCommand.Create()
            .Set(CommandKeys.PHOperation, node.Number("tempoLigadaS"))
            .Set(CommandKeys.PHMix, node.Number("tempoDesligadaS"))
            .Set(CommandKeys.PHIntensity, intensity * 10.0);

        Log(RecipeLogSeverity.Info,
            $"Bomba pH ({node.Text("bombaAlvo")}): {DescribePump(node, intensity)}.", node.Id);
        DispatchRecipe(command, node.Id);
    }

    private void ExecuteAntifoamPump(RecipeNode node)
    {
        var off = node.Enum<PumpManualAction>("acaoManual") == PumpManualAction.Off;
        var intensity = off ? 0.0 : node.Number("intensidade");

        // Maps to antifoamOperation/antifoamMix/antifoamIntensity (raw percent, no x10).
        var command = CommandBuilders.AntifoamControl(
            (int)node.Number("tempoLigadaS"), (int)node.Number("tempoDesligadaS"), intensity);

        Log(RecipeLogSeverity.Info, $"Bomba antiespuma: {DescribePump(node, intensity)}.", node.Id);
        DispatchRecipe(command, node.Id);
    }

    private void ExecuteNutrientPump(RecipeNode node)
    {
        // Cycle-only (the v2.x Dosagem mode was removed upstream). The block carries no intensity, so
        // On drives the pump at full and Off stops it; a single op/mix cycle is sent.
        var on = node.Enum<PumpManualAction>("acaoManual") == PumpManualAction.On
                 && node.Enum<PumpOperation>("operacao") != PumpOperation.ResetVolume;
        var intensity = on ? 99.0 : 0.0;

        var command = CommandBuilders.NutrientControl(
            (int)node.Number("tempoDosagemLigadaS"),
            (int)node.Number("tempoDosagemDesligadaS"),
            operationCycles: 1,
            mixCycles: 1,
            intensity);

        Log(RecipeLogSeverity.Info, $"Bomba de nutrientes: {(on ? "ligada" : "desligada")}.", node.Id);
        DispatchRecipe(command, node.Id);
    }

    private static string DescribePump(RecipeNode node, double intensity)
        => node.Enum<PumpOperation>("operacao") switch
        {
            PumpOperation.ResetVolume => "zerar volume",
            _ when intensity <= 0 => "desligada",
            _ => $"{intensity:0.##}% · {node.Number("tempoLigadaS"):0.##}s/{node.Number("tempoDesligadaS"):0.##}s",
        };
}
