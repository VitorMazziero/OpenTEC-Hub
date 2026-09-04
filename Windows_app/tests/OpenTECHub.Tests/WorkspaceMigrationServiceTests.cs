using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Copying a workspace onto a new root.
/// </summary>
/// <remarks>
/// The workspace holds the only copy of finished cultivation runs, so every test here
/// is ultimately about the same property: after a migration - successful, refused or
/// half-failed - the source folder still has everything it had before.
/// </remarks>
public sealed class WorkspaceMigrationServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _tempRoot;
    private readonly string _source;
    private readonly string _destination;
    private readonly WorkspaceMigrationService _migration;

    public WorkspaceMigrationServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OpenTECMigration_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_tempRoot, "Atual");
        _destination = Path.Combine(_tempRoot, "Nova");
        Directory.CreateDirectory(_source);

        _migration = new WorkspaceMigrationService(
            new MemorySettingsService(new AppSettings()),
            NullLogger<WorkspaceMigrationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task Copies_the_whole_tree_and_leaves_the_source_untouched()
    {
        Write("Receitas", "fermentacao.recipe.json", "{\"name\":\"fermentacao\"}");
        Write("Sessoes", "Ensaio_2026-08-26_1830.txt", "Time (min)\tTemperature (°C)\n");
        Write(Path.Combine("Testes-kLa", "Ensaio A", "runs"), "teste.json", "{}");
        Directory.CreateDirectory(Path.Combine(_source, "Backups"));

        var result = await _migration.MigrateAsync(_source, _destination);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(0, result.FilesFailed);

        Assert.True(File.Exists(Path.Combine(_destination, "Receitas", "fermentacao.recipe.json")));
        Assert.True(File.Exists(Path.Combine(_destination, "Sessoes", "Ensaio_2026-08-26_1830.txt")));
        Assert.True(File.Exists(Path.Combine(_destination, "Testes-kLa", "Ensaio A", "runs", "teste.json")));

        // An empty subfolder is part of the workspace layout and survives the copy.
        Assert.True(Directory.Exists(Path.Combine(_destination, "Backups")));

        // Nothing was moved.
        Assert.True(File.Exists(Path.Combine(_source, "Receitas", "fermentacao.recipe.json")));
        Assert.True(File.Exists(Path.Combine(_source, "Sessoes", "Ensaio_2026-08-26_1830.txt")));
    }

    [Fact]
    public async Task Never_overwrites_a_file_already_at_the_destination()
    {
        Write("Receitas", "compartilhada.recipe.json", "origem");
        Directory.CreateDirectory(Path.Combine(_destination, "Receitas"));
        await File.WriteAllTextAsync(
            Path.Combine(_destination, "Receitas", "compartilhada.recipe.json"), "destino");

        var result = await _migration.MigrateAsync(_source, _destination);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal(
            "destino",
            await File.ReadAllTextAsync(Path.Combine(_destination, "Receitas", "compartilhada.recipe.json")));
    }

    [Fact]
    public async Task Rebases_the_session_log_path_in_the_copied_settings_only()
    {
        var sessionPath = Path.Combine(_source, "Sessoes", "Ensaio_2026-08-26_1830.txt");
        Write("Sessoes", "Ensaio_2026-08-26_1830.txt", "Time (min)\n");

        var settings = new AppSettings
        {
            Logging = new LoggingSettings { SessionLogPath = sessionPath },
        };
        Write("Configuracoes", "settings.json", JsonSerializer.Serialize(settings, SerializerOptions));

        var result = await _migration.MigrateAsync(_source, _destination);
        Assert.True(result.Success, result.Message);

        var migrated = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(Path.Combine(_destination, "Configuracoes", "settings.json")),
            SerializerOptions);

        Assert.Equal(
            Path.Combine(_destination, "Sessoes", "Ensaio_2026-08-26_1830.txt"),
            migrated!.Logging.SessionLogPath);

        // The workspace being left behind still describes itself.
        var original = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(Path.Combine(_source, "Configuracoes", "settings.json")),
            SerializerOptions);
        Assert.Equal(sessionPath, original!.Logging.SessionLogPath);
    }

    [Fact]
    public async Task Copies_a_file_another_writer_holds_open_for_append()
    {
        // The session log and the Serilog sink are both open while the operator triggers
        // the migration; a copy that could not read them would migrate the run half-way.
        var open = Path.Combine(_source, "Logs", "opentechub-20260826.log");
        Directory.CreateDirectory(Path.GetDirectoryName(open)!);

        await using (var writer = new StreamWriter(open, append: true, new UTF8Encoding(false)) { AutoFlush = true })
        {
            await writer.WriteLineAsync("linha em uso");

            var result = await _migration.MigrateAsync(_source, _destination);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.FilesCopied);
            Assert.Contains(
                "linha em uso",
                await File.ReadAllTextAsync(Path.Combine(_destination, "Logs", "opentechub-20260826.log")));
        }
    }

    [Theory]
    [InlineData("dentro")]
    [InlineData("mesma")]
    public async Task Refuses_a_destination_that_would_nest_or_repeat_the_source(string kind)
    {
        Write("Receitas", "a.recipe.json", "{}");
        var destination = kind == "dentro" ? Path.Combine(_source, "Sessoes") : _source;

        Assert.False(_migration.CanMigrate(_source, destination, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        var result = await _migration.MigrateAsync(_source, destination);
        Assert.False(result.Success);
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public void Preview_counts_every_file_under_the_root()
    {
        Write("Receitas", "a.recipe.json", "12345");
        Write("Mapas", "b.kla.json", "123");

        var preview = _migration.Preview(_source);

        Assert.Equal(2, preview.FileCount);
        Assert.Equal(8, preview.TotalBytes);
    }

    private void Write(string relativeDirectory, string fileName, string content)
    {
        var directory = Path.Combine(_source, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }
}
