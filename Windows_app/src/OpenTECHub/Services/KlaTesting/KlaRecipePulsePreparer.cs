using System.Collections.Immutable;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public interface IKlaRecipePreparedPulse : IDisposable
{
    KlaAssayApiRequest Request { get; }
    IKlaAssayExecution Execution { get; }
    RunPhase? Phase { get; }
    bool HasStarted { get; }
    bool HasReturnedSuccessfully { get; }
}

public interface IKlaRecipePulsePreparer
{
    Task<IKlaRecipePreparedPulse> PrepareAsync(KlaRecipeRequest template, KlaQueueItem item,
        KlaTestDocument document, DateTimeOffset acquisitionDeadlineUtc, CancellationToken ct);
}

/// <summary>Pauses producers and captures a fresh target; ownership transfers only when E6 dispatches.</summary>
public sealed class KlaRecipePulsePreparer(RecipeResourceCoordinator coordinator,
    KlaRecipeAssayExecutionFactory factory, ISettingsService settings, TimeProvider time,
    KlaAssayExecutionCapabilities capabilities, RecipeAssayRecoveryCriteria recoveryCriteria,
    TimeSpan reservationTimeout) : IKlaRecipePulsePreparer
{
    public async Task<IKlaRecipePreparedPulse> PrepareAsync(KlaRecipeRequest template, KlaQueueItem item,
        KlaTestDocument document, DateTimeOffset acquisitionDeadlineUtc, CancellationToken ct)
    {
        var lease = await coordinator.ReserveForAssayAsync(template.Context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], reservationTimeout, ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = lease.CaptureReturnSnapshot(settings.Current.GasRig.ToConfiguration(), time);
            var invocation = template with { Restoration = template.Restoration with { BeforeAssay = snapshot },
                AcquisitionDeadlineUtc = acquisitionDeadlineUtc };
            var startDeadline = time.GetUtcNow().Add(reservationTimeout);
            if (startDeadline >= acquisitionDeadlineUtc) startDeadline = acquisitionDeadlineUtc.AddTicks(-1);
            var request = KlaRecipePulseMapper.Create(invocation, capabilities.InstallationId, item.ConditionId,
                item.ReplicateNumber, item.AttemptNumber, startDeadline);
            capabilities.EnsureAllows(request);
            var execution = factory.Create(lease, document, recoveryCriteria, capabilities);
            return new ReservedPulse(lease, request, execution);
        }
        catch { lease.AbortBeforeAssay(); throw; }
    }

    private sealed class ReservedPulse(RecipeAssayResourceLease lease, KlaAssayApiRequest request,
        KlaRecipeAssayExecution execution) : IKlaRecipePreparedPulse, IKlaAssayExecution
    {
        private int _started;
        public KlaAssayApiRequest Request { get; } = request;
        public IKlaAssayExecution Execution => this;
        public bool IsValidated => execution.IsValidated;
        public KlaAssayExecutionCapabilities Capabilities => execution.Capabilities;
        public RunPhase? Phase => execution.Phase;
        public bool HasStarted => Volatile.Read(ref _started) != 0;
        public bool HasReturnedSuccessfully => lease.HasReturnedSuccessfully;
        public async Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest pulse, CancellationToken token)
        {
            if (JsonSerializer.Serialize(pulse) != JsonSerializer.Serialize(Request) || Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("Escopo não corresponde à tentativa ou já foi usado.");
            try
            {
                lease.BeginAssay(Request.RecipePulse!.Invocation.Restoration.BeforeAssay);
                return await execution.ExecuteWithRecoveryAsync(pulse, token).ConfigureAwait(false);
            }
            catch { lease.Fail(); throw; }
        }
        public void Dispose()
        {
            if (!HasStarted) lease.AbortBeforeAssay();
            else if (!HasReturnedSuccessfully) lease.Fail();
        }
    }
}
