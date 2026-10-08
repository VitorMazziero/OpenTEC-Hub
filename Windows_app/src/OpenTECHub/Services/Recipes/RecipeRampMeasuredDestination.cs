using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public record RecipeRampMeasuredConfirmationPolicy(double Tolerance, TimeSpan Stability,
    TimeSpan MaximumSampleGap, TimeSpan Timeout)
{
    public void Validate()
    {
        if (!double.IsFinite(Tolerance) || Tolerance < 0 || Stability < TimeSpan.Zero ||
            MaximumSampleGap <= TimeSpan.Zero || Timeout <= TimeSpan.Zero || Stability >= Timeout ||
            Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Limites de confirmação da referência inválidos.");
    }
}

public sealed record RecipeRampMeasuredRoute(bool MotorViaUart, bool TemperatureViaBath, GasRigConfiguration? GasRig = null);
public sealed record RecipeRampMeasuredProof(double ObservedValue, double Tolerance, RecipeRampConfirmationEvidence Evidence);

/// <summary>Fresh, stable destination confirmation. Wrap in the ramp producer's guarded destination.</summary>
public abstract class RecipeRampMeasuredDestination : IRecipeRampDestination, IRecipeRampConfirmationSource, IDisposable
{
    private readonly RecipeEngine _engine;
    private readonly IDeviceService _device;
    private readonly TimeProvider _time;
    protected RecipeRampMeasuredConfirmationPolicy Policy { get; }
    protected abstract SetpointVariable Variable { get; }
    protected abstract RecipeRampMeasuredProof? Evaluate(SensorSnapshot snapshot, LinearRampSample target, RecipeRampMeasuredRoute route);
    protected virtual void ValidateReference(LinearRampSample target) { }
    private readonly Guid _executionId;
    private readonly object _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SensorSnapshot? _latest;
    private long _sequence, _receivedSequence, _receivedAt, _afterApplication, _applicationRevision;
    private RecipeRampMeasuredRoute? _route;
    private bool _disposed;
    private LinearRampSample? _lastApplied;
    private ImmutableArray<RecipeRampFinalConfirmation> _confirmations = [];

    protected RecipeRampMeasuredDestination(RecipeEngine engine, RecipeRampMeasuredConfirmationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (engine.ExecutionId == Guid.Empty) throw new InvalidOperationException("Destino exige execução identificada.");
        _engine = engine; _device = engine.RampTelemetryDevice; _time = engine.RampTimeProvider;
        Policy = policy; _executionId = engine.ExecutionId;
        _device.TelemetryReceived += OnTelemetry;
        _engine.StateChanged += OnStateChanged;
    }

    public ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations { get { lock (_gate) return _confirmations; } }

    public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var target = Target(references);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = []; _lastApplied = null; _applicationRevision++;
            _signal.TrySetResult(); _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_engine.TryApplyRampMeasuredReference(target, _executionId, out _route)) return Task.FromResult(false);
            _lastApplied = target;
            // Only samples received after acceptance can confirm this application.
            _afterApplication = Volatile.Read(ref _receivedSequence);
            return Task.FromResult(true);
        }
    }

    public async Task<bool> TryConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        var target = Target(references);
        if (!target.AtFinalTarget) throw new ArgumentException("Confirmação exige alvo final.", nameof(references));
        cancellation.ThrowIfCancellationRequested();
        long observed, applicationRevision;
        RecipeRampMeasuredRoute route;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = [];
            if (_lastApplied != target) return false;
            observed = _afterApplication; route = _route!; applicationRevision = _applicationRevision;
        }
        using var deadline = new CancellationTokenSource(Policy.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        long? stableSince = null, previous = null;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                SensorSnapshot? snapshot;
                long sequence, received;
                Task signal;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_applicationRevision != applicationRevision) return false;
                    snapshot = _latest; sequence = _sequence; received = _receivedAt; signal = _signal.Task;
                }
                // Capture the wait handle before checking state: a pause after this point wakes it.
                var availability = _engine.RampMeasuredAvailability(_executionId, Variable, route);
                if (availability == RecipeRampDestinationAvailability.Suspended) return false;
                if (availability == RecipeRampDestinationAvailability.Unavailable)
                    throw new InvalidOperationException("Rampa perdeu execução, rota ou posse do destino.");
                if (sequence <= observed) { await signal.WaitAsync(linked.Token).ConfigureAwait(false); continue; }
                if (sequence != observed + 1 || previous is { } prior && _time.GetElapsedTime(prior, received) > Policy.MaximumSampleGap)
                    stableSince = null;
                observed = sequence; previous = received;
                var proof = snapshot is not null ? Evaluate(snapshot, target, route) : null;
                var valid = proof is not null && double.IsFinite(proof.ObservedValue) && double.IsFinite(proof.Tolerance) &&
                    proof.Tolerance >= 0 && Math.Abs(proof.ObservedValue - target.Reference) <= proof.Tolerance &&
                    proof.Evidence is RecipeRampConfirmationEvidence.ProcessFeedback or RecipeRampConfirmationEvidence.DeviceReferenceReadback &&
                    _time.GetElapsedTime(received) <= Policy.MaximumSampleGap;
                if (!valid) { stableSince = null; continue; }
                stableSince ??= received;
                if (_time.GetElapsedTime(stableSince.Value, received) < Policy.Stability) continue;
                linked.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_sequence != sequence) continue;
                    if (_applicationRevision != applicationRevision || _lastApplied != target || _afterApplication >= sequence ||
                        _engine.RampMeasuredAvailability(_executionId, Variable, route) != RecipeRampDestinationAvailability.Available) return false;
                    _confirmations = [new(Variable, null, target.Reference,
                        proof!.Evidence, _time.GetUtcNow(), proof.ObservedValue, proof.Tolerance)];
                    return true;
                }
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        { throw new TimeoutException("Prazo de confirmação final da referência esgotado.", error); }
    }

    public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Referência final ainda não confirmada.");
    }

    private LinearRampSample Target(ImmutableArray<LinearRampSample> references)
    {
        if (references.IsDefault || references.Length != 1 || references[0].Variable != Variable ||
            references[0].OxygenTarget is not null || !double.IsFinite(references[0].Reference))
            throw new ArgumentException("Parâmetro incompatível com o destino da rampa.", nameof(references));
        ValidateReference(references[0]);
        return references[0];
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        var sequence = Interlocked.Increment(ref _receivedSequence);
        var received = _time.GetTimestamp();
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_disposed || sequence <= _sequence) return;
            _latest = snapshot; _receivedAt = received; _sequence = sequence;
            signal = _signal; _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    private void OnStateChanged()
    {
        // Wake without taking the destination lock from an engine callback.
        var signal = Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously));
        signal.TrySetResult();
    }

    public void Dispose()
    {
        TaskCompletionSource signal;
        lock (_gate) { if (_disposed) return; _disposed = true; _confirmations = []; signal = _signal; }
        _device.TelemetryReceived -= OnTelemetry;
        _engine.StateChanged -= OnStateChanged;
        signal.TrySetResult();
    }
}
