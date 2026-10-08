using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampMotorConfirmationPolicy(double ToleranceRpm, TimeSpan Stability,
    TimeSpan MaximumSampleGap, TimeSpan Timeout)
{
    public void Validate()
    {
        if (!double.IsFinite(ToleranceRpm) || ToleranceRpm < 0 || Stability < TimeSpan.Zero ||
            MaximumSampleGap <= TimeSpan.Zero || Timeout <= TimeSpan.Zero || Stability >= Timeout ||
            Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Limites de confirmação da rotação inválidos.");
    }
}

/// <summary>Measured motor-speed component. Wrap in the ramp producer's guarded destination.</summary>
public sealed class RecipeRampMotorDestination : IRecipeRampDestination, IRecipeRampConfirmationSource, IDisposable
{
    private readonly RecipeEngine _engine;
    private readonly IDeviceService _device;
    private readonly TimeProvider _time;
    private readonly RecipeRampMotorConfirmationPolicy _policy;
    private readonly Guid _executionId;
    private readonly object _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SensorSnapshot? _latest;
    private long _sequence, _receivedSequence, _receivedAt, _afterApplication, _applicationRevision;
    private bool _uart, _disposed;
    private LinearRampSample? _lastApplied;
    private ImmutableArray<RecipeRampFinalConfirmation> _confirmations = [];

    public RecipeRampMotorDestination(RecipeEngine engine, RecipeRampMotorConfirmationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (engine.ExecutionId == Guid.Empty) throw new InvalidOperationException("Destino exige execução identificada.");
        _engine = engine; _device = engine.RampTelemetryDevice; _time = engine.RampTimeProvider;
        _policy = policy; _executionId = engine.ExecutionId;
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
            if (!_engine.TryApplyRampMotorReference(target, _executionId, out _uart)) return Task.FromResult(false);
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
        bool uart;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _confirmations = [];
            if (_lastApplied != target) return false;
            observed = _afterApplication; uart = _uart; applicationRevision = _applicationRevision;
        }
        using var deadline = new CancellationTokenSource(_policy.Timeout, _time);
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
                var availability = _engine.RampMotorAvailability(_executionId, uart);
                if (availability == RecipeRampDestinationAvailability.Suspended) return false;
                if (availability == RecipeRampDestinationAvailability.Unavailable)
                    throw new InvalidOperationException("Rampa perdeu execução, rota ou posse do motor.");
                if (sequence <= observed) { await signal.WaitAsync(linked.Token).ConfigureAwait(false); continue; }
                if (sequence != observed + 1 || previous is { } prior && _time.GetElapsedTime(prior, received) > _policy.MaximumSampleGap)
                    stableSince = null;
                observed = sequence; previous = received;
                var valid = snapshot is not null && snapshot.HasServoTelemetry && snapshot.HasServoSample && snapshot.ServoOnline &&
                    snapshot.ServoCommEnabled == true && snapshot.ServoCommandPending == false && snapshot.ServoAlarm == 0 &&
                    snapshot.MotorControlViaModbus == !uart && snapshot.ServoMotorRouteAck == (uart ? 0 : 1) &&
                    double.IsFinite(snapshot.ServoRpm) && Math.Abs(snapshot.ServoRpm - target.Reference) <= _policy.ToleranceRpm &&
                    _time.GetElapsedTime(received) <= _policy.MaximumSampleGap;
                if (!valid) { stableSince = null; continue; }
                stableSince ??= received;
                if (_time.GetElapsedTime(stableSince.Value, received) < _policy.Stability) continue;
                linked.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_sequence != sequence) continue;
                    if (_applicationRevision != applicationRevision || _lastApplied != target || _afterApplication >= sequence ||
                        _engine.RampMotorAvailability(_executionId, uart) != RecipeRampDestinationAvailability.Available) return false;
                    _confirmations = [new(SetpointVariable.Agitation, null, target.Reference,
                        RecipeRampConfirmationEvidence.ProcessFeedback, _time.GetUtcNow(), snapshot!.ServoRpm, _policy.ToleranceRpm)];
                    return true;
                }
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        { throw new TimeoutException("Prazo de confirmação da rotação esgotado.", error); }
    }

    public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Rotação final ainda não confirmada.");
    }

    private static LinearRampSample Target(ImmutableArray<LinearRampSample> references)
    {
        if (references.IsDefault || references.Length != 1 || references[0].Variable != SetpointVariable.Agitation ||
            references[0].OxygenTarget is not null || !double.IsFinite(references[0].Reference) ||
            references[0].Reference != Math.Truncate(references[0].Reference))
            throw new ArgumentException("Destino do motor exige uma referência de agitação quantizada.", nameof(references));
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
