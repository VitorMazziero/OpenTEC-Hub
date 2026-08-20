using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TecnalHub.Services.Platform;

/// <summary>Windows implementation of file selection, Explorer and clipboard actions.</summary>
public sealed class FileInteractionService : IFileInteractionService
{
    public string? ChooseSavePath(
        string title,
        string suggestedName,
        string filter,
        string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        start.ArgumentList.Add(folder);
        Process.Start(start);
    }

    public void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }
}
