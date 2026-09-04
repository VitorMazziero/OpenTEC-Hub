namespace OpenTECHub.Services.Dialogs;

public enum RecipeStartOption
{
    Cancel,
    ResetAndStart,
    StartPreserving,
}

/// <summary>Operator dialogs used by view-models.</summary>
public interface IDialogService
{
    /// <summary>
    /// Confirms an action that changes equipment state destructively. The dialog shows
    /// the exact JSON that will be queued and defaults to cancellation.
    /// </summary>
    bool ConfirmDestructive(string title, string consequence, string exactCommand);

    /// <summary>
    /// Confirms an action or displays a warning without showing an equipment command box.
    /// </summary>
    bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false);

    /// <summary>
    /// Prompts the operator for a text input via a modal dialog window.
    /// </summary>
    bool PromptInput(string title, string message, out string response, string initialValue = "");

    /// <summary>
    /// Prompts the operator to choose how a recipe should start (reset loops vs start preserving current state).
    /// </summary>
    RecipeStartOption PromptRecipeStart(string recipeName);
}
