using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenTECHub.Services.Persistence;

/// <summary>What a migration would copy, measured before anything is written.</summary>
public sealed record WorkspaceMigrationPreview(int FileCount, long TotalBytes);

/// <summary>Outcome of copying a workspace onto a new root.</summary>
public sealed record WorkspaceMigrationResult(
    bool Success,
    string Message,
    int FilesCopied = 0,
    int FilesSkipped = 0,
    int FilesFailed = 0,
    long BytesCopied = 0);

/// <summary>Copies a whole workspace onto a new root, leaving the original untouched.</summary>
public interface IWorkspaceMigrationService
{
    /// <summary>Counts what a migration would copy. Reads only; writes nothing.</summary>
    WorkspaceMigrationPreview Preview(string sourceRoot);

    /// <summary>Whether the pair of roots is usable, with the reason it is not.</summary>
    bool CanMigrate(string sourceRoot, string destinationRoot, out string? reason);

    /// <summary>
    /// Copies every file under <paramref name="sourceRoot"/> into
    /// <paramref name="destinationRoot"/>. Nothing is deleted and nothing already present
    /// at the destination is overwritten.
    /// </summary>
    Task<WorkspaceMigrationResult> MigrateAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Moves an operator workspace to a new folder by copying it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copy, never move.</b> The workspace holds the only copy of finished cultivation
/// runs; a move that fails halfway through loses data that cannot be reproduced. The
/// original folder is left exactly as it was, so a migration that goes wrong costs
/// disk space and nothing else.
/// </para>
/// <para>
/// <b>Nothing at the destination is overwritten.</b> Pointing two machines at the same
/// shared folder is a normal thing to do, and a migration that clobbered the recipes
/// already there would be indistinguishable from data loss. A file that already exists
/// is counted as skipped and reported.
/// </para>
/// <para>
/// Source files are opened with <see cref="FileShare.ReadWrite"/> because the app is
/// running while this executes: the Serilog sink and the session log are both open for
/// append on their own files, and a plain <see cref="File.Copy(string,string)"/> would
/// be at the mercy of their share mode.
/// </para>
/// </remarks>
public sealed class WorkspaceMigrationService(
    ISettingsService settings,
    ILogger<WorkspaceMigrationService> log) : IWorkspaceMigrationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public WorkspaceMigrationPreview Preview(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return new WorkspaceMigrationPreview(0, 0);
        }

        try
        {
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            long total = 0;
            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // A file that vanished between enumeration and stat contributes nothing.
                }
            }

            return new WorkspaceMigrationPreview(files.Length, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not size the workspace at {Root}", sourceRoot);
            return new WorkspaceMigrationPreview(0, 0);
        }
    }

    public bool CanMigrate(string sourceRoot, string destinationRoot, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            reason = "Nenhuma pasta de destino foi selecionada.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            reason = "A pasta de trabalho atual não existe mais em disco.";
            return false;
        }

        var source = Normalize(sourceRoot);
        var destination = Normalize(destinationRoot);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            reason = "A pasta escolhida já é a pasta de trabalho atual.";
            return false;
        }

        if (IsInside(destination, source))
        {
            reason = "A pasta de destino está dentro da pasta de trabalho atual. Escolha uma pasta fora dela.";
            return false;
        }

        if (IsInside(source, destination))
        {
            reason = "A pasta de trabalho atual está dentro da pasta de destino. Escolha outra pasta.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<WorkspaceMigrationResult> MigrateAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!CanMigrate(sourceRoot, destinationRoot, out var reason))
        {
            return new WorkspaceMigrationResult(false, reason ?? "Migração inválida.");
        }

        var source = Normalize(sourceRoot);
        var destination = Normalize(destinationRoot);

        // The debounced save would otherwise land after the copy, so the new workspace
        // would carry a settings.json a few hundred milliseconds out of date.
        await settings.SaveNowAsync().ConfigureAwait(false);

        var copied = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;
        string? firstFailure = null;
        var settingsFilesCopied = new List<string>();

        try
        {
            Directory.CreateDirectory(destination);

            // Directories first, so an empty subfolder survives the migration.
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            // Snapshot the list up front: the app keeps writing to this folder while we copy.
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);

                if (File.Exists(target))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    bytes += await CopyOpenFileAsync(file, target, cancellationToken).ConfigureAwait(false);
                    copied++;

                    if (IsSettingsFile(relative))
                    {
                        settingsFilesCopied.Add(target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    firstFailure ??= relative;
                    log.LogWarning(ex, "Could not copy {File} while migrating the workspace", file);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new WorkspaceMigrationResult(
                false, "Migração cancelada. A pasta de origem permanece intacta.", copied, skipped, failed, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Workspace migration from {Source} to {Destination} failed", source, destination);
            return new WorkspaceMigrationResult(
                false,
                $"Falha ao copiar para a nova pasta: {ex.Message}. A pasta de origem permanece intacta.",
                copied, skipped, failed, bytes);
        }

        foreach (var settingsFile in settingsFilesCopied)
        {
            RebaseSessionLogPath(settingsFile, source, destination);
        }

        if (failed > 0)
        {
            return new WorkspaceMigrationResult(
                false,
                $"{failed} arquivo(s) não puderam ser copiados (o primeiro foi \"{firstFailure}\"). " +
                "Nada foi apagado e a pasta de trabalho continua sendo a atual.",
                copied, skipped, failed, bytes);
        }

        log.LogInformation(
            "Workspace migrated from {Source} to {Destination}: {Copied} copied, {Skipped} skipped",
            source, destination, copied, skipped);

        var skippedNote = skipped > 0
            ? $" {skipped} já existiam no destino e foram preservados."
            : string.Empty;

        return new WorkspaceMigrationResult(
            true,
            $"{copied} arquivo(s) copiados para a nova pasta.{skippedNote}",
            copied, skipped, failed, bytes);
    }

    /// <summary>Copies a file that another writer may hold open for append.</summary>
    private static async Task<long> CopyOpenFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        long written;
        await using (var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        await using (var output = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            written = output.Length;
        }

        // Recipes and sessions are listed most-recently-modified first; a migration that
        // stamped every file with today's date would scramble that ordering.
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (IOException)
        {
            // Cosmetic only.
        }

        return written;
    }

    private static bool IsSettingsFile(string relativePath) =>
        string.Equals(relativePath, "settings.json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, Path.Combine("Configuracoes", "settings.json"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Points the copied settings at the copied session log.
    /// </summary>
    /// <remarks>
    /// <c>Logging.SessionLogPath</c> is an absolute path. Left alone, the app would start
    /// in the new workspace and keep appending telemetry to the file in the old one - the
    /// run would look fine and the data would land in the folder the operator just
    /// migrated away from. Only the copy is rewritten; the original settings file still
    /// describes the original workspace.
    /// </remarks>
    private void RebaseSessionLogPath(string settingsFile, string source, string destination)
    {
        try
        {
            var current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), SerializerOptions);
            if (current?.Logging.SessionLogPath is not { Length: > 0 } sessionPath)
            {
                return;
            }

            var full = Path.GetFullPath(sessionPath);
            if (!IsInside(full, source))
            {
                return;
            }

            var rebased = Path.Combine(destination, Path.GetRelativePath(source, full));
            var updated = current with { Logging = current.Logging with { SessionLogPath = rebased } };
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(updated, SerializerOptions));

            log.LogInformation("Session log path rebased to {Path} in the migrated workspace", rebased);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A settings file we cannot parse is left exactly as copied: the app falls
            // back to a fresh session file, which is recoverable. Rewriting it blind is not.
            log.LogWarning(ex, "Could not rebase the session log path in {File}", settingsFile);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = Normalize(root) + Path.DirectorySeparatorChar;
        return Normalize(candidate).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
