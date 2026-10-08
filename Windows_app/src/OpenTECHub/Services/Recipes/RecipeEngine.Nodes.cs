using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

// Per-block execution: the dispatch switch plus the trigger and utility blocks. Actuation, pump and
// cascade blocks are handled in their own slices.
public sealed partial class RecipeEngine
{
    private async Task ExecuteNodeAsync(RecipeNode node, CancellationToken ct)
    {
        switch (node.Type)
        {
            case NodeType.LinearSetpointRamp:
                throw new InvalidOperationException("Rampa sem integração de reservas e confirmação.");
            case NodeType.KlaAssay:
                await ExecuteKlaWorkAsync(node, null, ct).ConfigureAwait(false);
                break;
            case NodeType.Periodic:
                await ExecutePeriodicNodeAsync(node, ct).ConfigureAwait(false);
                break;
            case NodeType.Start:
            case NodeType.End:
            case NodeType.And:
            case NodeType.Or:
                // Structural blocks: the flow walk does the work; the block itself is a marker.
                break;

            case NodeType.Timer:
                await ExecuteTimerAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.MonitorVariable:
                await ExecuteMonitorAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.ManualIntervention:
                await ExecuteManualInterventionAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.DataAcquisition:
                await ExecuteDataAcquisitionAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.LogEvent:
                Log(RecipeLogSeverity.Info, node.Text("mensagem"), node.Id);
                break;

            case NodeType.ResetVariables:
                // Destructive: the UI confirms before the recipe can include a live one.
                _arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.ResetVariables());
                Log(RecipeLogSeverity.Warning, "Variáveis do módulo zeradas (resetVariables).", node.Id);
                break;

            case NodeType.SetSetpoint:
            case NodeType.MultiSetpoint:
            case NodeType.SetLoop:
            case NodeType.MultiLoop:
                await ExecuteActuationAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.PhPump:
            case NodeType.AntifoamPump:
            case NodeType.NutrientPump:
                ExecutePump(node);
                break;

            // The Wi-Fi nodes are awaited, not fire-and-forget: each can be absent on its own.
            case NodeType.PumpControl:
                await ExecuteExternalPumpAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.BiomassSensor:
                await ExecuteBiomassAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.FlaskAgitator:
                await ExecuteFlaskAgitatorAsync(node, ct).ConfigureAwait(false);
                break;

            case NodeType.CascadeControl:
                await ExecuteCascadeAsync(node, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ExecuteTimerAsync(RecipeNode node, CancellationToken ct)
    {
        var seconds = DurationSeconds(node.Number("duracao"), node.Enum<TimeUnit>("unidade"));
        Log(RecipeLogSeverity.Info, $"Aguardando {node.Number("duracao"):0.##} {UnitLabel(node.Enum<TimeUnit>("unidade"))}.", node.Id);
        if (seconds > 0)
        {
            await _delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteMonitorAsync(RecipeNode node, CancellationToken ct)
    {
        var variable = node.Enum<MeasuredVariable>("variavel");
        var op = node.Enum<ComparisonOperator>("condicao");
        var timeoutMs = node.Number("tempoLimiteMs");
        var deadline = timeoutMs > 0 ? _time.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs) : (DateTimeOffset?)null;

        Log(RecipeLogSeverity.Info,
            $"Monitorando {variable} {SymbolFor(op)} {node.Number("valorAlvo"):0.##}.", node.Id);

        var consecutive = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Read parameters fresh each pass so a live edit (ApplyLiveTuning) takes effect.
            var target = node.Number("valorAlvo");
            if (_latest is { } snapshot && MeasuredValue(snapshot, variable) is { } value)
            {
                consecutive = Satisfies(value, op, target) ? consecutive + 1 : 0;
                if (consecutive >= Math.Max(1, (int)node.Number("confirmacoes")))
                {
                    Log(RecipeLogSeverity.Info, $"Condição atendida: {variable} = {value:0.##}.", node.Id);
                    return;
                }
            }

            if (deadline is { } d && _time.GetUtcNow() >= d)
            {
                Log(RecipeLogSeverity.Warning, "Tempo limite do monitoramento atingido; prosseguindo.", node.Id);
                return;
            }

            // Wake on the next telemetry frame, but no later than the poll interval, so a stalled
            // link still lets the timeout fire.
            var poll = TimeSpan.FromMilliseconds(Math.Max(1, node.Number("intervaloPollingMs")));
            await Task.WhenAny(WaitNextFrameAsync(ct), _delay(poll, ct)).ConfigureAwait(false);
        }
    }

    private async Task ExecuteManualInterventionAsync(RecipeNode node, CancellationToken ct)
    {
        if (node.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass)
        {
            return;
        }

        Log(RecipeLogSeverity.Info, "Em espera: intervenção manual (bloqueado).", node.Id);
        while (node.Enum<ManualGateOperation>("operacao") != ManualGateOperation.Pass)
        {
            ct.ThrowIfCancellationRequested();
            await _delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        Log(RecipeLogSeverity.Info, "Intervenção liberada; prosseguindo.", node.Id);
    }

    private async Task ExecuteDataAcquisitionAsync(RecipeNode node, CancellationToken ct)
    {
        if (node.Enum<AcquisitionMode>("modo") == AcquisitionMode.FixedTime)
        {
            var seconds = DurationSeconds(node.Number("duracao"), node.Enum<TimeUnit>("unidade"));
            Log(RecipeLogSeverity.Info, $"Início de aquisição de dados por {node.Number("duracao"):0.##} {UnitLabel(node.Enum<TimeUnit>("unidade"))}.", node.Id);
            if (seconds > 0)
            {
                await _delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }

            Log(RecipeLogSeverity.Info, "Fim da janela de aquisição de dados.", node.Id);
        }
        else
        {
            // Manual finalisation: mark the window open in the log and continue; without a per-block
            // stop control the block does not hold the flow.
            Log(RecipeLogSeverity.Info, "Janela de aquisição de dados marcada (finalização manual).", node.Id);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static double DurationSeconds(double value, TimeUnit unit) => unit switch
    {
        TimeUnit.Minutes => value * 60,
        TimeUnit.Hours => value * 3600,
        _ => value,
    };

    private static string UnitLabel(TimeUnit unit) => unit switch
    {
        TimeUnit.Minutes => "min",
        TimeUnit.Hours => "h",
        _ => "s",
    };

    private static string SymbolFor(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.GreaterOrEqual => "≥",
        ComparisonOperator.LessOrEqual => "≤",
        _ => "=",
    };

    private static double? MeasuredValue(SensorSnapshot snapshot, MeasuredVariable variable)
    {
        var raw = variable switch
        {
            MeasuredVariable.Temperature => snapshot.Temperature,
            MeasuredVariable.Ph => snapshot.PHCalibrated,
            MeasuredVariable.Oxygen => snapshot.OxygenCalibrated,
            MeasuredVariable.Pressure => snapshot.Pressure,
            MeasuredVariable.Flow => snapshot.FlowRate,
            MeasuredVariable.Level => snapshot.Distance,
            MeasuredVariable.Biomass => snapshot.BiomassAbsorbance,
            _ => SensorReadings.NotReceived,
        };

        return raw > SensorReadings.NotReceived ? raw : null;
    }

    private static bool Satisfies(double value, ComparisonOperator op, double target) => op switch
    {
        ComparisonOperator.GreaterThan => value > target,
        ComparisonOperator.LessThan => value < target,
        ComparisonOperator.GreaterOrEqual => value >= target,
        ComparisonOperator.LessOrEqual => value <= target,
        // Analog equality within a small tolerance — an exact double match on a noisy sensor
        // would essentially never fire.
        ComparisonOperator.Equal => Math.Abs(value - target) <= 0.1,
        _ => false,
    };
}
