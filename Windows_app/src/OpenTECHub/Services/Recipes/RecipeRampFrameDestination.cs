using System.Collections.Immutable;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

/// <summary>One complete engine dispatch followed by confirmations of every supported destination.</summary>
public sealed class RecipeRampFrameDestination : IRecipeRampReservedDestination, IRecipeRampConfirmationSource, IDisposable
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
    private readonly double _phInactiveBand;
    private readonly RampTemperatureRoute? _temperatureRoute;

    public RecipeRampFrameDestination(RecipeEngine engine, RecipeRampBlockConfiguration configuration,
        RecipeRampMotorConfirmationPolicy motor, RecipeRampTemperatureConfirmationPolicy temperature,
        RecipeRampFlowConfirmationPolicy flow, RecipeRampMeasuredConfirmationPolicy? ph = null,
        RecipeRampMeasuredConfirmationPolicy? pressure = null, double? phInactiveBand = null)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(configuration);
        configuration.Definition.Validate(); motor.Validate(); temperature.Validate(); flow.Validate();
        if (engine.ExecutionId == Guid.Empty) throw new InvalidOperationException("Quadro exige execução identificada.");
        if (configuration.Definition.Lines.Any(line => line.Variable == SetpointVariable.Ph))
        {
            if (ph is null || phInactiveBand is not { } band || !double.IsFinite(band) || band < 0)
                throw new ArgumentException("Quadro de pH requer política de confirmação e banda preservada.");
            ph.Validate();
        }
        if (configuration.Definition.Lines.Any(line => line.Variable == SetpointVariable.Pressure))
        {
            if (pressure is null) throw new ArgumentException("Quadro de pressão requer política de confirmação.");
            pressure.Validate();
        }
        _phInactiveBand = phInactiveBand is { } savedBand ? Math.Round(savedBand, 2, MidpointRounding.AwayFromZero) : 0;
        _temperatureRoute = configuration.Definition.Lines.Any(line => line.Variable == SetpointVariable.Temperature)
            ? configuration.TemperatureRoute ?? engine.RampTemperatureRoute : null;
        if (_temperatureRoute is { } temperaturePath && !Enum.IsDefined(temperaturePath)) throw new ArgumentException("Rota de temperatura inválida.");
        if (configuration.Definition.Lines.Any(line => line.Variable == SetpointVariable.Oxygen) && string.IsNullOrWhiteSpace(configuration.CascadeNodeId))
            throw new ArgumentException("Rampa de O₂ requer controle associado.");
        _engine = engine; _executionId = engine.ExecutionId; _cascadeNodeId = configuration.CascadeNodeId;
        _engine.RampTelemetryDevice.TelemetryReceived += OnTelemetry;
        _destinations = configuration.Definition.Lines.ToImmutableDictionary(line => line.Variable,
            line => (IRecipeRampDestination)(line.Variable switch
            {
                SetpointVariable.Agitation => new RecipeRampMotorDestination(engine, motor),
                SetpointVariable.Temperature => new RecipeRampTemperatureDestination(engine, temperature, _temperatureRoute),
                SetpointVariable.Flow => new RecipeRampFlowDestination(engine, flow),
                SetpointVariable.Oxygen => new RecipeRampCascadeDestination(engine, _cascadeNodeId!),
                SetpointVariable.Ph => new RecipeRampSensorModuleDestination(engine, SetpointVariable.Ph, ph!, _phInactiveBand),
                SetpointVariable.Pressure => new RecipeRampSensorModuleDestination(engine, SetpointVariable.Pressure, pressure!),
                _ => throw new ArgumentException("Destino desconhecido.")
            }));
    }

    public ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations { get { lock (_gate) return _confirmations; } }

    public async Task<RecipeRampTerminalCheckpoint> RestoreAndConfirmAsync(RecipeRampStartCheckpoint start,
        Communication.CommandAuthorityLease authority, RecipeRampTerminalStatus status, double activeSeconds,
        string? reason, CancellationToken cancellation)
    {
        if (status is not (RecipeRampTerminalStatus.Cancelled or RecipeRampTerminalStatus.Faulted) ||
            !double.IsFinite(activeSeconds) || activeSeconds < 0)
            throw new ArgumentException("Retorno exige encerramento por cancelamento/falha e tempo ativo válido.");
        if (!await TryRestoreDirectAsync(start, authority, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Quadro de retorno não aceito pelos destinos.");
        var references = RecipeRampRestoreCommands.Build(start, authority).References;
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Referências anteriores ainda não confirmadas.");
        lock (_gate)
        {
            if (!_applied.SequenceEqual(references) || _confirmations.Length != references.Length)
                throw new InvalidOperationException("Quadro de retorno mudou após confirmação.");
            return new(2, start.InitialState.ExecutionId, start.InvocationId, start.InitialState.SnapshotId,
                start.InitialState.NodeId, status, RecipeRampReturnOutcome.RestoredSnapshot, activeSeconds,
                _engine.RampTimeProvider.GetUtcNow(), reason, [])
                { Recovery = new(start.InitialState.SnapshotId, _confirmations, start.InitialState.Controller) };
        }
    }

    public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        => TryApplyFrameAsync(references, null, cancellation);

    public Task<bool> TryApplyReservedAsync(ImmutableArray<LinearRampSample> references,
        Communication.CommandAuthorityLease authority, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return TryApplyFrameAsync(references, authority, cancellation);
    }

    private Task<bool> TryApplyFrameAsync(ImmutableArray<LinearRampSample> references,
        Communication.CommandAuthorityLease? authority, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = []; _applied = []; _revision++;
            Validate(references);
            if (!_engine.TryApplyRampMeasuredFrame(references, _cascadeNodeId, _phInactiveBand, _executionId, out var route, _temperatureRoute, authority)) return Task.FromResult(false);
            foreach (var target in references)
                if (_destinations[target.Variable] is RecipeRampMeasuredDestination measured) measured.ObserveAcceptedFrame(target, route);
                else ((RecipeRampCascadeDestination)_destinations[target.Variable]).ObserveAcceptedFrame(target);
            _applied = references;
            return Task.FromResult(true);
        }
    }

    /// <summary>Restore all captured direct configuration, then use the same destination proofs
    /// for the previous references. The caller keeps producers quiescent and drains the reservation.</summary>
    public Task<bool> TryRestoreDirectAsync(RecipeRampStartCheckpoint start,
        Communication.CommandAuthorityLease authority, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var frame = RecipeRampRestoreCommands.Build(start, authority);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = []; _applied = []; _revision++;
            Validate(frame.References);
            if (start.InitialState.Controller is { } captured && captured.ControllerId != _cascadeNodeId)
                throw new InvalidOperationException("Controle capturado diverge do destino da rampa.");
            if (frame.References.Any(target => target.Variable == SetpointVariable.Ph) &&
                RecipeAssayReturnState.Number(OpenTECCommand.Parse(frame.CommandJson), CommandKeys.PHError) != _phInactiveBand)
                throw new InvalidOperationException("Confirmação do retorno exige a banda de pH capturada.");
            if (!_engine.TryDispatchRampRestoration(frame, _executionId, authority, _temperatureRoute, out var route, start.InitialState.Controller))
                return Task.FromResult(false);
            foreach (var target in frame.References)
                if (_destinations[target.Variable] is RecipeRampMeasuredDestination measured)
                    measured.ObserveAcceptedFrame(target, route, authority);
                else ((RecipeRampCascadeDestination)_destinations[target.Variable]).ObserveRestoredFrame(target, start.InitialState.Controller!, authority);
            _applied = frame.References;
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
