namespace OpenTECHub.Services.Platform;

/// <summary>Small, testable boundary around Windows file and clipboard interactions.</summary>
public interface IFileInteractionService
{
    string? ChooseSavePath(string title, string suggestedName, string filter, string extension);

    string? ChooseOpenPath(string title, string filter, string extension);

    string? ChooseFolder(string title, string? initialDirectory = null);

    void OpenFolder(string path);

    void CopyText(string text);

    /// <summary>Opens an absolute <c>http</c>/<c>https</c> URI in the operator's default browser.</summary>
    /// <remarks>
    /// Used for the external nodes' own diagnostic pages (<c>http://192.168.4.x/diag</c>),
    /// reachable only while the PC is on the Hub's Wi-Fi. Anything that is not a web URI
    /// is refused rather than handed to the shell.
    /// </remarks>
    void OpenUri(Uri uri);
}
