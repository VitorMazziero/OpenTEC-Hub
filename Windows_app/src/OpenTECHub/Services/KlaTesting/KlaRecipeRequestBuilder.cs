using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaRecipeRequestBuilder
{
    /// <summary>The caller must capture this snapshot while producers are quiescent and keep that lease for the first pulse.</summary>
    public static KlaRecipeRequest Build(RecipeInvocationContext context, RecipeKlaBlockConfiguration configuration,
        KlaRecipeOperationalProfile profile, KlaReturnSnapshot snapshot, DateTimeOffset now,
        PeriodicBlockInvocation? periodic = null, KlaMeasurementSource source = KlaMeasurementSource.Simulation)
    {
        context.Validate(); profile.Validate(now);
        KlaRecipeOperationalProfileRegistry.ValidateConfiguration(profile, configuration);
        RecipeAssayReturnState.Validate(snapshot);
        if (snapshot.CapturedUtc > now || snapshot.Actuators.Any(a => a.Owner != CommandOwner.Recipe ||
            a.OwnerExecutionId != context.RecipeRunId.ToString()))
            throw new ArgumentException("Snapshot não pertence à execução coordenada da receita.");
        var rows = configuration.ConditionsMode == RecipeKlaConditionMode.SingleAtCurrentCondition
            ? ImmutableArray.Create(new RecipeKlaCondition(snapshot.AgitationSetpointRpm, snapshot.AirflowSetpointLpm, 1))
            : configuration.Conditions;
        if (rows.IsDefaultOrEmpty || rows.Any(c => !double.IsFinite(c.AgitationRpm) || c.AgitationRpm < 15 ||
            c.AgitationRpm > 1000 || c.AgitationRpm != Math.Truncate(c.AgitationRpm) ||
            !double.IsFinite(c.AirflowLpm) || c.AirflowLpm <= 0 || c.AirflowLpm > DeviceRanges.For(SetpointVariable.Flow).Max || c.Replicates < 1))
            throw new ArgumentException("Condições do ensaio devem possuir N/Q positivos dentro da faixa.");
        var measurement = profile.Template.Context ?? new KlaMeasurementContext();
        if (!string.IsNullOrWhiteSpace(measurement.CultivationId) && measurement.CultivationId != context.CultivationId)
            throw new ArgumentException("Perfil científico pertence a outro cultivo.");
        var deadline = now.AddSeconds(configuration.Retry.MaximumBlockSeconds);
        if (deadline > profile.ValidUntilUtc) deadline = profile.ValidUntilUtc;
        var request = new KlaRecipeRequest
        {
            Context = context, PeriodicInvocation = periodic,
            Definition = profile.Template with
            {
                Context = measurement with { CultivationId = context.CultivationId, Source = source },
                CaptureMode = configuration.ConditionsMode == RecipeKlaConditionMode.Multiple ? KlaCaptureMode.Multiple : KlaCaptureMode.Single,
                SequenceLimits = null,
                Conditions = rows.Select((row, index) => new KlaAssayCondition(ConditionId(context, index), index,
                    row.AgitationRpm, row.AirflowLpm, row.Replicates)).ToImmutableArray()
            },
            Quality = profile.Quality with { RequireValidOur = profile.Quality.RequireValidOur || configuration.RequireValidOur },
            Retry = configuration.Retry with { RecoverableReasons = configuration.UseProfileRetryReasons && configuration.Retry.RecoverableReasons.IsEmpty
                ? profile.MaximumRetry.RecoverableReasons : configuration.Retry.RecoverableReasons },
            Restoration = new() { BeforeAssay = snapshot, MaximumRecoverySeconds = profile.MaximumRecoverySeconds,
                StabilitySeconds = profile.RecoveryStabilitySeconds, AgitationToleranceRpm = profile.AgitationToleranceRpm,
                FlowToleranceLpm = profile.FlowToleranceLpm },
            AcquisitionDeadlineUtc = deadline, FailurePolicy = configuration.FailurePolicy
        };
        return RecipeContractSerializer.Snapshot(request);
    }

    private static Guid ConditionId(RecipeInvocationContext context, int index)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.IdempotencyKey}/condition/{index}")).AsSpan(0, 16));
}
