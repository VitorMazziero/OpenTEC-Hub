using System.Collections.Immutable;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

/// <summary>One complete engine dispatch followed by confirmations of every supported destination.</summary>
public sealed class RecipeRampFrameDestination : IRecipeRampDestination, IRecipeRampConfirmationSource, IDisposable
{
    private readonly RecipeEngine _engine;
    private readonly Guid _executionId;
    private readonly string? _cascadeNodeId;
    private readonly ImmutableDictionary<SetpointVariable, IRecipeRampDestination> _destinations;
    private readonly object _gate = new();
    private ImmutableArray<LinearRampSample> _applied = [];
    private ImmutableArray<RecipeRampFinalConfirmation> _confirmations = [];
    private long _revision;
    private bool _disposed;
    private SensorSnapshot? _latestFrame;
    private long _frameSequence;

    public RecipeRampFrameDestination(RecipeEngine engine, RecipeRampBlockConfiguration configuration,
        RecipeRampMotorConfirmationPolicy motor, RecipeRampTemperatureConfirmationPolicy temperature,
        RecipeRampFlowConfirmationPolicy flow)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(configuration);
        configuration.Definition.Validate(); motor.Validate(); temperature.Validate(); flow.Validate();
        if (engine.ExecutionId == Guid.Empty) throw new InvalidOperationException("Quadro exige execução identificada.");
        // Reject unsupported destinations before subscribing or dispatching any command.
        if (configuration.Definition.Lines.Any(line => line.Variable is SetpointVariable.Ph or SetpointVariable.Pressure))
            throw new NotSupportedException("Confirmação de pH e pressão ainda não integrada ao quadro da rampa.");
        if (configuration.Definition.Lines.Any(line => line.Variable == SetpointVariable.Oxygen) && string.IsNullOrWhiteSpace(configuration.CascadeNodeId))
            throw new ArgumentException("Rampa de O₂ requer controle associado.");
        _engine = engine; _executionId = engine.ExecutionId; _cascadeNodeId = configuration.CascadeNodeId;
        _engine.RampTelemetryDevice.TelemetryReceived += OnTelemetry;
        _destinations = configuration.Definition.Lines.ToImmutableDictionary(line => line.Variable,
            line => (IRecipeRampDestination)(line.Variable switch
            {
                SetpointVariable.Agitation => new RecipeRampMotorDestination(engine, motor),
                SetpointVariable.Temperature => new RecipeRampTemperatureDestination(engine, temperature),
                SetpointVariable.Flow => new RecipeRampFlowDestination(engine, flow),
                SetpointVariable.Oxygen => new RecipeRampCascadeDestination(engine, _cascadeNodeId!),
                _ => throw new ArgumentException("Destino desconhecido.")
            }));
    }

    public ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations { get { lock (_gate) return _confirmations; } }

    public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = []; _applied = []; _revision++;
            Validate(references);
            if (!_engine.TryApplyRampMeasuredFrame(references, _cascadeNodeId, 0, _executionId, out var route)) return Task.FromResult(false);
            foreach (var target in references)
                if (_destinations[target.Variable] is RecipeRampMeasuredDestination measured) measured.ObserveAcceptedFrame(target, route);
                else ((RecipeRampCascadeDestination)_destinations[target.Variable]).ObserveAcceptedFrame(target);
            _applied = references;
            return Task.FromResult(true);
        }
    }

    public async Task<bool> TryConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); long revision;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _confirmations = []; Validate(references);
            if (references.Any(target => !target.AtFinalTarget)) throw new ArgumentException("Quadro exige todos os alvos finais.");
            if (!_applied.SequenceEqual(references)) return false;
            revision = _revision;
        }
        var confirmed = await Task.WhenAll(references.Select(target =>
            _destinations[target.Variable].TryConfirmFinalAsync([target], cancellation))).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frame = Volatile.Read(ref _latestFrame);
            var sequence = Volatile.Read(ref _frameSequence);
            if (revision != _revision || !_applied.SequenceEqual(references) || confirmed.Any(value => !value) ||
                _destinations.Values.Any(destination => destination is RecipeRampMeasuredDestination measured
                    ? !measured.HasCurrentFrameConfirmation(frame) : !((RecipeRampCascadeDestination)destination).HasCurrentFrameConfirmation()) ||
                sequence != Volatile.Read(ref _frameSequence) || !ReferenceEquals(frame, Volatile.Read(ref _latestFrame))) return false;
            _confirmations = references.SelectMany(target => ((IRecipeRampConfirmationSource)_destinations[target.Variable]).FinalConfirmations).ToImmutableArray();
            return true;
        }
    }

    public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Quadro final ainda não confirmado.");
    }

    private void Validate(ImmutableArray<LinearRampSample> references)
    {
        if (references.IsDefaultOrEmpty || references.Length != _destinations.Count ||
            references.Select(target => target.Variable).Distinct().Count() != references.Length ||
            references.Any(target => !_destinations.ContainsKey(target.Variable))) throw new ArgumentException("Quadro incompatível com a rampa.");
        foreach (var target in references)
            if (_destinations[target.Variable] is RecipeRampMeasuredDestination measured) measured.ValidateFrameTarget(target);
            else ((RecipeRampCascadeDestination)_destinations[target.Variable]).ValidateFrameTarget(target);
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        Volatile.Write(ref _latestFrame, snapshot);
        Interlocked.Increment(ref _frameSequence);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _confirmations = []; _applied = []; _revision++;
            _engine.RampTelemetryDevice.TelemetryReceived -= OnTelemetry;
            foreach (var destination in _destinations.Values.OfType<IDisposable>()) destination.Dispose();
        }
    }
}
