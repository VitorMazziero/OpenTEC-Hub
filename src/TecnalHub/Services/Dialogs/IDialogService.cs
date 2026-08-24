namespace TecnalHub.Services.Dialogs;

/// <summary>Operator dialogs used by view-models.</summary>
public interface IDialogService
{
    /// <summary>
    /// Confirms an action that changes equipment state destructively. The dialog shows
    /// the exact JSON that will be queued and defaults to cancellation.
    /// </summary>
    bool ConfirmDestructive(string title, string consequence, string exactCommand);

    /// <summary>
    /// Prompts the operator for a text input via a modal dialog window.
    /// </summary>
    bool PromptInput(string title, string message, out string response, string initialValue = "");
}
