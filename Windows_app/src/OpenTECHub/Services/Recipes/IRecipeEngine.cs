using OpenTECHub.Services.Control;

namespace OpenTECHub.Services.Recipes;

/// <summary>The run state of the recipe engine.</summary>
public enum RecipeRunState
{
    /// <summary>Nothing is running.</summary>
    Idle,

    /// <summary>A recipe is executing.</summary>
    Running,

    /// <summary>A recipe is paused between blocks.</summary>
    Paused,

    /// <summary>The recipe reached a Fim block and finished.</summary>
    Completed,

    /// <summary>The operator stopped the recipe, or a safe-abort tripped.</summary>
    Stopped,

    /// <summary>A block faulted.</summary>
    Failed,
}

/// <summary>Severity of an engine log line.</summary>
public enum RecipeLogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One engine log line, mirrored to Eventos under the <c>Receita</c> source.</summary>
/// <param name="Severity">Line severity.</param>
/// <param name="Message">pt-BR message.</param>
/// <param name="NodeId">The block it concerns, if any.</param>
public sealed record RecipeLogEntry(RecipeLogSeverity Severity, string Message, string? NodeId = null);

/// <summary>
/// A block held because an external device has not confirmed the command the recipe sent it.
/// </summary>
/// <remarks>
/// The recipe does not abort over an unresponsive device — it holds and keeps watching, which is
/// what the operator expects from a cultivation that is already running. What the wait adds is
/// visibility: an alarm, a log line, and the two ways out (skip the block, or stop the recipe).
/// </remarks>
/// <param name="NodeId">The block that is holding.</param>
/// <param name="Device">Operator-facing device name, e.g. "Fluxômetro".</param>
/// <param name="Detail">Why it is holding, in pt-BR.</param>
/// <param name="Since">When the block first sent the command.</param>
public sealed record RecipeDeviceWait(string NodeId, string Device, string Detail, DateTimeOffset Since);

/// <summary>
/// The recipe execution engine.
/// </summary>
/// <remarks>
/// <para>
/// The engine drives the <b>same</b> <see cref="Communication.ICommandArbiter"/> as manual
/// control, under <see cref="Communication.CommandOwner.Recipe"/> ownership — one command queue,
/// one owner. Starting a recipe <b>claims every actuator</b>, which is what deactivates the manual
/// control surfaces and leaves only the recipe writing to the wire (<c>docs/UI_DESIGN.md</c>
/// §5.3.3). Stopping releases ownership and safe-stops the declared subsystems; a link or feedback
/// loss revokes ownership and safe-aborts the run.
/// </para>
/// <para>Sliced by responsibility (<c>.Flow</c>, <c>.Nodes</c>, <c>.Actuation</c>, <c>.Pumps</c>,
/// <c>.Cascade</c>, <c>.Safety</c>, <c>.State</c>, <c>.LiveTuning</c>), the slicing kept from
/// ReceitasOpenTEC's <c>RecipeEngine</c>.</para>
/// </remarks>
public interface IRecipeEngine : IDisposable
{
    /// <summary>All automatic invocation results, including inconclusive and failed attempts.</summary>
    IReadOnlyList<KlaTesting.KlaRecipeResult> AutonomousResults => [];
    /// <summary>The current run state.</summary>
    RecipeRunState State { get; }

    /// <summary>Why the run stopped or failed, when it did.</summary>
    string? StatusReason { get; }

    /// <summary>The device a block is currently holding for, or null when nothing is held.</summary>
    RecipeDeviceWait? Waiting { get; }

    /// <summary>The recipe currently loaded into the engine, or null.</summary>
    RecipeDocument? Current { get; }

    /// <summary>Elapsed run time since the recipe started.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Whether the recipe can start now, with the reason it cannot.</summary>
    bool CanStart(RecipeDocument recipe, out string? reason);

    /// <summary>Validates, claims every actuator and begins executing the recipe.</summary>
    Task StartAsync(RecipeDocument recipe, bool resetLoopsBeforeStart = false, CancellationToken cancellationToken = default);

    /// <summary>Pauses execution between blocks. In-flight actuation continues holding.</summary>
    void Pause();

    /// <summary>Resumes a paused recipe.</summary>
    void Resume();

    /// <summary>Stops the recipe, releases ownership and safe-stops the declared subsystems.</summary>
    Task StopAsync(string reason);

    /// <summary>
    /// Gives up on the device the current block is holding for and moves on to the next block.
    /// </summary>
    /// <remarks>
    /// The operator's decision, never the engine's: the command stays sent, the block is logged as
    /// skipped without confirmation, and the recipe continues. No-op when nothing is held.
    /// </remarks>
    void SkipWait();

    /// <summary>Coordinator managing Modbus priority and UART fallback for motor rotation.</summary>
    Communication.MotorRouteCoordinator? RouteCoordinator => null;

    /// <summary>Completes when the current run ends (completed, stopped or failed).</summary>
    Task Completion { get; }

    /// <summary>Applies edited parameters to a live block (cascade gains, monitor targets).</summary>
    bool ApplyLiveTuning(RecipeNode node);

    /// <summary>The execution state of a block.</summary>
    NodeState NodeStateOf(string nodeId);

    /// <summary>True when a connection has been traversed — drives the green executed-path render.</summary>
    bool WasTraversed(RecipeConnection connection);

    /// <summary>The live decomposed cascade terms for a running cascade block, or null.</summary>
    CascadeTerms? CascadeTermsFor(string nodeId);

    /// <summary>Raised when a block's execution state changes; the argument is the block id.</summary>
    event Action<string>? NodeStateChanged;

    /// <summary>Raised when the run state changes.</summary>
    event Action? StateChanged;

    /// <summary>Raised when a block starts or stops holding for a device.</summary>
    event Action? WaitingChanged;

    /// <summary>Raised for each engine log line.</summary>
    event Action<RecipeLogEntry>? Logged;
}
