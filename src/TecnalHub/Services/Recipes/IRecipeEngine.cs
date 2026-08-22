using TecnalHub.Services.Control;

namespace TecnalHub.Services.Recipes;

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
/// ReceitasTECNAL's <c>RecipeEngine</c>.</para>
/// </remarks>
public interface IRecipeEngine : IDisposable
{
    /// <summary>The current run state.</summary>
    RecipeRunState State { get; }

    /// <summary>Why the run stopped or failed, when it did.</summary>
    string? StatusReason { get; }

    /// <summary>The recipe currently loaded into the engine, or null.</summary>
    RecipeDocument? Current { get; }

    /// <summary>Elapsed run time since the recipe started.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Whether the recipe can start now, with the reason it cannot.</summary>
    bool CanStart(RecipeDocument recipe, out string? reason);

    /// <summary>Validates, claims every actuator and begins executing the recipe.</summary>
    Task StartAsync(RecipeDocument recipe, CancellationToken cancellationToken = default);

    /// <summary>Pauses execution between blocks. In-flight actuation continues holding.</summary>
    void Pause();

    /// <summary>Resumes a paused recipe.</summary>
    void Resume();

    /// <summary>Stops the recipe, releases ownership and safe-stops the declared subsystems.</summary>
    Task StopAsync(string reason);

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

    /// <summary>Raised for each engine log line.</summary>
    event Action<RecipeLogEntry>? Logged;
}
