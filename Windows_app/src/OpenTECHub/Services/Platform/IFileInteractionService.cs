namespace OpenTECHub.Services.Platform;

/// <summary>Small, testable boundary around Windows file and clipboard interactions.</summary>
public interface IFileInteractionService
{
    string? ChooseSavePath(string title, string suggestedName, string filter, string extension);

    string? ChooseOpenPath(string title, string filter, string extension);

    string? ChooseFolder(string title, string? initialDirectory = null);

    void OpenFolder(string path);

    void CopyText(string text);
}
