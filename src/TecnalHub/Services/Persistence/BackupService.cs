using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace TecnalHub.Services.Persistence;

/// <summary>Outcome of an export or import operation.</summary>
public sealed record BackupResult(bool Success, string Message, int RecipesCount = 0, int MapsCount = 0);

/// <summary>
/// Packages and restores the entire application state (settings.json, recipes, and kla mappings).
/// </summary>
public interface IBackupService
{
    /// <summary>Exports settings, recipes and published kLa maps to a zip archive.</summary>
    Task<BackupResult> ExportBackupAsync(string destinationZipPath);

    /// <summary>Restores settings, recipes and published kLa maps from a zip archive.</summary>
    Task<BackupResult> ImportBackupAsync(string sourceZipPath);
}

/// <summary>
/// Implements backup creation and restoration targeting <see cref="AppPaths.DataDirectory"/>.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<BackupService> _log;

    public BackupService(ISettingsService settings, ILogger<BackupService> log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<BackupResult> ExportBackupAsync(string destinationZipPath)
    {
        try
        {
            // Flush any debounced settings first
            await _settings.SaveNowAsync().ConfigureAwait(false);

            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "TecnalBackup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var recipesCount = 0;
            var mapsCount = 0;

            try
            {
                // 1. Settings file
                if (File.Exists(AppPaths.SettingsFile))
                {
                    File.Copy(AppPaths.SettingsFile, Path.Combine(tempDir, "settings.json"), overwrite: true);
                }

                // 2. Recipes directory
                if (Directory.Exists(AppPaths.RecipesDirectory))
                {
                    var destRecipes = Path.Combine(tempDir, "recipes");
                    Directory.CreateDirectory(destRecipes);
                    foreach (var file in Directory.GetFiles(AppPaths.RecipesDirectory, "*.json"))
                    {
                        File.Copy(file, Path.Combine(destRecipes, Path.GetFileName(file)), overwrite: true);
                        recipesCount++;
                    }
                }

                // 3. Kla-mapping directory
                if (Directory.Exists(AppPaths.KlaMappingDirectory))
                {
                    var destKla = Path.Combine(tempDir, "kla-mapping");
                    Directory.CreateDirectory(destKla);
                    foreach (var file in Directory.GetFiles(AppPaths.KlaMappingDirectory, "*.json"))
                    {
                        File.Copy(file, Path.Combine(destKla, Path.GetFileName(file)), overwrite: true);
                        mapsCount++;
                    }
                }

                ZipFile.CreateFromDirectory(tempDir, destinationZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                _log.LogInformation("Backup exported successfully to {Path} ({Recipes} recipes, {Maps} maps)", destinationZipPath, recipesCount, mapsCount);

                return new BackupResult(true, "Backup exportado com sucesso!", recipesCount, mapsCount);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to export backup to {Path}", destinationZipPath);
            return new BackupResult(false, $"Falha ao exportar backup: {ex.Message}");
        }
    }

    public async Task<BackupResult> ImportBackupAsync(string sourceZipPath)
    {
        try
        {
            if (!File.Exists(sourceZipPath))
            {
                return new BackupResult(false, "Arquivo de backup não encontrado.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "TecnalRestore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var recipesCount = 0;
            var mapsCount = 0;

            try
            {
                ZipFile.ExtractToDirectory(sourceZipPath, tempDir, overwriteFiles: true);

                // 1. Settings file
                var settingsSrc = Path.Combine(tempDir, "settings.json");
                if (File.Exists(settingsSrc))
                {
                    File.Copy(settingsSrc, AppPaths.SettingsFile, overwrite: true);
                    _settings.Reload();
                }

                // 2. Recipes
                var recipesSrc = Path.Combine(tempDir, "recipes");
                if (Directory.Exists(recipesSrc))
                {
                    Directory.CreateDirectory(AppPaths.RecipesDirectory);
                    foreach (var file in Directory.GetFiles(recipesSrc, "*.json"))
                    {
                        File.Copy(file, Path.Combine(AppPaths.RecipesDirectory, Path.GetFileName(file)), overwrite: true);
                        recipesCount++;
                    }
                }

                // 3. Kla mapping
                var klaSrc = Path.Combine(tempDir, "kla-mapping");
                if (Directory.Exists(klaSrc))
                {
                    Directory.CreateDirectory(AppPaths.KlaMappingDirectory);
                    foreach (var file in Directory.GetFiles(klaSrc, "*.json"))
                    {
                        File.Copy(file, Path.Combine(AppPaths.KlaMappingDirectory, Path.GetFileName(file)), overwrite: true);
                        mapsCount++;
                    }
                }

                _log.LogInformation("Backup imported successfully from {Path} ({Recipes} recipes, {Maps} maps)", sourceZipPath, recipesCount, mapsCount);

                return new BackupResult(true, "Backup importado e restaurado com sucesso!", recipesCount, mapsCount);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to import backup from {Path}", sourceZipPath);
            return new BackupResult(false, $"Falha ao importar backup: {ex.Message}");
        }
    }
}
