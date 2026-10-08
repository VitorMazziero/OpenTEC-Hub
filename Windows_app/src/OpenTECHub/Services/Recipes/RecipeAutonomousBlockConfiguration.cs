using System.Collections.Immutable;
using System.Text.Json.Nodes;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeKlaCondition(double AgitationRpm, double AirflowLpm, int Replicates);
public sealed record RecipeKlaBlockConfiguration(KlaAssayProtocol Protocol, RecipeKlaConditionMode ConditionsMode,
    ImmutableArray<RecipeKlaCondition> Conditions, string ProfileId, string ProfileVersion, bool RequireValidOur,
    KlaAutomaticRetryPolicy Retry, KlaRecipeFailurePolicy FailurePolicy, bool UseProfileRetryReasons = true);
public sealed record RecipePeriodicBlockConfiguration(PeriodicBlockSchedule Schedule, string? CoordinatedCascadeId);

/// <summary>Strict draft parsing. Valid configuration does not establish operational qualification.</summary>
public static class RecipeAutonomousBlockConfiguration
{
    public static RecipeKlaBlockConfiguration ReadKla(RecipeNode node)
    {
        if (node.Type != NodeType.KlaAssay) throw new ArgumentException("Bloco não é Determinar kLa.");
        var protocol = Choice<KlaAssayProtocol>(node, "protocol");
        var mode = Choice<RecipeKlaConditionMode>(node, "conditionsMode");
        var conditions = ImmutableArray.CreateBuilder<RecipeKlaCondition>();
        if (mode == RecipeKlaConditionMode.SingleExplicit)
            conditions.Add(Condition(Number(node.Parameters, "agitationRpm"), Number(node.Parameters, "airflowLpm"), 1));
        else if (mode == RecipeKlaConditionMode.Multiple)
        {
            if (node.Parameters["conditions"] is not JsonArray rows || rows.Count == 0)
                throw new ArgumentException("A matriz exige ao menos uma condição.");
            foreach (var row in rows)
            {
                if (row is not JsonObject fields) throw new ArgumentException("Condição da matriz inválida.");
                conditions.Add(Condition(Number(fields, "agitationRpm"), Number(fields, "airflowLpm"), Integer(Number(fields, "replicates"))));
            }
        }
        var profile = node.Text("profileId"); var version = node.Text("profileVersion");
        ContractGuard.Text(profile); ContractGuard.Text(version);
        var requireOur = protocol == KlaAssayProtocol.Biotic && Boolean(node.Parameters, "requireValidOur");
        var useProfileRetryReasons = !node.Parameters.ContainsKey("useProfileRetryReasons") || Boolean(node.Parameters, "useProfileRetryReasons");
        var retry = new KlaAutomaticRetryPolicy
        {
            MaximumAttemptsPerReplicate = Integer(Number(node.Parameters, "maximumAttemptsPerReplicate")),
            MaximumAttemptsPerCultivation = Integer(Number(node.Parameters, "maximumAttemptsPerCultivation")),
            MinimumInterAssaySeconds = Number(node.Parameters, "minimumIntervalSeconds"), MaximumBlockSeconds = Number(node.Parameters, "maximumBlockSeconds"),
            MaximumGasOffSecondsPerAttempt = Number(node.Parameters, "maximumGasOffSeconds"),
            MaximumCumulativeGasOffSecondsPerCultivation = Number(node.Parameters, "maximumCultivationGasOffSeconds"),
            RecoverableReasons = new[] {
                (Key: "retryInsufficientWindow", Reason: KlaRetryReason.InsufficientWindow),
                (Key: "retryExcessiveNoise", Reason: KlaRetryReason.ExcessiveNoise),
                (Key: "retryUnstableCondition", Reason: KlaRetryReason.UnstableCondition)
            }.Where(option => node.Parameters.ContainsKey(option.Key) && Boolean(node.Parameters, option.Key))
                .Select(option => option.Reason).ToImmutableArray()
        };
        if (useProfileRetryReasons) retry = retry with { RecoverableReasons = [] };
        retry.Validate();
        return new(protocol, mode, conditions.ToImmutable(), profile, version, requireOur, retry,
            Choice<KlaRecipeFailurePolicy>(node, "failurePolicy"), useProfileRetryReasons);
    }

    public static RecipePeriodicBlockConfiguration ReadPeriodic(RecipeNode node)
    {
        if (node.Type != NodeType.Periodic) throw new ArgumentException("Bloco não é Periodicidade.");
        var schedule = new PeriodicBlockSchedule
        {
            InitialDelaySeconds = Seconds(Number(node.Parameters, "initialDelay"), Choice<TimeUnit>(node, "initialDelayUnit")),
            PeriodSeconds = Seconds(Number(node.Parameters, "period"), Choice<TimeUnit>(node, "periodUnit"))
        };
        schedule.Validate();
        if (node.Parameters["coordinatedCascadeId"] is not JsonValue value || !value.TryGetValue<string>(out var cascade))
            throw new ArgumentException("Identificação da cascata inválida.");
        return new(schedule, string.IsNullOrWhiteSpace(cascade) ? null : cascade);
    }

    private static RecipeKlaCondition Condition(double rpm, double flow, int replicates)
    {
        if (!double.IsFinite(rpm) || rpm != Math.Truncate(rpm) || rpm < 15 || rpm > 1000 ||
            !double.IsFinite(flow) || flow <= 0 || flow > DeviceRanges.For(SetpointVariable.Flow).Max || replicates < 1)
            throw new ArgumentException("Condição exige N/Q dentro da faixa e número inteiro de réplicas positivo.");
        return new(rpm, flow, replicates);
    }
    private static int Integer(double value)
    {
        if (!double.IsFinite(value) || value < 1 || value > int.MaxValue || value != Math.Truncate(value))
            throw new ArgumentException("Quantidade precisa ser um inteiro positivo.");
        return (int)value;
    }
    private static T Choice<T>(RecipeNode node, string key) where T : struct, Enum
    {
        if (node.Parameters[key] is not JsonValue field || !field.TryGetValue<string>(out var value))
            throw new ArgumentException($"Opção inválida: {key}.");
        if (!Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != value)
            throw new ArgumentException($"Opção inválida: {key}.");
        return parsed;
    }
    private static double Number(JsonObject fields, string key)
        => fields[key] is JsonValue value && RecipeNode.TryReadNumber(value, out var number) ? number :
            throw new ArgumentException($"Valor numérico ausente: {key}.");
    private static bool Boolean(JsonObject fields, string key)
        => fields[key] is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean :
            throw new ArgumentException($"Opção booleana ausente: {key}.");
    private static double Seconds(double value, TimeUnit unit) => value * (unit switch
        { TimeUnit.Seconds => 1, TimeUnit.Minutes => 60, TimeUnit.Hours => 3600, _ => throw new ArgumentException("Unidade inválida.") });
}
