using System.Collections.Immutable;

namespace OpenTECHub.Services.Recipes;

public interface IRecipeRampConfirmationSource
{
    ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations { get; }
}

/// <summary>The cascade-reference component of ramp destinations. Use inside the producer's guarded destination.</summary>
public sealed class RecipeRampCascadeDestination : IRecipeRampDestination, IRecipeRampConfirmationSource
{
    private readonly RecipeEngine _engine;
    private readonly string _cascadeNodeId;
    private readonly Guid _executionId;
    private readonly object _gate = new();
    private LinearRampSample? _lastApplied;
    private ImmutableArray<RecipeRampFinalConfirmation> _confirmations = [];

    public RecipeRampCascadeDestination(RecipeEngine engine, string cascadeNodeId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(cascadeNodeId);
        if (engine.ExecutionId == Guid.Empty) throw new InvalidOperationException("Destino da rampa exige execução identificada.");
        _engine = engine; _cascadeNodeId = cascadeNodeId; _executionId = engine.ExecutionId;
    }

    public ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations
    {
        get { lock (_gate) return _confirmations; }
    }

    public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var reference = Reference(references);
        lock (_gate)
        {
            _confirmations = []; _lastApplied = null;
            if (!_engine.TryApplyRampFrame(references, _cascadeNodeId, 0, _executionId))
                return Task.FromResult(false);
            _lastApplied = reference;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var reference = Reference(references);
        if (!reference.AtFinalTarget) throw new ArgumentException("Confirmação exige alvo final.", nameof(references));
        lock (_gate)
        {
            _confirmations = [];
            if (_lastApplied != reference) return Task.FromResult(false);
            var confirmation = _engine.TryConfirmRampCascadeReference(_cascadeNodeId, reference, _executionId);
            if (confirmation is null) return Task.FromResult(false);
            cancellation.ThrowIfCancellationRequested();
            _confirmations = [confirmation];
            return Task.FromResult(true);
        }
    }

    public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Referência da cascata ainda não confirmada ou controle indisponível.");
    }

    private static LinearRampSample Reference(ImmutableArray<LinearRampSample> references)
    {
        if (references.IsDefault || references.Length != 1 || references[0].Variable != SetpointVariable.Oxygen ||
            references[0].OxygenTarget != RampOxygenTarget.ActiveCascadeReference)
            throw new ArgumentException("Este destino recebe apenas a referência de O₂ da cascata.", nameof(references));
        return references[0];
    }
}
