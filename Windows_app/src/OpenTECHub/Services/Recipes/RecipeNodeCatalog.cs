using OpenTECHub.Protocol;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

/// <summary>Category presentation: pt-BR heading and header colour (§5.3.6).</summary>
/// <param name="Label">pt-BR category name shown in the block library.</param>
/// <param name="HeaderColor">Category header hex, applied to the block header only.</param>
public sealed record BlockCategoryInfo(string Label, string HeaderColor);

/// <summary>
/// The single, declared-once inventory of the nineteen recipe blocks.
/// </summary>
/// <remarks>
/// <para>
/// Taken from ReceitasOpenTEC's <c>NodeType</c> enum and node classes, then re-targeted to the
/// ESP32-S3 protocol (<c>docs/UI_DESIGN.md</c> §5.3.6). Declaring each block once here — its
/// category, ports and parameter schema — is the "declared once and generated" improvement the
/// roadmap calls for: the library rows, the property editor and the per-node defaults are all
/// generated from these definitions instead of a hand-written triple per type.
/// </para>
/// <para>
/// Every numeric range is the same source the manual setpoint fields and the wire builders use,
/// so a recipe cannot accept a value the device would reject.
/// </para>
/// </remarks>
public static class RecipeNodeCatalog
{
    private const string SlateHeader = "#596575";
    private const string VioletHeader = "#8B3CC2";
    private const string BlueHeader = "#3182F6";
    private const string OrangeHeader = "#E89A18";
    private const string TealHeader = "#0E8A8A";
    private const string CyanHeader = "#1F6FB2";

    /// <summary>Category presentation, keyed by category.</summary>
    public static IReadOnlyDictionary<BlockCategory, BlockCategoryInfo> Categories { get; } =
        new Dictionary<BlockCategory, BlockCategoryInfo>
        {
            [BlockCategory.Flow] = new("Fluxo", SlateHeader),
            [BlockCategory.Logic] = new("Lógica / Controle", VioletHeader),
            [BlockCategory.Triggers] = new("Gatilhos", BlueHeader),
            [BlockCategory.Actions] = new("Ações", OrangeHeader),
            [BlockCategory.Pumps] = new("Bombas", TealHeader),
            [BlockCategory.ExternalDevices] = new("Dispositivos Externos", CyanHeader),
            [BlockCategory.Utilities] = new("Utilitários", SlateHeader),
        };

    /// <summary>
    /// The green header <c>#3AA75B</c> that <see cref="NodeType.End"/> keeps as the single
    /// exception — successful termination is the one place a block's category and meaning coincide.
    /// </summary>
    public const string EndHeaderColor = "#3AA75B";

    /// <summary>The header colour for a block, honouring the <see cref="NodeType.End"/> exception.</summary>
    public static string HeaderColor(NodeType type)
        => type == NodeType.End ? EndHeaderColor : Categories[Definition(type).Category].HeaderColor;

    // Lazy so the definitions are built only after every static field they read (the port
    // shapes and option arrays declared below) has been initialised — a plain `= Build()`
    // field initialiser would run in textual order, before those fields exist.
    private static readonly Lazy<IReadOnlyList<RecipeNodeDefinition>> LazyAll = new(Build);

    private static readonly Lazy<IReadOnlyDictionary<NodeType, RecipeNodeDefinition>> LazyByType =
        new(() => LazyAll.Value.ToDictionary(d => d.Type));

    /// <summary>All nineteen block definitions, in library order.</summary>
    public static IReadOnlyList<RecipeNodeDefinition> All => LazyAll.Value;

    /// <summary>The definition for a block type.</summary>
    public static RecipeNodeDefinition Definition(NodeType type) => LazyByType.Value[type];

    /// <summary>True when the catalog declares this block type.</summary>
    public static bool Has(NodeType type) => LazyByType.Value.ContainsKey(type);

    // ── Port shapes ──────────────────────────────────────────────────────────

    private static readonly RecipePort PortIn = new(ConnectorNames.In, PortDirection.In);
    private static readonly RecipePort PortInMany = new(ConnectorNames.In, PortDirection.In, Multiple: true);
    private static readonly RecipePort PortOut = new(ConnectorNames.Out, PortDirection.Out);

    private static IReadOnlyList<RecipePort> InOut => [PortIn, PortOut];

    // ── Enum option sets (value = enum member name, label = pt-BR) ────────────

    private static readonly RecipeOption[] MeasuredVariables =
    [
        new(nameof(MeasuredVariable.Temperature), "Temperatura"),
        new(nameof(MeasuredVariable.Ph), "pH"),
        new(nameof(MeasuredVariable.Oxygen), "O₂ dissolvido"),
        new(nameof(MeasuredVariable.Pressure), "Pressão"),
        new(nameof(MeasuredVariable.Flow), "Vazão"),
        new(nameof(MeasuredVariable.Level), "Nível"),
        new(nameof(MeasuredVariable.Biomass), "Biomassa"),
    ];

    private static readonly RecipeOption[] SetpointVariables =
    [
        new(nameof(SetpointVariable.Temperature), "Temperatura"),
        new(nameof(SetpointVariable.Agitation), "Agitação"),
        new(nameof(SetpointVariable.Oxygen), "O₂ dissolvido"),
        new(nameof(SetpointVariable.Flow), "Vazão"),
        new(nameof(SetpointVariable.Pressure), "Pressão"),
        new(nameof(SetpointVariable.Ph), "pH"),
    ];

    private static readonly RecipeOption[] ControlLoops =
    [
        new(nameof(ControlLoop.Aeration), "Aeração"),
        new(nameof(ControlLoop.Ph), "pH"),
        new(nameof(ControlLoop.Antifoam), "Antiespuma"),
        new(nameof(ControlLoop.Nutrient), "Nutrientes"),
    ];

    private static readonly RecipeOption[] Comparisons =
    [
        new(nameof(ComparisonOperator.GreaterThan), "Maior que (>)"),
        new(nameof(ComparisonOperator.LessThan), "Menor que (<)"),
        new(nameof(ComparisonOperator.GreaterOrEqual), "Maior ou igual (>=)"),
        new(nameof(ComparisonOperator.LessOrEqual), "Menor ou igual (<=)"),
        new(nameof(ComparisonOperator.Equal), "Igual a (=)"),
    ];

    private static readonly RecipeOption[] TimeUnits =
    [
        new(nameof(TimeUnit.Seconds), "Segundos"),
        new(nameof(TimeUnit.Minutes), "Minutos"),
        new(nameof(TimeUnit.Hours), "Horas"),
    ];

    private static readonly RecipeOption[] AcquisitionModes =
    [
        new(nameof(AcquisitionMode.FixedTime), "Tempo definido"),
        new(nameof(AcquisitionMode.ManualStop), "Finalização manual"),
    ];

    private static readonly RecipeOption[] LoopOperations =
    [
        new(nameof(LoopOperation.Enable), "Ligar"),
        new(nameof(LoopOperation.Disable), "Desligar"),
    ];

    private static readonly RecipeOption[] ManualGates =
    [
        new(nameof(ManualGateOperation.Hold), "Bloquear"),
        new(nameof(ManualGateOperation.Pass), "Passar"),
    ];

    private static readonly RecipeOption[] PhTargets =
    [
        new(nameof(PhPumpTarget.Acid), "Ácido"),
        new(nameof(PhPumpTarget.Base), "Base"),
    ];

    private static readonly RecipeOption[] PumpOperations =
    [
        new(nameof(PumpOperation.Manual), "Acionamento manual"),
        new(nameof(PumpOperation.ResetVolume), "Zerar volume"),
        new(nameof(PumpOperation.ConfigureCycle), "Configurar ciclo"),
    ];

    private static readonly RecipeOption[] PumpManualActions =
    [
        new(nameof(PumpManualAction.On), "Ligar"),
        new(nameof(PumpManualAction.Off), "Desligar"),
    ];

    // Cascade rate-estimation method. Stored as stable strings; no enum needed elsewhere.
    private static readonly RecipeOption[] RateMethods =
    [
        new("LeastSquares", "Mínimos quadrados"),
        new("Endpoints", "Diferença de extremos"),
    ];

    private static readonly RecipeOption[] ExternalPumpActions =
    [
        new(nameof(ExternalPumpAction.Enable), "Habilitar no Hub"),
        new(nameof(ExternalPumpAction.SendProfile), "Enviar perfil"),
        new(nameof(ExternalPumpAction.Stop), "Parar e desativar"),
    ];

    /// <summary>The five firmware profiles. <c>Idle</c> is absent: it is a stop, not a profile.</summary>
    private static readonly RecipeOption[] PumpProfileModes =
    [
        new(nameof(PumpProfileMode.Constant), "Constante"),
        new(nameof(PumpProfileMode.Linear), "Linear"),
        new(nameof(PumpProfileMode.Exponential), "Exponencial"),
        new(nameof(PumpProfileMode.Polynomial), "Polinomial"),
        new(nameof(PumpProfileMode.Piecewise), "Segmentos"),
    ];

    private static readonly RecipeOption[] BiomassActions =
    [
        new(nameof(BiomassAction.Enable), "Habilitar no Hub"),
        new(nameof(BiomassAction.Blank), "Capturar branco"),
        new(nameof(BiomassAction.Start), "Iniciar aquisição"),
        new(nameof(BiomassAction.Stop), "Parar aquisição"),
        new(nameof(BiomassAction.Thresholds), "Enviar limiares"),
        new(nameof(BiomassAction.Disable), "Parar e desativar"),
    ];

    private static readonly RecipeOption[] FlaskAgitatorActions =
    [
        new(nameof(FlaskAgitatorAction.Run), "Acionar"),
        new(nameof(FlaskAgitatorAction.Stop), "Parar (bloqueia o potenciômetro)"),
    ];

    private static readonly RecipeOption[] AgitatorDirections =
    [
        new(nameof(AgitatorDirection.Clockwise), "Horário"),
        new(nameof(AgitatorDirection.CounterClockwise), "Anti-horário"),
    ];

    private static IReadOnlyList<RecipeNodeDefinition> Build() =>
    [
        new()
        {
            Type = NodeType.KlaAssay, Title = "Determinar kLa", Category = BlockCategory.Actions, Ports = InOut,
            Parameters =
            [
                EnumP("protocol", "Protocolo", nameof(KlaAssayProtocol.Abiotic),
                    [new(nameof(KlaAssayProtocol.Abiotic), "Abiótico"), new(nameof(KlaAssayProtocol.Biotic), "Biótico")]),
                EnumP("conditionsMode", "Condições", nameof(RecipeKlaConditionMode.SingleAtCurrentCondition),
                    [new(nameof(RecipeKlaConditionMode.SingleAtCurrentCondition), "Único — condições atuais do cultivo"),
                     new(nameof(RecipeKlaConditionMode.SingleExplicit), "Único — definir N e Q"),
                     new(nameof(RecipeKlaConditionMode.Multiple), "Múltiplos — matriz de condições")]),
                Num("agitationRpm", "Agitação", 300, min: 15, max: 1000, unit: "rpm", visibleWhen: "conditionsMode=SingleExplicit"),
                Num("airflowLpm", "Vazão de ar", 2, min: 0.001, unit: "L/min", visibleWhen: "conditionsMode=SingleExplicit"),
                ListP("conditions", "Condições e réplicas",
                    [Num("agitationRpm", "Agitação", 300, min: 15, max: 1000, unit: "rpm"),
                     Num("airflowLpm", "Vazão de ar", 2, min: 0.001, unit: "L/min"),
                     Int("replicates", "Réplicas", 1, min: 1)], "conditionsMode=Multiple"),
                Text("profileId", "Perfil operacional", KlaRecipeOperatorProfile.Id),
                Text("profileVersion", "Versão do perfil", KlaRecipeOperatorProfile.Version),
                Num("minimumDoPercent", "OD mínimo durante o corte de ar", 5, min: 0, max: 99, unit: "%", visibleWhen: "protocol=Biotic"),
                Num("returnSeconds", "Prazo para retornar às condições anteriores", 600, min: 1, unit: "s"),
                Bool("requireValidOur", "Exigir OUR válido para aceitar a réplica", false, visibleWhen: "protocol=Biotic"),
                Int("maximumAttemptsPerReplicate", "Máximo de tentativas por réplica", 1, min: 1),
                Bool("useProfileRetryReasons", "Usar os motivos de repetição do perfil", true),
                Bool("retryInsufficientWindow", "Repetir se a janela de medição for insuficiente", false, visibleWhen: "useProfileRetryReasons=false"),
                Bool("retryExcessiveNoise", "Repetir se o ruído for excessivo", false, visibleWhen: "useProfileRetryReasons=false"),
                Bool("retryUnstableCondition", "Repetir se a condição for instável", false, visibleWhen: "useProfileRetryReasons=false"),
                Int("maximumAttemptsPerCultivation", "Máximo de tentativas da receita (0 = não aplicável)", 0, min: 0),
                Num("minimumIntervalSeconds", "Intervalo mínimo entre ensaios (estabilização)", 30, min: 0, unit: "s"),
                Num("maximumBlockSeconds", "Prazo total do bloco (0 = não aplicável)", 21600, min: 0, unit: "s"),
                Num("maximumGasOffSeconds", "Tempo máximo sem ar por tentativa (0 = desoxigenação máxima da página kLa)", 0,
                    min: 0, unit: "s", visibleWhen: "protocol=Biotic"),
                Num("maximumCultivationGasOffSeconds", "Tempo total máximo sem ar no cultivo (0 = não aplicável)", 0, min: 0, unit: "s"),
                EnumP("failurePolicy", "Se o resultado for inconclusivo", nameof(KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration),
                    [new(nameof(KlaRecipeFailurePolicy.StopAfterRestoration), "Encerrar após restaurar"),
                     new(nameof(KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration), "Continuar sem resultado após restaurar")]),
            ]
        },
        new()
        {
            Type = NodeType.LinearSetpointRamp, Title = "Rampa linear de referências", Category = BlockCategory.Actions,
            Ports = [PortIn, PortOut],
            Parameters = [
                ListP("lines", "Parâmetros e tempos finais", [
                    EnumP("variable", "Parâmetro", nameof(SetpointVariable.Temperature), SetpointVariables),
                    EnumP("startSource", "Referência inicial", nameof(SetpointStartSource.CurrentConfirmed),
                        [new(nameof(SetpointStartSource.CurrentConfirmed), "Atual confirmada"), new(nameof(SetpointStartSource.Explicit), "Explícita")]),
                    Num("initialSetpoint", "Valor inicial", 30, visibleWhen: "startSource=Explicit"),
                    Num("finalSetpoint", "Valor final", 30),
                    Num("endAfterSeconds", "Terminar após", 60, min: 0, unit: "s"),
                    EnumP("oxygenTarget", "Destino de O₂", nameof(RampOxygenTarget.ActiveCascadeReference),
                        [new(nameof(RampOxygenTarget.ActiveCascadeReference), "Referência da cascata")], visibleWhen: "variable=Oxygen")]),
                Text("cascadeNodeId", "Controle de O₂ associado", ""),
                Num("temperatureTolerance", "Desvio permitido da temperatura", RecipeRampCompletionCriteria.OperationalDefaults.TemperatureToleranceCelsius, min: 0, unit: "°C"),
                Num("agitationTolerance", "Desvio permitido da agitação", RecipeRampCompletionCriteria.OperationalDefaults.AgitationToleranceRpm, min: 0, unit: "rpm"),
                Num("flowTolerance", "Desvio permitido da vazão", RecipeRampCompletionCriteria.OperationalDefaults.FlowToleranceLpm, min: 0, unit: "L/min"),
                Num("phTolerance", "Desvio permitido do pH", RecipeRampCompletionCriteria.OperationalDefaults.PhTolerance, min: 0),
                Num("pressureTolerance", "Desvio permitido da pressão", RecipeRampCompletionCriteria.OperationalDefaults.PressureToleranceKilopascals, min: 0, unit: "kPa"),
                Num("confirmationStabilitySeconds", "Manter o valor dentro do desvio por", RecipeRampCompletionCriteria.OperationalDefaults.StabilitySeconds, min: 0, unit: "s"),
                Num("maximumTelemetryGapSeconds", "Intervalo máximo sem uma medição nova", RecipeRampCompletionCriteria.OperationalDefaults.MaximumSampleGapSeconds, min: 0, unit: "s"),
                Num("confirmationTimeoutSeconds", "Prazo para confirmar os valores finais", RecipeRampCompletionCriteria.OperationalDefaults.TimeoutSeconds, min: 0, unit: "s"),
                EnumP("cancellationPolicy", "Ao cancelar", nameof(RampCancellationPolicy.HoldLastReferences),
                    [new(nameof(RampCancellationPolicy.HoldLastReferences), "Manter últimas referências"),
                     new(nameof(RampCancellationPolicy.RestoreSnapshot), "Restaurar referências iniciais")])]
        },
        new()
        {
            Type = NodeType.Periodic, Title = "Periodicidade", Category = BlockCategory.Triggers,
            Ports = [PortIn, new RecipePort(ConnectorNames.Out, PortDirection.Out, "Alvo periódico")],
            Parameters =
            [Num("initialDelay", "Primeiro disparo após", 2, min: 0),
             EnumP("initialDelayUnit", "Unidade do primeiro disparo", nameof(TimeUnit.Hours), TimeUnits),
             Num("period", "Período", 4, min: 0),
             EnumP("periodUnit", "Unidade do período", nameof(TimeUnit.Hours), TimeUnits),
             Text("coordinatedCascadeId", "Controle de O₂ associado", "")]
        },
        // ── Fluxo ─────────────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.Start,
            Title = "Início",
            Category = BlockCategory.Flow,
            Ports = [PortOut],
        },
        new()
        {
            Type = NodeType.End,
            Title = "Fim",
            Category = BlockCategory.Flow,
            Ports = [PortIn],
        },

        // ── Gatilhos ───────────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.Timer,
            Title = "Temporizador",
            Category = BlockCategory.Triggers,
            Ports = InOut,
            Parameters =
            [
                Num("duracao", "Duração", 10, min: 0),
                EnumP("unidade", "Unidade", nameof(TimeUnit.Seconds), TimeUnits),
            ],
        },
        new()
        {
            Type = NodeType.MonitorVariable,
            Title = "Monitorar Variável",
            Category = BlockCategory.Triggers,
            Ports = InOut,
            Parameters =
            [
                EnumP("variavel", "Variável", nameof(MeasuredVariable.Temperature), MeasuredVariables),
                EnumP("condicao", "Condição", nameof(ComparisonOperator.GreaterOrEqual), Comparisons),
                Num("valorAlvo", "Valor alvo", 0),
                Int("intervaloPollingMs", "Intervalo de polling", 1000, min: 1, unit: "ms"),
                Int("confirmacoes", "Confirmações consecutivas", 1, min: 1),
                Int("tempoLimiteMs", "Tempo limite (0 = sem limite)", 0, min: 0, unit: "ms"),
            ],
        },
        new()
        {
            Type = NodeType.ManualIntervention,
            Title = "Intervenção Manual",
            Category = BlockCategory.Triggers,
            Ports = InOut,
            Parameters =
            [
                EnumP("operacao", "Operação", nameof(ManualGateOperation.Hold), ManualGates),
            ],
        },

        // ── Lógica / Cascata ────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.And,
            Title = "Sincronizar (E)",
            Category = BlockCategory.Logic,
            Ports = [PortInMany, PortOut],
        },
        new()
        {
            Type = NodeType.Or,
            Title = "Qualquer (OU)",
            Category = BlockCategory.Logic,
            Ports = [PortInMany, PortOut],
        },
        BuildCascade(),

        // ── Ações ────────────────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.SetSetpoint,
            Title = "Definir Ponto de Ajuste",
            Category = BlockCategory.Actions,
            Ports = InOut,
            Parameters =
            [
                EnumP("variavel", "Variável", nameof(SetpointVariable.Temperature), SetpointVariables),
                Num("valor", "Valor", 25.0),
                Num("histerese", "Histerese", 0.1, min: 0, visibleWhen: $"variavel={nameof(SetpointVariable.Ph)}"),
            ],
        },
        new()
        {
            Type = NodeType.MultiSetpoint,
            Title = "Múltiplos Pontos de Ajuste",
            Category = BlockCategory.Actions,
            Ports = InOut,
            Parameters =
            [
                ListP("pontos", "Pontos de ajuste",
                [
                    EnumP("variavel", "Variável", nameof(SetpointVariable.Temperature), SetpointVariables),
                    Num("valor", "Valor", 25.0),
                    Num("histerese", "Histerese", 0.1, min: 0, visibleWhen: $"variavel={nameof(SetpointVariable.Ph)}"),
                ]),
            ],
        },
        new()
        {
            Type = NodeType.SetLoop,
            Title = "Controle de Malha",
            Category = BlockCategory.Actions,
            Ports = InOut,
            Parameters =
            [
                EnumP("malha", "Malha", nameof(ControlLoop.Aeration), ControlLoops),
                EnumP("operacao", "Operação", nameof(LoopOperation.Enable), LoopOperations),
            ],
        },
        new()
        {
            Type = NodeType.MultiLoop,
            Title = "Múltiplos Controles",
            Category = BlockCategory.Actions,
            Ports = InOut,
            Parameters =
            [
                ListP("controles", "Controles",
                [
                    EnumP("malha", "Malha", nameof(ControlLoop.Aeration), ControlLoops),
                    EnumP("operacao", "Operação", nameof(LoopOperation.Enable), LoopOperations),
                ]),
            ],
        },

        // ── Bombas ────────────────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.PhPump,
            Title = "Bomba pH",
            Category = BlockCategory.Pumps,
            Ports = InOut,
            Parameters =
            [
                EnumP("bombaAlvo", "Bomba alvo", nameof(PhPumpTarget.Acid), PhTargets),
                EnumP("operacao", "Operação", nameof(PumpOperation.Manual), PumpOperations),
                Num("intensidade", "Intensidade", 50, min: 0, max: 100, unit: "%"),
                Num("tempoLigadaS", "Tempo ligada", 5, min: 0, max: 3600, unit: "s"),
                Num("tempoDesligadaS", "Tempo desligada", 5, min: 0, max: 3600, unit: "s"),
                EnumP("acaoManual", "Ação manual", nameof(PumpManualAction.On), PumpManualActions),
            ],
        },
        new()
        {
            Type = NodeType.AntifoamPump,
            Title = "Bomba Antiespuma",
            Category = BlockCategory.Pumps,
            Ports = InOut,
            Parameters =
            [
                EnumP("operacao", "Operação", nameof(PumpOperation.Manual), PumpOperations),
                Num("intensidade", "Intensidade", 50, min: 0, max: 100, unit: "%"),
                Num("tempoLigadaS", "Tempo ligada", 5, min: 0, max: 3600, unit: "s"),
                Num("tempoDesligadaS", "Tempo desligada", 5, min: 0, max: 3600, unit: "s"),
                EnumP("acaoManual", "Ação manual", nameof(PumpManualAction.On), PumpManualActions),
            ],
        },
        new()
        {
            Type = NodeType.NutrientPump,
            Title = "Bomba Nutrientes",
            Category = BlockCategory.Pumps,
            Ports = InOut,
            Parameters =
            [
                EnumP("operacao", "Operação", nameof(PumpOperation.Manual), PumpOperations),
                Num("tempoDosagemLigadaS", "Tempo dosagem ligada", 1, min: 0, max: 3600, unit: "s"),
                Num("tempoDosagemDesligadaS", "Tempo dosagem desligada", 1, min: 0, max: 3600, unit: "s"),
                Num("volumeDosar", "Volume a dosar", 0, min: 0, unit: "mL"),
                EnumP("acaoManual", "Ação manual", nameof(PumpManualAction.On), PumpManualActions),
            ],
        },
        // ── Dispositivos Externos ─────────────────────────────────────────────
        // The three Wi-Fi nodes behind the Hub. Each of these blocks can hold waiting for its
        // device to confirm, which is what separates them from the dosing pumps above: those
        // are inside the module and cannot be absent on their own.
        new()
        {
            Type = NodeType.PumpControl,
            Title = "Bomba Externa",
            Category = BlockCategory.ExternalDevices,
            Ports = InOut,
            Parameters =
            [
                EnumP("acao", "Ação", nameof(ExternalPumpAction.Enable), ExternalPumpActions),
                EnumP("modo", "Perfil", nameof(PumpProfileMode.Constant), PumpProfileModes,
                    visibleWhen: $"acao={nameof(ExternalPumpAction.SendProfile)}"),
                Num("inicioMin", "Início", 0, min: 0, max: 100000, unit: "min",
                    visibleWhen: $"acao={nameof(ExternalPumpAction.SendProfile)}"),
                Num("fimMin", "Fim", 60, min: 0, max: 100000, unit: "min",
                    visibleWhen: $"acao={nameof(ExternalPumpAction.SendProfile)}"),
                // λ and φ carry different meanings per mode (see PumpProfileMath), so they are
                // one pair of fields rather than three near-duplicate pairs.
                Num("lambda", "λ", 1.0, min: 0, unit: "mL/min",
                    visibleWhen: $"acao={nameof(ExternalPumpAction.SendProfile)}"),
                Num("phi", "φ", 0.0,
                    visibleWhen: $"acao={nameof(ExternalPumpAction.SendProfile)}"),
                Text("coeficientes", "Coeficientes p0..pN", "1"),
                Text("tempos", "Tempos dos segmentos (min)", "0, 60"),
                Text("vazoes", "Vazões dos segmentos (mL/min)", "1, 1"),
            ],
        },
        new()
        {
            Type = NodeType.BiomassSensor,
            Title = "Absorbância",
            Category = BlockCategory.ExternalDevices,
            Ports = InOut,
            Parameters =
            [
                EnumP("acao", "Ação", nameof(BiomassAction.Enable), BiomassActions),
                Int("limiarBaixo", "Limiar baixo", 10000, min: 0, max: 200000, unit: "contagens",
                    visibleWhen: $"acao={nameof(BiomassAction.Thresholds)}"),
                Int("limiarAlto", "Limiar alto", 40000, min: 0, max: 200000, unit: "contagens",
                    visibleWhen: $"acao={nameof(BiomassAction.Thresholds)}"),
                Int("limiarOtimo", "Limiar ótimo", 25000, min: 0, max: 200000, unit: "contagens",
                    visibleWhen: $"acao={nameof(BiomassAction.Thresholds)}"),
            ],
        },
        new()
        {
            Type = NodeType.FlaskAgitator,
            Title = "Agitador de Frasco",
            Category = BlockCategory.ExternalDevices,
            Ports = InOut,
            Parameters =
            [
                EnumP("acao", "Ação", nameof(FlaskAgitatorAction.Run), FlaskAgitatorActions),
                Num("intensidade", "Intensidade", 50, min: 0, max: 100, unit: "%",
                    visibleWhen: $"acao={nameof(FlaskAgitatorAction.Run)}"),
                EnumP("sentido", "Sentido", nameof(AgitatorDirection.Clockwise), AgitatorDirections,
                    visibleWhen: $"acao={nameof(FlaskAgitatorAction.Run)}"),
                Bool("automatico", "Modo automático", false),
            ],
        },

        // ── Utilitários ────────────────────────────────────────────────────────────
        new()
        {
            Type = NodeType.DataAcquisition,
            Title = "Aquisição de Dados",
            Category = BlockCategory.Utilities,
            Ports = InOut,
            Parameters =
            [
                EnumP("modo", "Modo", nameof(AcquisitionMode.FixedTime), AcquisitionModes),
                Num("duracao", "Duração", 60, min: 0, visibleWhen: $"modo={nameof(AcquisitionMode.FixedTime)}"),
                EnumP("unidade", "Unidade", nameof(TimeUnit.Seconds), TimeUnits,
                    visibleWhen: $"modo={nameof(AcquisitionMode.FixedTime)}"),
            ],
        },
        new()
        {
            Type = NodeType.LogEvent,
            Title = "Registrar Evento",
            Category = BlockCategory.Utilities,
            Ports = InOut,
            Parameters =
            [
                Text("mensagem", "Mensagem", "Evento registrado"),
            ],
        },
        new()
        {
            Type = NodeType.ResetVariables,
            Title = "Zerar Variáveis",
            Category = BlockCategory.Utilities,
            Ports = InOut,
        },
    ];

    /// <summary>
    /// The cascade block (§5.3.7). Its defaults are the field-validated set from the manuscript;
    /// the gas mixer ships disabled because the nitrogen-enrichment path is deferred.
    /// </summary>
    private static RecipeNodeDefinition BuildCascade() => new()
    {
        Type = NodeType.CascadeControl,
        Title = "Controle de O₂",
        Category = BlockCategory.Logic,
        Ports =
        [
            PortIn,
            new(ConnectorNames.LoopOut, PortDirection.Out, Label: "Condição de Saída"),
            new(ConnectorNames.LoopIn, PortDirection.In, Label: "Retorno da Condição"),
            PortOut,
        ],
        Parameters =
        [
            Num("spO2", "SP de O₂", 30.0, min: 0, max: 100, unit: "%", group: "Setpoint"),

            Num("kDot", "K_DOT (laço externo)", 0.075, group: "Ganhos"),
            Num("kp", "Kp", 0.035, group: "Ganhos"),
            Num("ki", "Ki", 0.0010, group: "Ganhos"),
            Num("kd", "Kd", 3.50, group: "Ganhos"),

            Num("iMin", "I_min", -2.5, group: "Anti-windup"),
            Num("iMax", "I_max", 2.5, group: "Anti-windup"),
            Num("janelaIntegradorS", "Janela do integrador", 2400, min: 0, unit: "s", group: "Anti-windup"),

            Num("horizonteTPredS", "Horizonte t_pred", 60, min: 0, unit: "s", group: "Predição"),
            Int("janelaPreditorAmostras", "Janela do preditor", 15, min: 2, unit: "amostras", group: "Predição"),
            Num("tauDFiltroS", "τ_D do filtro", 30, min: 0, unit: "s", group: "Predição"),

            EnumP("metodoTaxa", "Método", "LeastSquares", RateMethods, group: "Estimativa de taxa"),
            Int("janelaMediaAmostras", "Janela da média", 20, min: 2, unit: "amostras", group: "Estimativa de taxa"),
            Num("intervaloPidS", "Intervalo de cálculo do PID", 3.0, min: 0.1, max: 60, unit: "s", group: "Temporização"),

            EnumP("modo", "Modo de atuação", "DualCascade", new RecipeOption[]
            {
                new("AgitationOnly", "Agitação"),
                new("AerationOnly", "Aeração"),
                new("DualCascade", "Cascata (percentuais)"),
                new("KlaPath", "Mapa (trajetória kLa)")
            }, group: "Atuadores"),

            Text("klaMapId", "ID do Mapa kLa", ""),

            Num("nMinRpm", "N_min", 50, min: 0, unit: "rpm", group: "Faixas físicas"),
            Num("nMaxRpm", "N_max", 800, min: 0, unit: "rpm", group: "Faixas físicas"),
            // The aeration window is applied directly in L/min (RecipeEngine.Cascade); the key keeps its old name.
            Num("qMinVvm", "Q_min", 0.5, min: 0, unit: "L/min", group: "Faixas físicas"),
            Num("qMaxVvm", "Q_max", 12.0, min: 0, unit: "L/min", group: "Faixas físicas"),
            // Grid of the aeration setpoint sent to the flowmeter (D-070).
            Num("passoAeracaoLpm", "Passo da vazão", 0.2, min: 0.01, max: 5, unit: "L/min", group: "Faixas físicas"),

            Num("agitacaoOutMin", "Agitação OutMin", 0, min: 0, max: 100, unit: "%", group: "Janelas de atuação"),
            Num("agitacaoOutMax", "Agitação OutMax", 90, min: 0, max: 100, unit: "%", group: "Janelas de atuação"),
            Num("aeracaoOutMin", "Aeração OutMin", 10, min: 0, max: 100, unit: "%", group: "Janelas de atuação"),
            Num("aeracaoOutMax", "Aeração OutMax", 100, min: 0, max: 100, unit: "%", group: "Janelas de atuação"),

            Num("agitacaoGanho", "Agitação (ganho relativo)", 1.0, min: 0, group: "Ganhos relativos"),
            Num("aeracaoGanho", "Aeração (ganho relativo)", 1.43, min: 0, group: "Ganhos relativos"),
        ],
    };

    // ── Parameter factory helpers (keep the declarations above readable) ───────

    private static RecipeParameter Num(
        string key, string label, double def,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity,
        string? unit = null, string? group = null, string? visibleWhen = null)
        => new()
        {
            Key = key,
            Label = label,
            Kind = ParameterKind.Number,
            Default = def,
            Min = min,
            Max = max,
            Unit = unit,
            Group = group,
            VisibleWhen = visibleWhen,
        };

    private static RecipeParameter Int(
        string key, string label, double def,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity,
        string? unit = null, string? group = null, string? visibleWhen = null)
        => new()
        {
            Key = key,
            Label = label,
            Kind = ParameterKind.Integer,
            Default = def,
            Min = min,
            Max = max,
            Unit = unit,
            Group = group,
            VisibleWhen = visibleWhen,
        };

    private static RecipeParameter Bool(string key, string label, bool def, string? group = null, string? visibleWhen = null)
        => new() { Key = key, Label = label, Kind = ParameterKind.Bool, Default = def, Group = group, VisibleWhen = visibleWhen };

    private static RecipeParameter Text(string key, string label, string def)
        => new() { Key = key, Label = label, Kind = ParameterKind.Text, Default = def };

    private static RecipeParameter EnumP(
        string key, string label, string def, RecipeOption[] options,
        string? group = null, string? unit = null, string? visibleWhen = null)
        => new()
        {
            Key = key,
            Label = label,
            Kind = ParameterKind.Enum,
            Default = def,
            Options = options,
            Group = group,
            Unit = unit,
            VisibleWhen = visibleWhen,
        };

    private static RecipeParameter ListP(string key, string label, RecipeParameter[] itemSchema, string? visibleWhen = null)
        => new() { Key = key, Label = label, Kind = ParameterKind.List, ItemSchema = itemSchema, VisibleWhen = visibleWhen };
}
