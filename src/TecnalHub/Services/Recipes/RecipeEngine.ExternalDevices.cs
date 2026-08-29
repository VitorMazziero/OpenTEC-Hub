using System.Globalization;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;

namespace TecnalHub.Services.Recipes;

// The three Wi-Fi nodes behind the Hub: the external feed pump, the biomass optical sensor and
// the flask agitator.
//
// What separates these from the dosing pumps is that they can be absent on their own — they are
// separate ESP32s on the Hub's SoftAP, not hardware inside the module — so every block here
// dispatches and then holds until the device confirms, on the same mechanism the flowmeter
// already used. And two of them carry a firmware ordering constraint that the block has to
// honour, because the Hub drops a device's sub-commands the moment its routing flag is clear.
public sealed partial class RecipeEngine
{
    /// <summary>How close the echoed agitator magnitude must be to count as applied, in percent.</summary>
    /// <remarks>The node quantises through a 12-bit PWM and its own minimum-drive floor.</remarks>
    private const double AgitatorEchoTolerancePercent = 2.0;

    // ── External feed pump ───────────────────────────────────────────────────

    private async Task ExecuteExternalPumpAsync(RecipeNode node, CancellationToken ct)
    {
        switch (node.Enum<ExternalPumpAction>("acao"))
        {
            case ExternalPumpAction.Enable:
                Log(RecipeLogSeverity.Info, "Bomba externa: ativando o roteamento do Hub.", node.Id);
                DispatchRecipe(CommandBuilders.PumpEnable(), node.Id);
                await AwaitPumpRoutingAsync(node, enabled: true, ct).ConfigureAwait(false);
                break;

            case ExternalPumpAction.SendProfile:
                await SendPumpProfileAsync(node, ct).ConfigureAwait(false);
                break;

            case ExternalPumpAction.Stop:
                // Two frames, in this order. A combined {"pumpComm":0,"mode":0} does not stop the
                // pump: the Hub parses the routing flag before it reaches the pump block and then
                // drops its own mode:0, so the node keeps dosing and only its telemetry goes quiet.
                Log(RecipeLogSeverity.Info, "Bomba externa: parando o perfil e desativando o roteamento.", node.Id);
                DispatchRecipe(CommandBuilders.PumpStopProfile(), node.Id);
                DispatchRecipeSeparateFrame(CommandBuilders.PumpRoutingDisabled(), node.Id);
                await AwaitPumpRoutingAsync(node, enabled: false, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendPumpProfileAsync(RecipeNode node, CancellationToken ct)
    {
        var mode = node.Enum<PumpProfileMode>("modo");
        var spec = new PumpProfileSpec(
            mode,
            node.Number("inicioMin"),
            node.Number("fimMin"),
            node.Number("lambda"),
            node.Number("phi"),
            ParseList(node.Text("coeficientes")),
            ParseList(node.Text("tempos")),
            ParseList(node.Text("vazoes")));

        TecnalCommand command;
        try
        {
            // Same builder the manual card uses. Two profile builders would eventually disagree,
            // and the one that disagreed would be the one nobody was watching.
            command = PumpProfileMath.BuildCommand(spec);
        }
        catch (ArgumentException ex)
        {
            // The validator catches this before a run starts; this covers a hand-edited file.
            Log(RecipeLogSeverity.Error, $"Perfil da bomba externa inválido: {ex.Message}", node.Id);
            return;
        }

        Log(RecipeLogSeverity.Info,
            $"Bomba externa: perfil {PumpModeLabel(mode)} de {spec.InitMinutes:0.##} a {spec.FinalMinutes:0.##} min.",
            node.Id);
        DispatchRecipe(command, node.Id);
        await AwaitPumpProfileAsync(node, mode, ct).ConfigureAwait(false);
    }

    // ── Biomass optical sensor ───────────────────────────────────────────────

    private async Task ExecuteBiomassAsync(RecipeNode node, CancellationToken ct)
    {
        switch (node.Enum<BiomassAction>("acao"))
        {
            case BiomassAction.Enable:
                Log(RecipeLogSeverity.Info, "Biomassa: ativando o roteamento do Hub.", node.Id);
                DispatchRecipe(CommandBuilders.BiomassComm(true), node.Id);
                await AwaitBiomassRoutingAsync(node, enabled: true, ct).ConfigureAwait(false);
                break;

            case BiomassAction.Blank:
                // No observable confirmation: the node blanks for ~15 s and publishes nothing that
                // distinguishes a fresh reference from the previous one. The block does not hold,
                // and a recipe that needs the sweep to finish should follow this with a timer.
                Log(RecipeLogSeverity.Info,
                    "Biomassa: capturando o branco. A varredura leva cerca de 15 s e não é confirmada " +
                    "pela telemetria — use um temporizador antes de iniciar a aquisição.",
                    node.Id);
                DispatchRecipe(CommandBuilders.BiomassBlank(), node.Id);
                break;

            case BiomassAction.Start:
                Log(RecipeLogSeverity.Info, "Biomassa: iniciando a aquisição.", node.Id);
                DispatchRecipe(CommandBuilders.BiomassStart(), node.Id);
                await AwaitBiomassMeasuringAsync(node, ct).ConfigureAwait(false);
                break;

            case BiomassAction.Stop:
                // A stop never waits, for the same reason a zero flow setpoint never waits: an
                // operator cutting something off must not be held up by the device that is failing
                // to answer. Confirming a stop would also take a full sample window to observe.
                Log(RecipeLogSeverity.Info, "Biomassa: parando a aquisição.", node.Id);
                DispatchRecipe(CommandBuilders.BiomassStop(), node.Id);
                break;

            case BiomassAction.Thresholds:
                var low = (int)node.Number("limiarBaixo");
                var high = (int)node.Number("limiarAlto");
                var optimal = (int)node.Number("limiarOtimo");
                Log(RecipeLogSeverity.Info,
                    $"Biomassa: limiares de integração {low}/{optimal}/{high} contagens.", node.Id);
                DispatchRecipe(CommandBuilders.BiomassThresholds(low, high, optimal), node.Id);
                break;

            case BiomassAction.Disable:
                // Stop first, while the Hub is still routing. The same frame carrying biomassComm:0
                // would have its stop discarded and the node would keep acquiring.
                Log(RecipeLogSeverity.Info, "Biomassa: parando a aquisição e desativando o roteamento.", node.Id);
                DispatchRecipe(CommandBuilders.BiomassStop(), node.Id);
                DispatchRecipeSeparateFrame(CommandBuilders.BiomassComm(false), node.Id);
                await AwaitBiomassRoutingAsync(node, enabled: false, ct).ConfigureAwait(false);
                break;
        }
    }

    // ── Flask agitator ───────────────────────────────────────────────────────

    private async Task ExecuteFlaskAgitatorAsync(RecipeNode node, CancellationToken ct)
    {
        var magnitude = Math.Clamp(node.Number("intensidade"), 0.0, 100.0);
        var clockwise = node.Enum<AgitatorDirection>("sentido") == AgitatorDirection.Clockwise;
        var signed = clockwise ? magnitude : -magnitude;

        if (node.Enum<FlaskAgitatorAction>("acao") == FlaskAgitatorAction.Stop)
        {
            // Deliberately the safe-stop shape, which also sends agitatorReEnablePot:0. An ordinary
            // stop hands the motor back to the bench knob, so a recipe using it would both fail to
            // stop the agitator and then hold forever waiting for a zero that never arrives.
            Log(RecipeLogSeverity.Info,
                "Agitador de frasco: parando e bloqueando o potenciômetro de bancada.", node.Id);
            DispatchRecipe(CommandBuilders.FlaskAgitatorSafeStop(signed), node.Id);
            await AwaitAgitatorAppliedAsync(node, 0.0, ct).ConfigureAwait(false);
            return;
        }

        var automatic = node.Flag("automatico");
        Log(RecipeLogSeverity.Info,
            $"Agitador de frasco: {magnitude:0.#}% no sentido {(clockwise ? "horário" : "anti-horário")}" +
            $"{(automatic ? ", modo automático" : "")}.",
            node.Id);
        DispatchRecipe(CommandBuilders.FlaskAgitator(on: true, automatic, signed), node.Id);
        await AwaitAgitatorAppliedAsync(node, magnitude, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a frame that must not be merged with what is already buffered.
    /// </summary>
    /// <remarks>
    /// The outgoing buffer merges by design — several setpoints in one gesture leave as one frame.
    /// That is wrong for the two disable sequences here, where the Hub's parse order makes the
    /// routing flag and the stop interact.
    /// </remarks>
    private void DispatchRecipeSeparateFrame(TecnalCommand command, string nodeId)
    {
        if (command.IsEmpty)
        {
            return;
        }

        var result = _arbiter.DispatchSeparateFrame(CommandOwner.Recipe, command);
        if (!result.Accepted)
        {
            Log(RecipeLogSeverity.Warning, "Comando recusado pelo árbitro (posse de atuador perdida).", nodeId);
        }
    }

    /// <summary>Reads a comma or semicolon separated numeric list, invariant.</summary>
    private static IReadOnlyList<double> ParseList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<double> values = [];
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Accept the comma decimal separator an operator on a pt-BR keyboard will type, but
            // only when it is not already doing duty as the list separator.
            if (double.TryParse(part.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string PumpModeLabel(PumpProfileMode mode) => mode switch
    {
        PumpProfileMode.Constant => "constante",
        PumpProfileMode.Linear => "linear",
        PumpProfileMode.Exponential => "exponencial",
        PumpProfileMode.Polynomial => "polinomial",
        PumpProfileMode.Piecewise => "por segmentos",
        _ => mode.ToString(),
    };
}
