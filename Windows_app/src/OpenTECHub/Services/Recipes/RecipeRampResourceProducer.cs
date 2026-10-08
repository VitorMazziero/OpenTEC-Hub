using OpenTECHub.Services.Communication;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

/// <summary>Quiesces the complete ramp dispatch before an assay can capture and take authority.</summary>
public sealed class RecipeRampResourceProducer : IRecipeResourceProducer, IDisposable
{
    private readonly RecipeCascadeSuspensionGate _dispatch = new();
    private readonly RecipeRampActiveClock _clock;
    private readonly CancellationToken _stopToken;
    public string NodeId { get; }
    public IReadOnlyList<ActuatorId> Resources { get; }
    public CancellationToken StopToken => _stopToken;
    internal RecipeRampActiveClock ActiveClock => _clock;

    public RecipeRampResourceProducer(string nodeId, IReadOnlyList<ActuatorId> resources,
        RecipeRampActiveClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(clock);
        if (resources.Count == 0) throw new ArgumentException("Rampa sem recursos.", nameof(resources));
        NodeId = nodeId;
        Resources = Array.AsReadOnly(resources.Distinct().ToArray());
        _clock = clock;
        _stopToken = _dispatch.StopToken;
    }

    // The destination must hold this lease across sampling and the complete dispatch.
    public RecipeCascadeSuspensionGate.StepLease? TryEnterStep() => _dispatch.TryEnterStep();

    public async Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct)
    {
        var reason = $"assay:{Guid.NewGuid():N}";
        _clock.Suspend(reason);
        try
        {
            var receipt = await _dispatch.PauseAsync(ct).ConfigureAwait(false);
            return new Suspension(receipt, _clock, reason);
        }
        catch
        {
            _clock.Resume(reason);
            throw;
        }
    }

    private sealed class Suspension(RecipeCascadeSuspensionGate.PauseReceipt receipt,
        RecipeRampActiveClock clock, string reason) : IRecipeResourceSuspension
    {
        public bool CanResume => receipt.CanResume;
        public void Resume()
        {
            // A stale receipt must neither open dispatch nor remove a newer suspension.
            receipt.Resume();
            clock.Resume(reason);
        }
        public void Stop()
        {
            clock.Suspend("stopped");
            receipt.Stop();
        }
    }

    public void Dispose()
    {
        _clock.Suspend("stopped");
        _dispatch.Dispose();
    }
}
