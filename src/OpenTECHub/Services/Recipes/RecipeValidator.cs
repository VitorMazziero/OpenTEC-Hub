namespace OpenTECHub.Services.Recipes;

/// <summary>
/// The setpoint device envelopes, matched to the manual setpoint fields and wire builders.
/// </summary>
/// <remarks>
/// The ranges mirror <c>ShellViewModel.Subsystems</c> / <c>docs/PROTOCOL.md</c> §3.1, so a recipe
/// cannot accept a value the device would reject. <c>0</c> is always allowed as "off". The live
/// flow ceiling is the session's configured <c>maxFlow</c>; the validator uses the device envelope
/// and the engine clamps to the live maximum at dispatch.
/// </remarks>
public static class DeviceRanges
{
    /// <summary>Inclusive (min, max) engineering-unit band for a setpoint variable.</summary>
    public static (double Min, double Max) For(SetpointVariable variable) => variable switch
    {
        SetpointVariable.Temperature => (15, 60),
        SetpointVariable.Agitation => (50, 1000),
        SetpointVariable.Oxygen => (0, 100),
        SetpointVariable.Flow => (0, 25),
        SetpointVariable.Pressure => (1, 380),
        SetpointVariable.Ph => (0, 14),
        _ => (double.NegativeInfinity, double.PositiveInfinity),
    };

    /// <summary>True when <paramref name="value"/> is within the band, or is exactly 0 (off).</summary>
    public static bool Accepts(SetpointVariable variable, double value)
    {
        if (value == 0)
        {
            return true;
        }

        var (min, max) = For(variable);
        return value >= min && value <= max;
    }
}

/// <summary>
/// Validates a recipe against the rule set ported from ReceitasOpenTEC's <c>RecipeValidator</c>
/// (<c>docs/UI_DESIGN.md</c> §5.3.13).
/// </summary>
/// <remarks>
/// Findings are surfaced in a persistent validation strip rather than only on save. Graph rules
/// come first, then per-block rules; <see cref="RecipeValidationResult.IsValid"/> gates
/// <c>Iniciar</c>.
/// </remarks>
public static class RecipeValidator
{
    private const double MaxPumpSeconds = 3600;

    /// <summary>Validates a recipe, returning every finding.</summary>
    public static RecipeValidationResult Validate(RecipeDocument recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var findings = new List<RecipeFinding>();

        ValidateGraph(recipe, findings);
        foreach (var node in recipe.Nodes)
        {
            ValidateNode(node, findings);
        }

        // Errors first, then warnings — the strip and findings list read most-severe first.
        findings.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        return new RecipeValidationResult { Findings = findings };
    }

    // ── Graph-level rules ──────────────────────────────────────────────────────

    private static void ValidateGraph(RecipeDocument recipe, List<RecipeFinding> findings)
    {
        if (recipe.Nodes.Count == 0)
        {
            findings.Add(Error("A receita não contém blocos."));
            return;
        }

        // Ids present and unique.
        var seen = new HashSet<string>();
        foreach (var node in recipe.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                findings.Add(Error("Há um bloco com identificador vazio.", node.Id));
            }
            else if (!seen.Add(node.Id))
            {
                findings.Add(Error($"Identificador de bloco duplicado: {node.Id}.", node.Id));
            }
        }

        var starts = recipe.Nodes.Where(n => n.Type == NodeType.Start).ToList();
        if (starts.Count == 0)
        {
            findings.Add(Error("A receita precisa de exatamente um bloco Início."));
        }
        else if (starts.Count > 1)
        {
            findings.Add(Error($"A receita tem {starts.Count} blocos Início; deve ter exatamente um."));
        }

        if (recipe.Nodes.All(n => n.Type != NodeType.End))
        {
            findings.Add(Error("A receita precisa de pelo menos um bloco Fim."));
        }

        // Connections must reference existing endpoints.
        var ids = recipe.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var c in recipe.Connections)
        {
            if (!ids.Contains(c.SourceNodeId))
            {
                findings.Add(Error($"Uma conexão referencia um bloco de origem inexistente ({c.SourceNodeId})."));
            }

            if (!ids.Contains(c.TargetNodeId))
            {
                findings.Add(Error($"Uma conexão referencia um bloco de destino inexistente ({c.TargetNodeId})."));
            }
        }

        if (starts.Count != 1)
        {
            return; // Reachability and path checks need a single, well-defined entry point.
        }

        var start = starts[0];
        var reachable = Reachable(recipe, start.Id);

        foreach (var node in recipe.Nodes.Where(n => !reachable.Contains(n.Id)))
        {
            findings.Add(Warning($"Bloco inacessível a partir do Início: {Title(node)}.", node.Id));
        }

        if (!recipe.Nodes.Any(n => n.Type == NodeType.End && reachable.Contains(n.Id)))
        {
            findings.Add(Error("Não há caminho do Início até um Fim."));
        }

        if (HasCycleOutsideLoop(recipe, start.Id))
        {
            findings.Add(Error("Ciclo detectado fora de um laço de cascata."));
        }
    }

    /// <summary>Nodes reachable from <paramref name="startId"/> over every edge (loop edges included).</summary>
    private static HashSet<string> Reachable(RecipeDocument recipe, string startId)
    {
        var reachable = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(startId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reachable.Add(id))
            {
                continue;
            }

            foreach (var c in recipe.Connections.Where(c => c.SourceNodeId == id))
            {
                stack.Push(c.TargetNodeId);
            }
        }

        return reachable;
    }

    /// <summary>
    /// Detects a cycle in the <b>normal-flow</b> graph — loop-out edges and edges into a loop-in
    /// port are excluded, since the cascade's loop is a deliberate, bounded construct.
    /// </summary>
    private static bool HasCycleOutsideLoop(RecipeDocument recipe, string startId)
    {
        var edges = recipe.Connections
            .Where(c => !ConnectorNames.IsLoopOut(c.SourceConnector) && !ConnectorNames.IsLoopIn(c.TargetConnector))
            .ToLookup(c => c.SourceNodeId, c => c.TargetNodeId);

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        bool Dfs(string id)
        {
            if (done.Contains(id))
            {
                return false;
            }

            if (!visiting.Add(id))
            {
                return true; // back-edge onto the current DFS stack
            }

            foreach (var next in edges[id])
            {
                if (Dfs(next))
                {
                    return true;
                }
            }

            visiting.Remove(id);
            done.Add(id);
            return false;
        }

        // Start from the entry point, then from any node, so a cycle in a detached component
        // is still caught.
        return Dfs(startId) || recipe.Nodes.Any(n => Dfs(n.Id));
    }

    // ── Per-block rules ────────────────────────────────────────────────────────

    private static void ValidateNode(RecipeNode node, List<RecipeFinding> findings)
    {
        switch (node.Type)
        {
            case NodeType.Timer:
                RequireFinite(node, "duracao", "A duração", findings, allowNegative: false);
                break;

            case NodeType.MonitorVariable:
                ValidateMonitor(node, findings);
                break;

            case NodeType.SetSetpoint:
                ValidateSetpoint(node, node, findings);
                break;

            case NodeType.MultiSetpoint:
                foreach (var row in node.Rows("pontos").OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    ValidateSetpointRow(node, row, findings);
                }

                break;

            case NodeType.SetLoop:
                ValidateLoop(node, node.Text("malha"), findings);
                break;

            case NodeType.MultiLoop:
                foreach (var row in node.Rows("controles").OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    ValidateLoop(node, row["malha"]?.GetValue<string>() ?? "", findings);
                }

                break;

            case NodeType.PhPump:
            case NodeType.AntifoamPump:
                ValidatePump(node, findings, hasIntensity: true);
                break;

            case NodeType.NutrientPump:
                ValidatePump(node, findings, hasIntensity: false);
                break;

            case NodeType.PumpControl:
                ValidateExternalPump(node, findings);
                break;

            case NodeType.BiomassSensor:
                ValidateBiomass(node, findings);
                break;

            case NodeType.FlaskAgitator:
                ValidateFlaskAgitator(node, findings);
                break;

            case NodeType.CascadeControl:
                ValidateCascade(node, findings);
                break;
        }
    }

    private static void ValidateMonitor(RecipeNode node, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<MeasuredVariable>(node.Text("variavel"), out _))
        {
            // An actuation variable (or anything unmeasurable) reached the field via a hand-edit.
            findings.Add(Error(
                $"'{node.Text("variavel")}' não é uma variável medível — variáveis de atuação não podem ser monitoradas.",
                node.Id));
        }

        var target = node.Number("valorAlvo");
        if (double.IsNaN(target) || double.IsInfinity(target))
        {
            findings.Add(Error("O valor alvo do monitoramento é inválido.", node.Id));
        }

        if (node.Number("intervaloPollingMs") <= 0)
        {
            findings.Add(Error("O intervalo de polling deve ser maior que zero.", node.Id));
        }

        if (node.Number("confirmacoes") < 1)
        {
            findings.Add(Error("As confirmações consecutivas devem ser ao menos 1.", node.Id));
        }

        if (node.Number("tempoLimiteMs") < 0)
        {
            findings.Add(Error("O tempo limite não pode ser negativo.", node.Id));
        }
    }

    private static void ValidateSetpoint(RecipeNode node, RecipeNode valueSource, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<SetpointVariable>(valueSource.Text("variavel"), out var variable))
        {
            findings.Add(Error($"Variável de setpoint inválida: {valueSource.Text("variavel")}.", node.Id));
            return;
        }

        var value = valueSource.Number("valor");
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            findings.Add(Error("O valor do setpoint é inválido.", node.Id));
        }
        else if (!DeviceRanges.Accepts(variable, value))
        {
            var (min, max) = DeviceRanges.For(variable);
            findings.Add(Error($"Setpoint fora da faixa do dispositivo ({min}–{max}).", node.Id));
        }
    }

    private static void ValidateSetpointRow(
        RecipeNode node, System.Text.Json.Nodes.JsonObject row, List<RecipeFinding> findings)
    {
        var variableName = row["variavel"]?.GetValue<string>() ?? "";
        if (!Enum.TryParse<SetpointVariable>(variableName, out var variable))
        {
            findings.Add(Error($"Variável de setpoint inválida: {variableName}.", node.Id));
            return;
        }

        var value = row["valor"] is System.Text.Json.Nodes.JsonValue v && RecipeNode.TryReadNumber(v, out var d) ? d : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value) || !DeviceRanges.Accepts(variable, value))
        {
            var (min, max) = DeviceRanges.For(variable);
            findings.Add(Error($"Setpoint de {variableName} fora da faixa ({min}–{max}).", node.Id));
        }
    }

    private static void ValidateLoop(RecipeNode node, string loopName, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<ControlLoop>(loopName, out _))
        {
            findings.Add(Error($"Malha de controle inválida: {loopName}.", node.Id));
        }
    }

    private static void ValidatePump(RecipeNode node, List<RecipeFinding> findings, bool hasIntensity)
    {
        if (hasIntensity)
        {
            var intensity = node.Number("intensidade");
            if (double.IsNaN(intensity) || intensity is < 0 or > 100)
            {
                findings.Add(Error("A intensidade da bomba deve estar entre 0 e 100 %.", node.Id));
            }
        }

        foreach (var key in PumpTimeKeys(node.Type))
        {
            var value = node.Number(key);
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                findings.Add(Error($"O tempo '{node.Definition.Parameter(key)?.Label ?? key}' é inválido.", node.Id));
            }
            else if (value > MaxPumpSeconds)
            {
                findings.Add(Error($"O tempo '{node.Definition.Parameter(key)?.Label ?? key}' excede 3600 s.", node.Id));
            }
        }
    }

    private static IEnumerable<string> PumpTimeKeys(NodeType type) => type switch
    {
        NodeType.NutrientPump => ["tempoDosagemLigadaS", "tempoDosagemDesligadaS"],
        _ => ["tempoLigadaS", "tempoDesligadaS"],
    };

    private static void ValidateCascade(RecipeNode node, List<RecipeFinding> findings)
    {
        var interval = node.Number("intervaloPidS");
        if (double.IsNaN(interval) || interval is < 0.1 or > 60)
        {
            findings.Add(Error("O intervalo de cálculo do PID deve estar entre 0,1 e 60 s.", node.Id));
        }

        var modo = node.Text("modo");
        if (modo is not ("AgitationOnly" or "AerationOnly" or "DualCascade" or "KlaPath"))
        {
            findings.Add(Error($"Modo de controle de O₂ inválido: {modo}.", node.Id));
            return;
        }

        var drivesAgitation = modo is "AgitationOnly" or "DualCascade" or "KlaPath";
        var drivesAeration = modo is "AerationOnly" or "DualCascade" or "KlaPath";

        if (drivesAgitation)
        {
            CheckOrdered(node, "nMinRpm", "nMaxRpm", "As faixas de agitação (N_min/N_max)", findings);
        }

        if (drivesAeration)
        {
            CheckOrdered(node, "qMinVvm", "qMaxVvm", "As faixas de aeração (Q_min/Q_max)", findings);
        }

        // The percentage actuation windows only apply to the Cascata mode.
        if (modo == "DualCascade")
        {
            CheckWindow(node, "agitacaoOutMin", "agitacaoOutMax", "Agitação", findings);
            CheckWindow(node, "aeracaoOutMin", "aeracaoOutMax", "Aeração", findings);

            var overlapStart = Math.Max(node.Number("agitacaoOutMin"), node.Number("aeracaoOutMin"));
            var overlapEnd = Math.Min(node.Number("agitacaoOutMax"), node.Number("aeracaoOutMax"));
            if (overlapEnd <= overlapStart)
            {
                findings.Add(Error("As janelas de atuação de Agitação e Aeração devem se sobrepor no modo Cascata.", node.Id));
            }
        }
    }

    private static void CheckOrdered(
        RecipeNode node, string minKey, string maxKey, string label, List<RecipeFinding> findings)
    {
        if (node.Number(minKey) > node.Number(maxKey))
        {
            findings.Add(Error($"{label}: o mínimo é maior que o máximo.", node.Id));
        }
    }

    private static void CheckWindow(
        RecipeNode node, string minKey, string maxKey, string label, List<RecipeFinding> findings)
    {
        var min = node.Number(minKey);
        var max = node.Number(maxKey);
        if (!(min >= 0 && min < max && max <= 100))
        {
            findings.Add(Error($"Janela de atuação de {label} fora de 0 ≤ OutMin < OutMax ≤ 100.", node.Id));
        }
    }

    private static void RequireFinite(
        RecipeNode node, string key, string label, List<RecipeFinding> findings, bool allowNegative)
    {
        var value = node.Number(key);
        if (double.IsNaN(value) || double.IsInfinity(value) || (!allowNegative && value < 0))
        {
            findings.Add(Error($"{label} é inválida.", node.Id));
        }
    }

    /// <summary>
    /// The external feed pump. Only the selected action's fields are checked, so an operator can
    /// leave the other modes' values staged in the block without failing validation.
    /// </summary>
    private static void ValidateExternalPump(RecipeNode node, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<ExternalPumpAction>(node.Text("acao"), out var action))
        {
            findings.Add(Error("Ação da bomba externa desconhecida.", node.Id));
            return;
        }

        if (action != ExternalPumpAction.SendProfile)
        {
            return;
        }

        if (!Enum.TryParse<OpenTECHub.Protocol.PumpProfileMode>(node.Text("modo"), out var mode) ||
            mode == OpenTECHub.Protocol.PumpProfileMode.Idle)
        {
            findings.Add(Error("Perfil da bomba externa desconhecido.", node.Id));
            return;
        }

        var init = node.Number("inicioMin");
        var final = node.Number("fimMin");
        if (double.IsNaN(init) || double.IsNaN(final) || init < 0 || final < 0)
        {
            findings.Add(Error("A janela de operação da bomba é inválida.", node.Id));
        }
        else if (final <= init)
        {
            findings.Add(Error("O fim da janela da bomba deve ser maior que o início.", node.Id));
        }

        switch (mode)
        {
            case OpenTECHub.Protocol.PumpProfileMode.Constant:
            case OpenTECHub.Protocol.PumpProfileMode.Linear:
            case OpenTECHub.Protocol.PumpProfileMode.Exponential:
                RequireFinite(node, "lambda", "O parâmetro λ", findings, allowNegative: false);
                if (mode != OpenTECHub.Protocol.PumpProfileMode.Constant)
                {
                    RequireFinite(node, "phi", "O parâmetro φ", findings, allowNegative: true);
                }

                break;

            case OpenTECHub.Protocol.PumpProfileMode.Polynomial:
            {
                // p0..p20 is what the firmware forwards; more coefficients would be dropped
                // silently and the delivered profile would not be the one on screen.
                var coefficients = ParseNumberList(node.Text("coeficientes"));
                if (coefficients.Count is < 1 or > 21)
                {
                    findings.Add(Error(
                        "O perfil polinomial precisa de 1 a 21 coeficientes (p0..p20).", node.Id));
                }

                break;
            }

            case OpenTECHub.Protocol.PumpProfileMode.Piecewise:
            {
                var times = ParseNumberList(node.Text("tempos"));
                var flows = ParseNumberList(node.Text("vazoes"));

                if (times.Count != flows.Count)
                {
                    findings.Add(Error(
                        "Os segmentos precisam do mesmo número de tempos e de vazões.", node.Id));
                }
                else if (times.Count is < 2 or > 100)
                {
                    findings.Add(Error("O perfil por segmentos precisa de 2 a 100 pontos.", node.Id));
                }
                else
                {
                    // Interpolation between two points at the same time is a division by zero, and
                    // a decreasing series would silently reorder the profile.
                    for (var i = 1; i < times.Count; i++)
                    {
                        if (times[i] <= times[i - 1])
                        {
                            findings.Add(Error(
                                "Os tempos dos segmentos devem ser estritamente crescentes.", node.Id));
                            break;
                        }
                    }
                }

                break;
            }
        }
    }

    /// <summary>The biomass sensor. Only the threshold action carries values to check.</summary>
    private static void ValidateBiomass(RecipeNode node, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<BiomassAction>(node.Text("acao"), out var action))
        {
            findings.Add(Error("Ação do sensor de biomassa desconhecida.", node.Id));
            return;
        }

        if (action != BiomassAction.Thresholds)
        {
            return;
        }

        var low = node.Number("limiarBaixo");
        var high = node.Number("limiarAlto");
        var optimal = node.Number("limiarOtimo");

        foreach (var (value, label) in new[]
                 {
                     (low, "baixo"), (high, "alto"), (optimal, "ótimo"),
                 })
        {
            if (double.IsNaN(value) || value is < 0 or > 200_000)
            {
                findings.Add(Error($"O limiar {label} deve estar entre 0 e 200000 contagens.", node.Id));
            }
        }

        // Same ordering the manual card enforces: the integration-time search walks low → optimal
        // → high, and an out-of-order triple makes the node's gear selection meaningless.
        if (low >= high)
        {
            findings.Add(Error("O limiar baixo deve ser menor que o alto.", node.Id));
        }
        else if (optimal <= low || optimal >= high)
        {
            findings.Add(Error("O limiar ótimo deve ficar entre o baixo e o alto.", node.Id));
        }
    }

    private static void ValidateFlaskAgitator(RecipeNode node, List<RecipeFinding> findings)
    {
        if (!Enum.TryParse<FlaskAgitatorAction>(node.Text("acao"), out var action))
        {
            findings.Add(Error("Ação do agitador de frasco desconhecida.", node.Id));
            return;
        }

        if (action != FlaskAgitatorAction.Run)
        {
            return;
        }

        var magnitude = node.Number("intensidade");
        if (double.IsNaN(magnitude) || magnitude is < 0 or > 100)
        {
            findings.Add(Error("A intensidade do agitador deve estar entre 0 e 100%.", node.Id));
        }

        if (!Enum.TryParse<AgitatorDirection>(node.Text("sentido"), out _))
        {
            findings.Add(Error("Sentido do agitador desconhecido.", node.Id));
        }
    }

    /// <summary>
    /// Reads a comma or semicolon separated numeric list, matching the engine's parser.
    /// </summary>
    /// <remarks>
    /// Deliberately the same acceptance as <c>RecipeEngine.ParseList</c>: a validator that is
    /// stricter than the engine passes recipes the engine then mangles, and one that is looser
    /// blocks recipes that would have run.
    /// </remarks>
    private static IReadOnlyList<double> ParseNumberList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<double> values = [];
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(
                    part.Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static RecipeFinding Error(string message, string? nodeId = null)
        => new(RecipeFindingSeverity.Error, message, nodeId);

    private static RecipeFinding Warning(string message, string? nodeId = null)
        => new(RecipeFindingSeverity.Warning, message, nodeId);

    private static string Title(RecipeNode node) => node.Definition.Title;
}
