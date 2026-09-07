using System;
using System.IO;
using System.Linq;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public sealed class AppPathsCollectionDefinition
{
}

[Collection("AppPaths")]
public sealed class PowerImpellerCatalogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDisposable _workspaceScope;

    public PowerImpellerCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OpenTECHubTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceScope = AppPaths.OverrideForTests(_tempDir);
    }

    public void Dispose()
    {
        _workspaceScope.Dispose();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best-effort cleanup; the workspace override has already been restored.
        }
    }

    [Fact]
    public void PowerImpellerCatalog_Defaults_Are_Well_Formed()
    {
        var defaults = PowerImpellerCatalog.Defaults;
        Assert.Equal(4, defaults.Count);
        Assert.All(defaults, imp =>
        {
            Assert.True(imp.DiameterM > 0, "Impeller diameter must be positive.");
            Assert.True(imp.BladeCount >= 3, "Impeller blade count must be at least 3.");
            Assert.True(imp.LiteratureNp > 0, "Reference Np must be positive.");
            Assert.False(string.IsNullOrWhiteSpace(imp.Label), "Label must not be empty.");
        });
    }

    [Fact]
    public void PowerImpellerCatalog_Save_And_Load_Roundtrip()
    {
        var customList = new[]
        {
            new Impeller
            {
                Type = ImpellerType.Custom,
                Label = "Impelidor Teste A",
                DiameterM = 0.080,
                BladeCount = 4,
                ClearanceM = 0.070,
                LiteratureNp = 2.15,
            },
            new Impeller
            {
                Type = ImpellerType.RushtonFlatBlade,
                Label = "Rushton 80mm",
                DiameterM = 0.080,
                BladeCount = 6,
                ClearanceM = 0.065,
                LiteratureNp = 5.2,
            }
        };

        PowerImpellerCatalog.SaveCatalog(customList);
        Assert.True(File.Exists(PowerImpellerCatalog.CatalogFilePath));

        var loaded = PowerImpellerCatalog.LoadCatalog();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Impelidor Teste A", loaded[0].Label);
        Assert.Equal(0.080, loaded[0].DiameterM);
        Assert.Equal(4, loaded[0].BladeCount);
        Assert.Equal(2.15, loaded[0].LiteratureNp);

        Assert.Equal("Rushton 80mm", loaded[1].Label);
        Assert.Equal(ImpellerType.RushtonFlatBlade, loaded[1].Type);
    }

    [Fact]
    public void PowerImpellerCatalog_Missing_Or_Invalid_File_Falls_Back_To_Defaults()
    {
        Assert.False(File.Exists(PowerImpellerCatalog.CatalogFilePath));
        Assert.Equal(4, PowerImpellerCatalog.LoadCatalog().Count);

        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        File.WriteAllText(PowerImpellerCatalog.CatalogFilePath, "{ invalid json");

        Assert.Equal(4, PowerImpellerCatalog.LoadCatalog().Count);
    }

    [Fact]
    public void PowerImpellerCatalog_Empty_List_Remains_Empty_After_Reload()
    {
        PowerImpellerCatalog.SaveCatalog(Array.Empty<Impeller>());

        Assert.Empty(PowerImpellerCatalog.LoadCatalog());
    }

    [Fact]
    public void PowerCondition_ZeroGasFlow_Retains_UngassedMode()
    {
        var condition = new PowerCondition
        {
            AgitationRpm = 300,
            GasFlowLpm = 0.0,
        };

        // 0.0 remains explicit in the model and retains Ungassed mode.
        Assert.Equal(PowerGasMode.Ungassed, condition.GasMode);
        Assert.Equal(0.0, condition.GasFlowLpm);

        // Setting a positive flow switches to Gassed
        condition.GasFlowLpm = 5.0;
        Assert.Equal(PowerGasMode.Gassed, condition.GasMode);
        Assert.Equal(5.0, condition.GasFlowLpm);

        // Setting back to 0.0 switches back to Ungassed
        condition.GasFlowLpm = 0.0;
        Assert.Equal(PowerGasMode.Ungassed, condition.GasMode);
        Assert.Equal(0.0, condition.GasFlowLpm);
    }

    [Fact]
    public void PowerTestViewModel_CatalogCommands_Add_Save_And_TransferToAssembly()
    {
        var inner = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(inner, TimeProvider.System);
        var store = new PowerTestStore();
        var testName = "Ensaio Teste Catalog " + Guid.NewGuid().ToString("N");
        var doc = store.CreateTest(testName, new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        using var vm = new PowerTestViewModel(store, arbiter, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        Assert.NotEmpty(vm.CatalogImpellers);

        var initialCount = vm.CatalogImpellers.Count;
        vm.AddCatalogImpellerCommand.Execute(null);

        Assert.Equal(initialCount + 1, vm.CatalogImpellers.Count);
        var added = vm.SelectedCatalogImpeller;
        Assert.NotNull(added);

        added.Label = "Custom Turbine";
        added.DiameterM = 0.075;
        added.BladeCount = 4;
        added.LiteratureNp = 1.8;

        // Assembly is initially empty
        vm.Impellers.Clear();
        Assert.Empty(vm.Impellers);

        // Add catalog impeller to assembly
        vm.AddCatalogImpellerToAssemblyCommand.Execute(null);

        Assert.Single(vm.Impellers);
        var inAssembly = vm.Impellers[0];
        Assert.Equal("Custom Turbine", inAssembly.Label);
        Assert.Equal(0.075, inAssembly.DiameterM);
        Assert.Equal(4, inAssembly.BladeCount);
        Assert.Equal(1.8, inAssembly.LiteratureNp);
        Assert.Equal(0, inAssembly.StageIndex);
    }

    [Fact]
    public void PowerImpellerCatalog_CreateByName_Returns_Exact_Model()
    {
        var custom = new Impeller
        {
            Type = ImpellerType.Custom,
            Label = "Combijet",
            DiameterM = 0.070,
            BladeCount = 4,
            ClearanceM = 0.050,
            LiteratureNp = 1.25,
        };
        PowerImpellerCatalog.SaveCatalog([custom]);

        var created = PowerImpellerCatalog.CreateByName("Combijet");
        Assert.Equal("Combijet", created.Label);
        Assert.Equal(0.070, created.DiameterM);
        Assert.Equal(4, created.BladeCount);
        Assert.Equal(1.25, created.LiteratureNp);
    }

    [Fact]
    public void PowerTestViewModel_ApplyCatalogImpellerToStage_AppliesPropertiesProperly()
    {
        var inner = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(inner, TimeProvider.System);
        var store = new PowerTestStore();
        var testName = "Ensaio Teste StageApply " + Guid.NewGuid().ToString("N");
        var doc = store.CreateTest(testName, new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        using var vm = new PowerTestViewModel(store, arbiter, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        var stage = new Impeller
        {
            StageIndex = 0,
            Label = "Old Model",
            DiameterM = 0.060,
            BladeCount = 6,
            ClearanceM = 0.065,
            LiteratureNp = 5.0,
        };
        vm.Impellers.Add(stage);

        var catalogModel = new Impeller
        {
            Label = "IsojetB",
            DiameterM = 0.072,
            BladeCount = 3,
            ClearanceM = 0.080,
            LiteratureNp = 0.95,
        };

        vm.ApplyCatalogImpellerToStage(stage, catalogModel);

        Assert.Equal("IsojetB", stage.Label);
        Assert.Equal(0.072, stage.DiameterM);
        Assert.Equal(3, stage.BladeCount);
        Assert.Equal(0.95, stage.LiteratureNp);
    }

    [Fact]
    public void PowerTestViewModel_DuplicateCatalogImpeller_Creates_Unique_Copy()
    {
        var inner = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(inner, TimeProvider.System);
        var store = new PowerTestStore();
        var testName = "Ensaio Teste Duplicate " + Guid.NewGuid().ToString("N");
        var doc = store.CreateTest(testName, new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        using var vm = new PowerTestViewModel(store, arbiter, arbiter);
        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);

        var baseModel = vm.CatalogImpellers.First();
        vm.SelectedCatalogImpeller = baseModel;
        var countBefore = vm.CatalogImpellers.Count;

        vm.DuplicateCatalogImpellerCommand.Execute(null);

        Assert.Equal(countBefore + 1, vm.CatalogImpellers.Count);
        var duplicate = vm.SelectedCatalogImpeller;
        Assert.NotNull(duplicate);
        Assert.StartsWith(baseModel.Label, duplicate.Label);
        Assert.Contains("(Cópia)", duplicate.Label);
        Assert.Equal(baseModel.DiameterM, duplicate.DiameterM);
        Assert.Equal(baseModel.BladeCount, duplicate.BladeCount);
        Assert.Equal(baseModel.LiteratureNp, duplicate.LiteratureNp);
    }

    [Fact]
    public void ImpellerComparisonItem_Propagates_ImpellerName_To_Label()
    {
        var item = new ImpellerComparisonItem
        {
            TestName = "Ensaio Combijet",
            ImpellerName = "Combijet",
            ImpellerType = ImpellerType.Custom,
            ImpellerDiameterM = 0.065,
        };
        var row = new ImpellerComparisonRow(item);
        Assert.Equal("Combijet", row.ImpellerTypeLabel);
    }

    [Fact]
    public void PowerCondition_ReplicatesDisplay_ShowsProgress_And_AllowsEditing()
    {
        var cond = new PowerCondition
        {
            RequestedReplicates = 3,
            CompletedReplicates = 1,
        };

        Assert.Equal("1/3", cond.ReplicatesDisplay);

        cond.CompletedReplicates = 2;
        Assert.Equal("2/3", cond.ReplicatesDisplay);

        // Edit target via string setter
        cond.ReplicatesDisplay = "4";
        Assert.Equal(4, cond.RequestedReplicates);
        Assert.Equal("2/4", cond.ReplicatesDisplay);

        cond.ReplicatesDisplay = "2/5";
        Assert.Equal(5, cond.RequestedReplicates);
        Assert.Equal("2/5", cond.ReplicatesDisplay);
    }
}
