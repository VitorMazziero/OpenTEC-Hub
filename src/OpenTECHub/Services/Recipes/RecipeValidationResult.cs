namespace OpenTECHub.Services.Recipes;

/// <summary>Severity of a validation finding.</summary>
public enum RecipeFindingSeverity
{
    /// <summary>Blocks execution: <c>Iniciar</c> is disabled while any error stands.</summary>
    Error,

    /// <summary>Worth surfacing, does not block execution.</summary>
    Warning,
}

/// <summary>One validation finding, optionally anchored to a block.</summary>
/// <param name="Severity">Whether this blocks execution.</param>
/// <param name="Message">pt-BR description shown in the findings list.</param>
/// <param name="NodeId">The offending block, when the finding is block-scoped; clicking it centres the block.</param>
public sealed record RecipeFinding(RecipeFindingSeverity Severity, string Message, string? NodeId = null);

/// <summary>The outcome of validating a recipe: every finding, most severe first.</summary>
public sealed class RecipeValidationResult
{
    /// <summary>All findings.</summary>
    public required IReadOnlyList<RecipeFinding> Findings { get; init; }

    /// <summary>The error findings.</summary>
    public IReadOnlyList<RecipeFinding> Errors => [.. Findings.Where(f => f.Severity == RecipeFindingSeverity.Error)];

    /// <summary>The warning findings.</summary>
    public IReadOnlyList<RecipeFinding> Warnings => [.. Findings.Where(f => f.Severity == RecipeFindingSeverity.Warning)];

    /// <summary>True when nothing blocks execution.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>A valid, findings-free result.</summary>
    public static RecipeValidationResult Ok { get; } = new() { Findings = [] };
}
