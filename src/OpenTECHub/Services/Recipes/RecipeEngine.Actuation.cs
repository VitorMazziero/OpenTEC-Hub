using System.Text.Json.Nodes;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

// Actuation: the setpoint and loop blocks, re-targeted from ReceitasOpenTEC's Modbus writes to the
// ESP32 JSON wire in engineering units — no scale factors, no control word, no O2 enrichment.
public sealed partial class RecipeEngine
{
    private double MaxFlow => _settings.Current.Setpoints.MaxFlowLitresPerMinute;

    /// <summary>
    /// Runs an actuation block. Blocks that command an external device hold until that device
    /// confirms — see <see cref="AwaitDeviceAsync"/>; the rest are fire-and-forget as before.
    /// </summary>
    private async Task ExecuteActuationAsync(RecipeNode node, CancellationToken ct)
    {
        switch (node.Type)
        {
            case NodeType.SetSetpoint:
            {
                var variable = node.Enum<SetpointVariable>("variavel");
                var value = node.Number("valor");
                Log(RecipeLogSeverity.Info, $"Definir {Label(variable)} = {value:0.##}{UnitFor(variable)}.", node.Id);
                DispatchRecipe(BuildSetpoint(variable, value, node.Number("histerese")), node.Id);
                if (variable == SetpointVariable.Flow)
                {
                    await AwaitFlowAppliedAsync(node, value, ct).ConfigureAwait(false);
                }

                break;
            }

            case NodeType.MultiSetpoint:
            {
                // One combined command object — the protocol prefers it and it saves round trips.
                var combined = OpenTECCommand.Create();
                double? flowTarget = null;
                foreach (var row in node.Rows("pontos").OfType<JsonObject>())
                {
                    if (Enum.TryParse<SetpointVariable>(row["variavel"]?.GetValue<string>(), out var variable))
                    {
                        var value = row["valor"] is JsonValue v && RecipeNode.TryReadNumber(v, out var d) ? d : 0.0;
                        var hyst = row["histerese"] is JsonValue h && RecipeNode.TryReadNumber(h, out var hv) ? hv : 0.0;
                        combined.Merge(BuildSetpoint(variable, value, hyst));
                        Log(RecipeLogSeverity.Info, $"Definir {Label(variable)} = {value:0.##}{UnitFor(variable)}.", node.Id);
                        if (variable == SetpointVariable.Flow)
                        {
                            flowTarget = value;
                        }
                    }
                }

                DispatchRecipe(combined, node.Id);
                if (flowTarget is { } target)
                {
                    await AwaitFlowAppliedAsync(node, target, ct).ConfigureAwait(false);
                }

                break;
            }

            case NodeType.SetLoop:
                await ExecuteLoopAsync(node, node.Enum<ControlLoop>("malha"), node.Enum<LoopOperation>("operacao"), ct)
                    .ConfigureAwait(false);
                break;

            case NodeType.MultiLoop:
                foreach (var row in node.Rows("controles").OfType<JsonObject>())
                {
                    if (Enum.TryParse<ControlLoop>(row["malha"]?.GetValue<string>(), out var loop) &&
                        Enum.TryParse<LoopOperation>(row["operacao"]?.GetValue<string>(), out var op))
                    {
                        await ExecuteLoopAsync(node, loop, op, ct).ConfigureAwait(false);
                    }
                }

                break;
        }
    }

    /// <summary>Builds the wire frame for one setpoint, in engineering units.</summary>
    private OpenTECCommand BuildSetpoint(SetpointVariable variable, double value, double hysteresis) => variable switch
    {
        SetpointVariable.Temperature => OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, value),
        SetpointVariable.Agitation => CommandBuilders.MotorSetpoint((int)value),
        // O2 setpoint writes the monitor only; it does NOT engage the deferred enrichment path.
        SetpointVariable.Oxygen => OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, value),
        SetpointVariable.Flow => CommandBuilders.FlowSetpoint(value, MaxFlow),
        SetpointVariable.Pressure => OpenTECCommand.Create().Set(CommandKeys.PressureReference, value),
        // pH sets the reference and inactive band; the dosing pump timing/intensity is the Bomba pH block.
        SetpointVariable.Ph => OpenTECCommand.Create().Set(CommandKeys.PHSetpoint, value).Set(CommandKeys.PHError, hysteresis),
        _ => OpenTECCommand.Create(),
    };

    private async Task ExecuteLoopAsync(RecipeNode node, ControlLoop loop, LoopOperation operation, CancellationToken ct)
    {
        var enable = operation == LoopOperation.Enable;
        Log(RecipeLogSeverity.Info, $"{(enable ? "Ligar" : "Desligar")} malha {LoopLabel(loop)}.", node.Id);

        // Re-targeted: there is no Modbus control word. Enabling a loop is the subsystem's own
        // enable; disabling is its enable off or a zero setpoint/intensity.
        var command = loop switch
        {
            // The aeration loop switch is the Hub's flow-loop flag; the setpoint itself rides in
            // the setpoint block. Disabling also safe-stops, so the gas actually stops.
            ControlLoop.Aeration => enable
                ? CommandBuilders.FlowmeterLoopEnabled(true)
                : CommandBuilders.FlowSafeStop(MaxFlow).Merge(CommandBuilders.FlowmeterLoopEnabled(false)),
            ControlLoop.Ph => enable ? OpenTECCommand.Create() : OpenTECCommand.Create().Set(CommandKeys.PHIntensity, 0.0),
            ControlLoop.Antifoam => enable ? OpenTECCommand.Create() : OpenTECCommand.Create().Set(CommandKeys.AntifoamIntensity, 0.0),
            ControlLoop.Nutrient => enable ? OpenTECCommand.Create() : OpenTECCommand.Create().Set(CommandKeys.NutriIntensity, 0.0),
            _ => OpenTECCommand.Create(),
        };

        if (enable && loop is ControlLoop.Aeration or ControlLoop.Ph or ControlLoop.Antifoam or ControlLoop.Nutrient)
        {
            var guidance = loop == ControlLoop.Aeration
                ? "A vazão em si é definida pelo bloco de setpoint."
                : "Configure a dosagem pelo bloco de bomba correspondente.";
            Log(RecipeLogSeverity.Info, guidance, node.Id);
        }

        DispatchRecipe(command, node.Id);

        // Enabling the aeration loop is confirmed by the Hub echoing the flag back; disabling is a
        // safe-stop, which must never wait on the device it is trying to stop.
        if (enable && loop == ControlLoop.Aeration)
        {
            await AwaitFlowLoopEnabledAsync(node, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a recipe-owned frame, logging a refusal (should not happen — the recipe owns all).</summary>
    private void DispatchRecipe(OpenTECCommand command, string nodeId)
    {
        if (command.IsEmpty)
        {
            return;
        }

        var result = _arbiter.Dispatch(CommandOwner.Recipe, command);
        if (!result.Accepted)
        {
            Log(RecipeLogSeverity.Warning, "Comando recusado pelo árbitro (posse de atuador perdida).", nodeId);
        }
    }

    private static string Label(SetpointVariable variable) => variable switch
    {
        SetpointVariable.Temperature => "temperatura",
        SetpointVariable.Agitation => "agitação",
        SetpointVariable.Oxygen => "O₂",
        SetpointVariable.Flow => "vazão",
        SetpointVariable.Pressure => "pressão",
        SetpointVariable.Ph => "pH",
        _ => variable.ToString(),
    };

    private static string UnitFor(SetpointVariable variable) => variable switch
    {
        SetpointVariable.Temperature => " °C",
        SetpointVariable.Agitation => " rpm",
        SetpointVariable.Oxygen => " %",
        SetpointVariable.Flow => " L/min",
        SetpointVariable.Pressure => " kPa",
        _ => "",
    };

    private static string LoopLabel(ControlLoop loop) => loop switch
    {
        ControlLoop.Aeration => "aeração",
        ControlLoop.Ph => "pH",
        ControlLoop.Antifoam => "antiespuma",
        ControlLoop.Nutrient => "nutrientes",
        _ => loop.ToString(),
    };
}
