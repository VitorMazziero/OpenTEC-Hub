using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace OpenTECHub.Services.Platform;

/// <summary>Relaunches the application against a different workspace.</summary>
public interface IApplicationRestartService
{
    /// <summary>
    /// Starts a new instance pinned to <paramref name="workspacePath"/> and shuts this one
    /// down. Returns false when the executable could not be located, in which case
    /// nothing was started and the app keeps running.
    /// </summary>
    bool RestartWithWorkspace(string workspacePath);
}

/// <summary>
/// Restarts the process with <c>--workspace</c>, the path the new instance starts in.
/// </summary>
/// <remarks>
/// <para>
/// Half the app captures its folder at construction - the settings file, the recipe,
/// map and kLa-test stores, the backup service and the Serilog sink all resolve
/// <see cref="Persistence.AppPaths"/> once, in the composition root. Rebasing those live
/// would mean a swap on every one of them while telemetry is arriving; a restart moves
/// the whole app at once, and the startup switches it needs already exist.
/// </para>
/// <para>
/// The new instance is started before this one exits, so for a moment both are running.
/// They write to different workspaces, so no file is contended - but this is the reason
/// the operation is refused while a recipe or a kLa run is in progress.
/// </para>
/// </remarks>
public sealed class ApplicationRestartService(ILogger<ApplicationRestartService> log) : IApplicationRestartService
{
    public bool RestartWithWorkspace(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var executable = ResolveExecutable();
        if (executable is null)
        {
            log.LogError("Could not resolve the executable path; the app was not restarted");
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(workspacePath);
            start.ArgumentList.Add("--no-workspace-prompt");

            Process.Start(start);
            log.LogInformation("Restarting into workspace {Workspace}", workspacePath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogError(ex, "Could not restart the app into workspace {Workspace}", workspacePath);
            return false;
        }

        // Shutdown runs OnExit, which flushes settings and closes the session log of the
        // workspace being left behind.
        Application.Current?.Shutdown();
        return true;
    }

    /// <summary>
    /// The app host next to the entry assembly, falling back to the process itself.
    /// </summary>
    /// <remarks>
    /// Under <c>dotnet run</c> the process is <c>dotnet.exe</c>, and restarting that with
    /// our own arguments would do nothing useful - so the app host beside the managed
    /// assembly is preferred when it exists.
    /// </remarks>
    private static string? ResolveExecutable()
    {
        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(assembly))
        {
            var host = Path.ChangeExtension(assembly, ".exe");
            if (File.Exists(host))
            {
                return host;
            }
        }

        var process = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(process) ? null : process;
    }
}
