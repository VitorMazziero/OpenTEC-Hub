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
    private readonly string _settingsFile;
    private readonly string _recipesDirectory;
    private readonly string _klaMappingDirectory;

    public BackupService(ISettingsService settings, ILogger<BackupService> log)
        : this(settings, log, AppPaths.DataDirectory)
    {
    }

    internal BackupService(ISettingsService settings, ILogger<BackupService> log, string dataDirectory)
    {
        _settings = settings;
        _log = log;
        _settingsFile = File.Exists(Path.Combine(dataDirectory, "Configuracoes", "settings.json"))
            ? Path.Combine(dataDirectory, "Configuracoes", "settings.json")
            : (File.Exists(Path.Combine(dataDirectory, "settings.json"))
                ? Path.Combine(dataDirectory, "settings.json")
                : Path.Combine(dataDirectory, "Configuracoes", "settings.json"));
        _recipesDirectory = Directory.Exists(Path.Combine(dataDirectory, "Receitas"))
            ? Path.Combine(dataDirectory, "Receitas")
            : (Directory.Exists(Path.Combine(dataDirectory, "recipes"))
                ? Path.Combine(dataDirectory, "recipes")
                : Path.Combine(dataDirectory, "Receitas"));
        _klaMappingDirectory = Directory.Exists(Path.Combine(dataDirectory, "Mapas"))
            ? Path.Combine(dataDirectory, "Mapas")
            : (Directory.Exists(Path.Combine(dataDirectory, "kla-mapping"))
                ? Path.Combine(dataDirectory, "kla-mapping")
                : Path.Combine(dataDirectory, "Mapas"));
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
                if (File.Exists(_settingsFile))
                {
                    var destConfig = Path.Combine(tempDir, "Configuracoes");
                    Directory.CreateDirectory(destConfig);
                    File.Copy(_settingsFile, Path.Combine(destConfig, "settings.json"), overwrite: true);
                    File.Copy(_settingsFile, Path.Combine(tempDir, "settings.json"), overwrite: true);
                }

                // 2. Recipes directory (Receitas)
                if (Directory.Exists(_recipesDirectory))
                {
                    var destRecipes = Path.Combine(tempDir, "Receitas");
                    Directory.CreateDirectory(destRecipes);
                    foreach (var file in Directory.GetFiles(_recipesDirectory, "*.json", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(_recipesDirectory, file);
                        var target = Path.Combine(destRecipes, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                        recipesCount++;
                    }
                }

                // 3. Kla-mapping directory (Mapas)
                if (Directory.Exists(_klaMappingDirectory))
                {
                    var destKla = Path.Combine(tempDir, "Mapas");
                    Directory.CreateDirectory(destKla);
                    foreach (var file in Directory.GetFiles(_klaMappingDirectory, "*.json", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(_klaMappingDirectory, file);
                        var target = Path.Combine(destKla, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
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
                var settingsSrc = File.Exists(Path.Combine(tempDir, "Configuracoes", "settings.json"))
                    ? Path.Combine(tempDir, "Configuracoes", "settings.json")
                    : Path.Combine(tempDir, "settings.json");

                if (File.Exists(settingsSrc))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
                    File.Copy(settingsSrc, _settingsFile, overwrite: true);
                    _settings.Reload();
                }

                // 2. Recipes (Receitas or recipes)
                var recipesSrc = Directory.Exists(Path.Combine(tempDir, "Receitas"))
                    ? Path.Combine(tempDir, "Receitas")
                    : Path.Combine(tempDir, "recipes");

                if (Directory.Exists(recipesSrc))
                {
                    Directory.CreateDirectory(_recipesDirectory);
                    foreach (var file in Directory.GetFiles(recipesSrc, "*.json", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(recipesSrc, file);
                        var target = Path.Combine(_recipesDirectory, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                        recipesCount++;
                    }
                }

                // 3. Kla mapping (Mapas or kla-mapping)
                var klaSrc = Directory.Exists(Path.Combine(tempDir, "Mapas"))
                    ? Path.Combine(tempDir, "Mapas")
                    : Path.Combine(tempDir, "kla-mapping");

                if (Directory.Exists(klaSrc))
                {
                    Directory.CreateDirectory(_klaMappingDirectory);
                    foreach (var file in Directory.GetFiles(klaSrc, "*.json", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(klaSrc, file);
                        var target = Path.Combine(_klaMappingDirectory, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
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
