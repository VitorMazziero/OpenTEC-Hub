using System.Text.Json.Nodes;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;

namespace TecnalHub.Services.Recipes;

// Actuation: the setpoint and loop blocks, re-targeted from ReceitasTECNAL's Modbus writes to the
// ESP32 JSON wire in engineering units — no scale factors, no control word, no O2 enrichment.
public sealed partial class RecipeEngine
{
    private double MaxFlow => _settings.Current.Setpoints.MaxFlowLitresPerMinute;

    private void ExecuteActuation(RecipeNode node)
    {
        switch (node.Type)
        {
            case NodeType.SetSetpoint:
            {
                var variable = node.Enum<SetpointVariable>("variavel");
                var value = node.Number("valor");
                Log(RecipeLogSeverity.Info, $"Definir {Label(variable)} = {value:0.##}{UnitFor(variable)}.", node.Id);
                DispatchRecipe(BuildSetpoint(variable, value, node.Number("histerese")), node.Id);
                break;
            }

            case NodeType.MultiSetpoint:
            {
                // One combined command object — the protocol prefers it and it saves round trips.
                var combined = TecnalCommand.Create();
                foreach (var row in node.Rows("pontos").OfType<JsonObject>())
                {
                    if (Enum.TryParse<SetpointVariable>(row["variavel"]?.GetValue<string>(), out var variable))
                    {
                        var value = row["valor"] is JsonValue v && RecipeNode.TryReadNumber(v, out var d) ? d : 0.0;
                        var hyst = row["histerese"] is JsonValue h && RecipeNode.TryReadNumber(h, out var hv) ? hv : 0.0;
                        combined.Merge(BuildSetpoint(variable, value, hyst));
                        Log(RecipeLogSeverity.Info, $"Definir {Label(variable)} = {value:0.##}{UnitFor(variable)}.", node.Id);
                    }
                }

                DispatchRecipe(combined, node.Id);
                break;
            }

            case NodeType.SetLoop:
                ExecuteLoop(node, node.Enum<ControlLoop>("malha"), node.Enum<LoopOperation>("operacao"));
                break;

            case NodeType.MultiLoop:
                foreach (var row in node.Rows("controles").OfType<JsonObject>())
                {
                    if (Enum.TryParse<ControlLoop>(row["malha"]?.GetValue<string>(), out var loop) &&
                        Enum.TryParse<LoopOperation>(row["operacao"]?.GetValue<string>(), out var op))
                    {
                        ExecuteLoop(node, loop, op);
                    }
                }

                break;
        }
    }

    /// <summary>Builds the wire frame for one setpoint, in engineering units.</summary>
    private TecnalCommand BuildSetpoint(SetpointVariable variable, double value, double hysteresis) => variable switch
    {
        SetpointVariable.Temperature => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value),
        SetpointVariable.Agitation => CommandBuilders.MotorSetpoint((int)value),
        // O2 setpoint writes the monitor only; it does NOT engage the deferred enrichment path.
        SetpointVariable.Oxygen => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, value),
        SetpointVariable.Flow => CommandBuilders.FlowSetpoint(value, MaxFlow),
        SetpointVariable.Pressure => TecnalCommand.Create().Set(CommandKeys.PressureReference, value),
        // pH sets the reference and inactive band; the dosing pump timing/intensity is the Bomba pH block.
        SetpointVariable.Ph => TecnalCommand.Create().Set(CommandKeys.PHSetpoint, value).Set(CommandKeys.PHError, hysteresis),
        _ => TecnalCommand.Create(),
    };

    private void ExecuteLoop(RecipeNode node, ControlLoop loop, LoopOperation operation)
    {
        var enable = operation == LoopOperation.Enable;
        Log(RecipeLogSeverity.Info, $"{(enable ? "Ligar" : "Desligar")} malha {LoopLabel(loop)}.", node.Id);

        // Re-targeted: there is no Modbus control word. Enabling a loop is the subsystem's own
        // enable; disabling is its enable off or a zero setpoint/intensity.
        var command = loop switch
        {
            ControlLoop.Aeration => enable
                ? TecnalCommand.Create()
                : CommandBuilders.FlowSafeStop(MaxFlow),
            ControlLoop.Ph => enable ? TecnalCommand.Create() : TecnalCommand.Create().Set(CommandKeys.PHIntensity, 0.0),
            ControlLoop.Antifoam => enable ? TecnalCommand.Create() : TecnalCommand.Create().Set(CommandKeys.AntifoamIntensity, 0.0),
            ControlLoop.Nutrient => enable ? TecnalCommand.Create() : TecnalCommand.Create().Set(CommandKeys.NutriIntensity, 0.0),
            _ => TecnalCommand.Create(),
        };

        if (enable && loop is ControlLoop.Aeration or ControlLoop.Ph or ControlLoop.Antifoam or ControlLoop.Nutrient)
        {
            var guidance = loop == ControlLoop.Aeration
                ? "Defina a vazão pelo bloco de setpoint; o Hub v7 não roteia flowmeterComm."
                : "Configure a dosagem pelo bloco de bomba correspondente.";
            Log(RecipeLogSeverity.Info, guidance, node.Id);
        }

        DispatchRecipe(command, node.Id);
    }

    /// <summary>Sends a recipe-owned frame, logging a refusal (should not happen — the recipe owns all).</summary>
    private void DispatchRecipe(TecnalCommand command, string nodeId)
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
