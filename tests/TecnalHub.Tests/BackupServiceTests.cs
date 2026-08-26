using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using TecnalHub.Services.Persistence;
using Xunit;

namespace TecnalHub.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dataRoot;

    public BackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "TecnalTest_" + Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Export_and_Import_backup_roundtrip_preserves_all_files()
    {
        var settingsService = new MemorySettingsService(new AppSettings());
        var backupService = new BackupService(settingsService, NullLogger<BackupService>.Instance, _dataRoot);

        // Ensure directories exist
        var recipesDirectory = Path.Combine(_dataRoot, "recipes");
        var klaMappingDirectory = Path.Combine(_dataRoot, "kla-mapping");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(recipesDirectory);
        Directory.CreateDirectory(klaMappingDirectory);

        var dummyRecipe = Path.Combine(recipesDirectory, "test_recipe.json");
        await File.WriteAllTextAsync(dummyRecipe, "{\"test\": \"recipe\"}");

        var dummyKla = Path.Combine(klaMappingDirectory, "test_kla.json");
        await File.WriteAllTextAsync(dummyKla, "{\"test\": \"kla\"}");

        var backupZip = Path.Combine(_tempRoot, "backup.tecbkp");

        // 1. Export
        var exportResult = await backupService.ExportBackupAsync(backupZip);
        Assert.True(exportResult.Success);
        Assert.True(File.Exists(backupZip));

        using (var archive = ZipFile.OpenRead(backupZip))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "settings.json" || e.FullName == "recipes/test_recipe.json" || e.FullName == "kla-mapping/test_kla.json");
        }

        // Clean up dummy files
        if (File.Exists(dummyRecipe))
        {
            File.Delete(dummyRecipe);
        }
        if (File.Exists(dummyKla))
        {
            File.Delete(dummyKla);
        }

        // 2. Import
        var importResult = await backupService.ImportBackupAsync(backupZip);
        Assert.True(importResult.Success);

        Assert.True(File.Exists(dummyRecipe));
        Assert.True(File.Exists(dummyKla));

        // Cleanup
        if (File.Exists(dummyRecipe))
        {
            File.Delete(dummyRecipe);
        }
        if (File.Exists(dummyKla))
        {
            File.Delete(dummyKla);
        }
    }
}
