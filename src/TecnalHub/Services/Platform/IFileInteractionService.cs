namespace TecnalHub.Services.Platform;

/// <summary>Small, testable boundary around Windows file and clipboard interactions.</summary>
public interface IFileInteractionService
{
    string? ChooseSavePath(string title, string suggestedName, string filter, string extension);

    void OpenFolder(string path);

    void CopyText(string text);
}
