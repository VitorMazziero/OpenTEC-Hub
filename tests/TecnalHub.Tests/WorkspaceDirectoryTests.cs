using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Recipes;
using TecnalHub.Services.Theme;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

public sealed class WorkspaceDirectoryTests : IDisposable
{
    private readonly string _testRoot;

    public WorkspaceDirectoryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"tecnalhub-workspace-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public void AppPaths_InitializeWorkspace_Creates_Expected_Portuguese_Subdirectories()
    {
        var workspace = Path.Combine(_testRoot, "MeuEnsaio");
        AppPaths.InitializeWorkspace(workspace);

        Assert.Equal(workspace, AppPaths.DataDirectory);
        Assert.True(Directory.Exists(workspace));

        // Subpastas em Português com inicial Maiúscula
        Assert.EndsWith("Mapas", AppPaths.KlaMappingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Receitas", AppPaths.RecipesDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Logs", AppPaths.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Sessoes", AppPaths.SessionsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Backups", AppPaths.BackupsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Configuracoes", AppPaths.ConfigDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Configuracoes", "settings.json"), AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(AppPaths.KlaMappingDirectory));
        Assert.True(Directory.Exists(AppPaths.RecipesDirectory));
        Assert.True(Directory.Exists(AppPaths.LogDirectory));
        Assert.True(Directory.Exists(AppPaths.SessionsDirectory));
        Assert.True(Directory.Exists(AppPaths.BackupsDirectory));
        Assert.True(Directory.Exists(AppPaths.ConfigDirectory));
    }

    [Fact]
    public void SettingsViewModel_Exposes_All_Workspace_Folders_With_Descriptions()
    {
        var workspace = Path.Combine(_testRoot, "WorkspaceExposed");
        AppPaths.InitializeWorkspace(workspace);

        var (viewModel, _, _) = CreateSettingsViewModel();

        Assert.Equal(workspace, viewModel.WorkspaceDirectory);
        Assert.Equal(6, viewModel.WorkspaceFolders.Count);

        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Mapas" && f.RelativePath == "Mapas\\" && f.FullPath == AppPaths.KlaMappingDirectory);
        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Receitas" && f.RelativePath == "Receitas\\" && f.FullPath == AppPaths.RecipesDirectory);
        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Sessões" && f.RelativePath == "Sessoes\\" && f.FullPath == AppPaths.SessionsDirectory);
        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Logs" && f.RelativePath == "Logs\\" && f.FullPath == AppPaths.LogDirectory);
        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Configurações" && f.RelativePath == "Configuracoes\\" && f.FullPath == AppPaths.ConfigDirectory);
        Assert.Contains(viewModel.WorkspaceFolders, f => f.Name == "Backups" && f.RelativePath == "Backups\\" && f.FullPath == AppPaths.BackupsDirectory);
    }

    [Fact]
    public void SettingsViewModel_ChangeWorkspaceDirectory_Prompts_Warning_And_Cancels_When_Rejected()
    {
        var initialWorkspace = Path.Combine(_testRoot, "WorkspaceInitial");
        AppPaths.InitializeWorkspace(initialWorkspace);

        var (viewModel, dialogs, files) = CreateSettingsViewModel();
        dialogs.ConfirmResult = false;
        files.NextFolder = Path.Combine(_testRoot, "WorkspaceNovo");

        viewModel.ChangeWorkspaceDirectoryCommand.Execute(null);

        Assert.True(dialogs.ConfirmCalled);
        Assert.Contains("NÃO serão migrados automaticamente", dialogs.LastConsequence);
        Assert.Equal(initialWorkspace, viewModel.WorkspaceDirectory);
        Assert.Equal(initialWorkspace, AppPaths.DataDirectory);
    }

    [Fact]
    public void SettingsViewModel_ChangeWorkspaceDirectory_Updates_When_Confirmed()
    {
        var initialWorkspace = Path.Combine(_testRoot, "Workspace1");
        var newWorkspace = Path.Combine(_testRoot, "Workspace2");
        AppPaths.InitializeWorkspace(initialWorkspace);

        var (viewModel, dialogs, files) = CreateSettingsViewModel();
        dialogs.ConfirmResult = true;
        files.NextFolder = newWorkspace;

        viewModel.ChangeWorkspaceDirectoryCommand.Execute(null);

        Assert.True(dialogs.ConfirmCalled);
        Assert.Equal(newWorkspace, viewModel.WorkspaceDirectory);
        Assert.Equal(newWorkspace, AppPaths.DataDirectory);
        Assert.True(Directory.Exists(newWorkspace));
        Assert.True(Directory.Exists(AppPaths.KlaMappingDirectory));
    }

    [Fact]
    public void SettingsViewModel_OpenWorkspaceFolder_And_OpenSubfolder_Delegate_To_Files()
    {
        var workspace = Path.Combine(_testRoot, "WorkspaceOpen");
        AppPaths.InitializeWorkspace(workspace);

        var (viewModel, _, files) = CreateSettingsViewModel();

        viewModel.OpenWorkspaceFolderCommand.Execute(null);
        Assert.Equal(workspace, files.LastOpenedFolder);

        var mapasDir = AppPaths.KlaMappingDirectory;
        viewModel.OpenSubfolderCommand.Execute(mapasDir);
        Assert.Equal(mapasDir, files.LastOpenedFolder);
    }

    [Fact]
    public async Task KlaProfileStore_And_RecipeStore_Save_To_Portuguese_Subdirectories()
    {
        var workspace = Path.Combine(_testRoot, "WorkspaceStores");
        AppPaths.InitializeWorkspace(workspace);

        var klaStore = new KlaProfileStore(AppPaths.KlaMappingDirectory);
        var recipeStore = new RecipeStore(AppPaths.RecipesDirectory);

        // 1. Kla Profile
        var experiment = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Name = "Ensaio Biorreator",
                Broth = "Serratia marcescens",
                RunCode = "fixture",
                Domain = new KlaDomain(0.5, 12, 50, 750),
                Anchors =
                [
                    new(0.5, 50, 10),
                    new(1.0, 100, 20),
                    new(2.0, 200, 40),
                    new(4.0, 300, 60),
                    new(6.0, 500, 80),
                    new(8.0, 700, 100),
                ],
            },
        };

        await klaStore.SaveExperimentAsync(experiment);
        var savedKlaPath = klaStore.GetExperimentFilePath(experiment.Snapshot.Id);

        Assert.True(File.Exists(savedKlaPath));
        Assert.Contains(Path.Combine(workspace, "Mapas"), savedKlaPath);

        // 2. Recipe
        var recipeDoc = new RecipeDocument
        {
            Name = "Receita Fermentação",
            Description = "Teste de receita",
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
            Nodes = [],
            Connections = [],
        };

        var savedRecipeFileName = recipeStore.Save(recipeDoc);
        var savedRecipePath = Path.Combine(recipeStore.Directory, savedRecipeFileName);

        Assert.True(File.Exists(savedRecipePath));
        Assert.Contains(Path.Combine(workspace, "Receitas"), savedRecipePath);
    }

    private static (SettingsViewModel vm, MockDialogService dialogs, MockFileInteraction files) CreateSettingsViewModel()
    {
        var settingsService = new MemorySettingsService(new AppSettings());
        var theme = new ThemeService(NullLogger<ThemeService>.Instance);
        var dialogs = new MockDialogService();
        var files = new MockFileInteraction();
        var device = new RecordingDeviceService();

        var vm = new SettingsViewModel(
            settingsService,
            theme,
            device,
            dialogs,
            backup: null,
            files: files);

        return (vm, dialogs, files);
    }

    private sealed class MemorySettingsService(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial;

        public event Action<AppSettings>? Changed;

        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(Current);
        }

        public Task SaveNowAsync() => Task.CompletedTask;

        public void Reload() => Changed?.Invoke(Current);
    }

    private sealed class MockDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmCalled { get; set; }
        public string LastTitle { get; set; } = "";
        public string LastConsequence { get; set; } = "";

        public bool ConfirmDestructive(string title, string consequence, string exactCommand)
        {
            ConfirmCalled = true;
            LastTitle = title;
            LastConsequence = consequence;
            return ConfirmResult;
        }

        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false)
        {
            ConfirmCalled = true;
            LastTitle = title;
            LastConsequence = message;
            return ConfirmResult;
        }

        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            response = initialValue;
            return true;
        }
    }

    private sealed class MockFileInteraction : IFileInteractionService
    {
        public string? NextFolder { get; set; }
        public string? LastOpenedFolder { get; set; }

        public string? ChooseFolder(string title, string? initialDirectory = null)
            => NextFolder;

        public string? ChooseOpenPath(string title, string filter, string extension)
            => null;

        public string? ChooseSavePath(string title, string suggestedName, string filter, string extension)
            => null;

        public void CopyText(string text)
        {
        }

        public void OpenFolder(string path)
        {
            LastOpenedFolder = path;
        }
    }
}
