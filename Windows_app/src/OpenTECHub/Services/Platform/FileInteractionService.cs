using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Platform;

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
            RestoreDirectory = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public string? ChooseOpenPath(string title, string filter, string extension)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public string? ChooseFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory ?? AppPaths.DataDirectory,
            Multiselect = false,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FolderName
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

    public void OpenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute http/https URIs are opened.", nameof(uri));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    }
}
