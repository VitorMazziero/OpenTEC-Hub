using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.Services.Recipes;

/// <summary>
/// The recipe execution engine (Phase 3 WP4 part 2). Core lifecycle; the flow walk, per-block
/// execution, actuation, cascade, safety and live tuning live in the sibling partials.
/// </summary>
public sealed partial class RecipeEngine : IRecipeEngine, IAsyncDisposable
{
    private readonly ICommandArbiter _arbiter;
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly IEventJournal? _journal;
    private readonly IKlaProfileStore? _klaStore;
    public RecipeResourceCoordinator? Resources { get; }
    public Guid ExecutionId { get; private set; }

    /// <summary>How a block waits — injected so tests run without wall-clock sleeps.</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Set while running, reset while paused; the flow loop waits on it between blocks.</summary>
    private readonly ManualResetEventSlim _pauseGate = new(true);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, NodeState> _nodeStates = [];
    private readonly HashSet<string> _executedConnections = [];

    /// <summary>Replaced each telemetry frame; block handlers await it to step on live data.</summary>
    private volatile TaskCompletionSource _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private DateTimeOffset _startedAt;
    private SensorSnapshot? _latest;
    private long _frameVersion;
    private readonly Queue<(SensorSnapshot Snapshot, long Version)> _cascadeFrames = new();

    // Last route the Hub itself reported for temperature. A frame without bath telemetry
    // (partial/legacy) says nothing about the route and must not reset this.
    private volatile bool _hubRoutesTemperatureToBath;
    private readonly MotorRouteCoordinator _routeCoordinator;
    private bool _disposed;

    public RecipeEngine(
        ICommandArbiter arbiter,
        IDeviceService device,
        ISettingsService settings,
        TimeProvider time,
        IEventJournal? journal = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IKlaProfileStore? klaStore = null,
        IRecipePeriodicWorkSource? periodicWorkSource = null,
        IRecipeAutonomousWorkSource? autonomousWorkSource = null)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);

        _arbiter = arbiter;
        _device = device;
        _settings = settings;
        _time = time;
        _journal = journal;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
        _klaStore = klaStore;
        _periodicWorkSource = periodicWorkSource;
        _autonomousWorkSource = autonomousWorkSource;
        Resources = arbiter is ICommandAuthorityArbiter authority ? new RecipeResourceCoordinator(authority, time) : null;
        _routeCoordinator = new MotorRouteCoordinator(arbiter, device, CommandOwner.Recipe);

        _device.TelemetryReceived += OnTelemetry;
        _arbiter.OwnershipRevoked += OnOwnershipRevoked;
    }

    public MotorRouteCoordinator RouteCoordinator => _routeCoordinator;

    public RecipeDocument? Current { get; private set; }

    public Task Completion => _run;

    public TimeSpan Elapsed => State is RecipeRunState.Idle ? TimeSpan.Zero : _time.GetUtcNow() - _startedAt;

    public bool CanStart(RecipeDocument recipe, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            reason = "Uma receita já está em execução.";
            return false;
        }

        if (!RecipeValidator.Validate(recipe).IsValid)
        {
            reason = "A receita tem erros de validação.";
            return false;
        }

        if (recipe.Nodes.Any(n => n.Type is NodeType.KlaAssay or NodeType.Periodic))
        {
            if (_autonomousWorkSource is null || Resources is null)
            {
                reason = "Os blocos autônomos requerem integração e perfil operacional qualificado antes da execução.";
                return false;
            }
            if (!_autonomousWorkSource.CanExecute(recipe, out reason)) return false;
        }

        if (_device.State is not ConnectionState.Connected)
        {
            reason = "Conecte-se ao equipamento antes de iniciar a receita.";
            return false;
        }

        // The engine will claim every actuator; a live cascade (Automatic) must be stood down
        // first, so the operator makes that handover deliberately rather than the recipe seizing it.
        if (CommandActuators.All.Any(a => _arbiter.OwnerOf(a) == CommandOwner.Automatic))
        {
            reason = "Desative o Automático (cascata) antes de iniciar a receita.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task StartAsync(RecipeDocument recipe, bool resetLoopsBeforeStart = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (!CanStart(recipe, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        // Snapshot the run task the previous run left behind, so a fast restart still awaits it.
        await _run.ConfigureAwait(false);

        Current = recipe;
        ExecutionId = Guid.NewGuid();
        PreparePeriodicWork(recipe);
        ResetNodeStates(recipe);
        ResetFlowState();
        SetWaiting(null);
        lock (_lock)
        {
            _executedConnections.Clear();
        }

        StatusReason = null;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startedAt = _time.GetUtcNow();
        _pauseGate.Set();

        // Claiming every actuator is what deactivates manual control (§5.3.3 / WP4 point 3).
        ClaimAllActuators($"receita '{recipe.Name}' iniciada");
        _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
        if (!_routeCoordinator.RouteRequestAccepted) { _arbiter.Release(CommandOwner.Recipe, routeMsg); throw new InvalidOperationException(routeMsg); }
        Log(RecipeLogSeverity.Info, routeMsg);

        if (resetLoopsBeforeStart)
        {
            var maxFlow = _settings.Current.Setpoints.MaxFlowLitresPerMinute;
            var stop = CommandBuilders.CoreSafeStop(maxFlow)
                .Set(CommandKeys.PHIntensity, 0.0)
                .Set(CommandKeys.NutriIntensity, 0.0)
                .Set(CommandKeys.AntifoamIntensity, 0.0);
            _arbiter.Dispatch(CommandOwner.Recipe, stop);
        }

        SetState(RecipeRunState.Running);
        Log(RecipeLogSeverity.Info, $"Receita '{recipe.Name}' iniciada.");

        _run = Task.Run(() => RunAsync(recipe, _cts.Token), CancellationToken.None);
    }

    private async Task RunAsync(RecipeDocument recipe, CancellationToken ct)
    {
        try
        {
            InitializeGraphPeriodicGroups(ct);
            await ExecuteFlowAsync(recipe.Start, entryConnection: null, ct).ConfigureAwait(false);
            if (Resources?.HasUnreturnedAssayAuthority(ExecutionId) == true)
                throw new InvalidOperationException("Ensaio terminou sem devolução confirmada dos atuadores.");
            SetState(RecipeRunState.Completed);
            Log(RecipeLogSeverity.Info, "Receita concluída.");
        }
        catch (OperationCanceledException)
        {
            SetState(RecipeRunState.Stopped, StatusReason ?? "receita cancelada");
            Log(RecipeLogSeverity.Warning, $"Receita interrompida: {StatusReason ?? "cancelada"}.");
        }
        catch (Exception ex)
        {
            SetState(RecipeRunState.Failed, ex.Message);
            Log(RecipeLogSeverity.Error, $"Falha na execução: {ex.Message}");
        }
        finally
        {
            // Whatever ended the run — completion, stop or fault — hands the wire back safely.
            try { await StopGraphPeriodicGroupsAsync().ConfigureAwait(false); }
            catch (Exception ex) { SetState(RecipeRunState.Failed, ex.Message); }
            finally
            {
                SetWaiting(null);
                SafeStopAndRelease("fim da receita");
            }
        }
    }

    public void Pause()
    {
        if (!TransitionPauseState(RecipeRunState.Running, RecipeRunState.Paused, openGate: false))
        {
            return;
        }

        Log(RecipeLogSeverity.Info, "Receita pausada.");
    }

    public void Resume()
    {
        if (!TransitionPauseState(RecipeRunState.Paused, RecipeRunState.Running, openGate: true))
        {
            return;
        }

        Log(RecipeLogSeverity.Info, "Receita retomada.");
    }

    public async Task StopAsync(string reason)
    {
        if (State is RecipeRunState.Idle)
        {
            return;
        }

        StatusReason = reason;
        _cts?.Cancel();
        _pauseGate.Set(); // unblock a paused flow so it can observe the cancellation
        await _run.ConfigureAwait(false);
    }

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _latest = snapshot;
            _frameVersion++;
            _cascadeFrames.Enqueue((snapshot, _frameVersion));
            while (_cascadeFrames.Count > 256) _cascadeFrames.Dequeue();
            signal = _frameSignal;
            _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (snapshot.HasBathTelemetry && snapshot.TempControlViaBath is { } viaBath)
        {
            _hubRoutesTemperatureToBath = viaBath;
        }

        // Wake any block awaiting the next frame (monitor conditions, cascade steps).
        signal.TrySetResult();
    }

    /// <summary>Awaits the next telemetry frame, or throws if the run is cancelled.</summary>
    private Task WaitNextFrameAsync(CancellationToken ct) => _frameSignal.Task.WaitAsync(ct);

    // A frame that arrives while the cascade computes must remain observable on its next pass.
    // Capture the snapshot, version and wait handle under the same lock to avoid a lost wakeup.
    private async Task<(SensorSnapshot? Snapshot, long Version)> WaitCascadeFrameAsync(long observedVersion, DateTimeOffset? deadline, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline is { } end && _time.GetUtcNow() >= end) return (null, observedVersion);
            Task signal;
            lock (_lock)
            {
                foreach (var frame in _cascadeFrames)
                    if (frame.Version > observedVersion) return frame;
                signal = _frameSignal.Task;
            }
            if (deadline is null) await signal.WaitAsync(ct).ConfigureAwait(false);
            else
            {
                try { await signal.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
                catch (TimeoutException) { /* Recheck the injected clock without duplicating a cached frame. */ }
            }
        }
    }

    private void OnOwnershipRevoked(OwnershipTransfer transfer)
    {
        if (State is not (RecipeRunState.Running or RecipeRunState.Paused))
        {
            return;
        }

        // The arbiter forced ownership back to Manual on a link/feedback loss. The recipe can no
        // longer command the reactor, so it aborts rather than continuing to walk the graph blind.
        StatusReason = $"aborto seguro: {transfer.Reason}";
        _cts?.Cancel();
        _pauseGate.Set();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        // Application DI must keep devices, the writer and the assay journal alive until recovery ends.
        if (!_run.IsCompleted) await StopAsync("encerramento do aplicativo").ConfigureAwait(false);
        else await _run.ConfigureAwait(false);
        Dispose();
        _cts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.TelemetryReceived -= OnTelemetry;
        _arbiter.OwnershipRevoked -= OnOwnershipRevoked;
        _cts?.Cancel();
        _pauseGate.Dispose();
        if (_run.IsCompleted) DisposeAutonomousPauseControls();
        else _ = _run.ContinueWith(_ => DisposeAutonomousPauseControls(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
