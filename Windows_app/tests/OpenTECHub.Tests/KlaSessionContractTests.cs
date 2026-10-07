using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaSessionContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"opentechub-kla-session-{Guid.NewGuid():N}");
    private KlaTestStore Store => new(_root);

    private static KlaAssayDefinition Definition(KlaAssayProtocol protocol, KlaCaptureMode mode) => new()
    {
        Protocol = protocol, CaptureMode = mode,
        Conditions = [new(Guid.NewGuid(), 0, 400, 3, 1)],
        ProtocolSettings = new()
        {
            OperatingRange = KlaOperatingRange.CurrentCultivation,
            Probe = new() { Technology = "polarographic", ResponseDescription = "médio" },
        },
    };

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple)]
    public void FourModes_RoundTripWithoutMapOrKnownProbeResponse(KlaAssayProtocol protocol, KlaCaptureMode mode)
    {
        var store = Store;
        var doc = store.CreateTest("Session", Definition(protocol, mode));
        var loaded = store.LoadTest(doc.FolderName)!;
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal(protocol, loaded.EffectiveProtocol);
        Assert.Equal(mode, loaded.EffectiveCaptureMode);
        Assert.Null(loaded.LinkedMap);
        Assert.Null(loaded.ProtocolSettings!.Probe.ResponseTimeSeconds);
        Assert.Equal(KlaOperatingRange.CurrentCultivation, loaded.ProtocolSettings.OperatingRange);
        Assert.Null(loaded.ProtocolSettings.AerationReturn.MinimumDoPercent);
        Assert.Equal(protocol == KlaAssayProtocol.Biotic ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping,
            KlaAssayDefinition.FromDocument(loaded).GasRemoval);
    }

    [Theory]
    [InlineData(50, 0.5)]
    [InlineData(1000, 16)]
    public void SuppliedOperatingRange_IncludesEndpoints(double rpm, double flow)
    {
        var definition = Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single) with
        { Conditions = [new(Guid.NewGuid(), 0, rpm, flow, 1)] };
        definition.Validate();
        Assert.Equal(30, definition.ProtocolSettings.OperatingRange!.MinimumOperatingDoPercent);
        Assert.Equal(100, definition.ProtocolSettings.OperatingRange.MaximumOperatingDoPercent);
    }

    [Fact]
    public void ExampleDefaultsCanBeChangedWithoutRestrictingTheAlgorithmToCurrentCultivation()
    {
        var definition = Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single) with
        {
            Conditions = [new(Guid.NewGuid(), 0, 1500, 20, 1)],
            ProtocolSettings = new()
            {
                OperatingRange = new(10, 2000, .1, 30, 15, 105), OxygenRemovalAgitationRpm = 150,
                MinimumRemovalTargetDoPercent = 2, MaximumRemovalTargetDoPercent = 40, RemovalTargetDoPercent = 25,
            },
        };
        definition.Validate();
        var document = Store.CreateTest("Custom defaults", definition);
        var read = Store.LoadTest(document.FolderName)!;
        Assert.Equal(1500, read.Conditions[0].AgitationRpm);
        Assert.Equal(20, read.Conditions[0].AirflowLpm);
        Assert.Equal(25, read.ProtocolSettings!.RemovalTargetDoPercent);
        Assert.Equal(150, read.ProtocolSettings.OxygenRemovalAgitationRpm);
    }

    [Theory]
    [InlineData(49, 3)]
    [InlineData(1001, 3)]
    [InlineData(400, 0.49)]
    [InlineData(400, 16.01)]
    [InlineData(double.NaN, 3)]
    public void InvalidCondition_DoesNotCreateAnySessionFiles(double rpm, double flow)
    {
        var definition = Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single) with
        { Conditions = [new(Guid.NewGuid(), 0, rpm, flow, 1)] };
        var store = Store;
        Assert.Throws<ArgumentException>(() => store.CreateTest("Invalid", definition));
        Assert.False(Directory.Exists(Path.Combine(_root, "Invalid")));
    }

    [Fact]
    public void SingleCapture_RequiresExactlyOnePlannedRun()
    {
        var definition = Definition(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single);
        Assert.Throws<ArgumentException>(() => (definition with { Conditions = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (definition with
        { Conditions = [definition.Conditions[0] with { RequestedReplicates = 2 }] }).Validate());
        Assert.Throws<ArgumentException>(() => (definition with
        { Conditions = [definition.Conditions[0], definition.Conditions[0] with { ConditionId = Guid.NewGuid() }] }).Validate());
    }

    [Fact]
    public void MultipleCapture_AllowsOneConditionWithReplicates()
    {
        var definition = Definition(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple);
        (definition with { Conditions = [definition.Conditions[0] with { RequestedReplicates = 3 }] }).Validate();
    }

    [Fact]
    public void Snapshot_DoesNotChangeWhenSessionSettingsOrQueueChange()
    {
        var store = Store;
        var doc = store.CreateTest("Frozen", Definition(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single));
        var condition = doc.Conditions[0];
        var frozen = KlaRunDefinition.Create(doc, condition, 1);
        var run = new KlaTestRun { Definition = frozen, AgitationRpm = 400, AirflowLpm = 3 };
        var folder = store.InitializeRunFolder(doc.FolderName, run);
        condition.AgitationRpm = 600;
        doc.Settings = doc.Settings with { DOMaxPercent = 95 };
        doc.Conditions.Clear();
        var loaded = store.LoadRunDefinition(doc.FolderName, folder)!;
        Assert.Equal(400, frozen.Condition.AgitationRpm);
        Assert.Equal(400, loaded.Condition.AgitationRpm);
        Assert.Equal(85, loaded.Settings.DOMaxPercent);
        Assert.Equal(frozen, loaded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyVersions_AreResolvedWithoutRewritingOldSchemaOrResults(int version)
    {
        var json = $"{{\"schemaVersion\":{version},\"nature\":\"Abiotico\",\"runs\":[{{\"klaPerHour\":42.125,\"phase\":\"Accepted\",\"decision\":\"Acceptable\"}}]}}";
        var doc = KlaTestFileContracts.DeserializeTestDocument(json)!;
        Assert.Equal(KlaAssayProtocol.Abiotic, doc.EffectiveProtocol);
        Assert.Equal(KlaCaptureMode.Multiple, doc.EffectiveCaptureMode);
        Assert.Null(doc.Protocol);
        Assert.Null(doc.CaptureMode);
        Assert.Equal(version, doc.SchemaVersion);
        Assert.Equal(42.125, doc.Runs[0].KlaPerHour);
        Assert.Equal(KlaRestorationState.NotRecorded, doc.Runs[0].EffectiveOutcome.Restoration);
        Assert.False(doc.Runs[0].EffectiveOutcome.HasValidKla);
        var saved = KlaTestFileContracts.SerializeTestDocument(doc);
        Assert.DoesNotContain("\"protocol\":", saved);
        Assert.DoesNotContain("\"captureMode\":", saved);
        Assert.Equal(version, KlaTestFileContracts.DeserializeTestDocument(saved)!.SchemaVersion);
    }

    [Fact]
    public void LegacyBioticNature_IsNotSilentlyReinterpretedAsAbiotic()
    {
        var doc = KlaTestFileContracts.DeserializeTestDocument("{\"schemaVersion\":2,\"nature\":\"Biotico\"}")!;
        Assert.Equal(KlaAssayProtocol.Biotic, doc.EffectiveProtocol);
        Assert.Equal(1, KlaTestFileContracts.DeserializeTestDocument("{}")!.SchemaVersion);
        Assert.Throws<NotSupportedException>(() => KlaTestFileContracts.DeserializeTestDocument("{\"schemaVersion\":4}"));
    }

    [Fact]
    public void AcceptedByOperator_DoesNotPromoteInconclusiveScienceOrConfirmRestoration()
    {
        var analysis = new KlaAnalysisRevision
        {
            Quality = DecisionQuality.Acceptable,
            Outcome = new()
            {
                KlaQuality = KlaScientificQuality.Inconclusive,
                OurQuality = KlaScientificQuality.Valid, OurPercentPointsPerHour = 1800,
                OperatorDecision = KlaOperatorDecision.Accepted,
                Restoration = KlaRestorationState.Failed,
            },
        };
        var loaded = KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(analysis))!;
        Assert.False(loaded.EffectiveOutcome.HasValidKla);
        Assert.Equal(KlaScientificQuality.Valid, loaded.EffectiveOutcome.OurQuality);
        Assert.Equal(KlaRestorationState.Failed, loaded.EffectiveOutcome.Restoration);
    }

    [Fact]
    public void Reload_DoesNotAutomaticallyAcceptScientificallyValidPendingResult()
    {
        var store = Store;
        var doc = store.CreateTest("Pending", Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single));
        var run = new KlaTestRun { AgitationRpm = 400, AirflowLpm = 3 };
        var folder = store.InitializeRunFolder(doc.FolderName, run);
        store.SaveRunAnalysis(doc.FolderName, folder, new()
        {
            KlaPerHour = 72, Quality = DecisionQuality.Acceptable,
            Outcome = new() { KlaQuality = KlaScientificQuality.Valid, OperatorDecision = KlaOperatorDecision.Pending },
        });
        var loaded = store.LoadTest(doc.FolderName)!;
        Assert.Equal(RunPhase.Reviewing, Assert.Single(loaded.Runs).Phase);
        Assert.Equal(0, loaded.Conditions[0].AcceptedReplicates);
    }

    [Fact]
    public void ProbeMetadata_AcceptsAnyTechnologyWithoutInventingTau()
    {
        var definition = Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single);
        foreach (var technology in new[] { "polarographic", "optical", "other", "unknown" })
        {
            var variant = definition with { ProtocolSettings = definition.ProtocolSettings with
            { Probe = new() { Technology = technology } } };
            variant.Validate();
            Assert.Null(variant.ProtocolSettings.Probe.ResponseTimeSeconds);
        }
    }

    [Fact]
    public void RemovalProtocol_SeparatesLowAgitationTargetFromUsualControlRange()
    {
        var definition = Definition(KlaAssayProtocol.Biotic, KlaCaptureMode.Single) with
        {
            ProtocolSettings = new()
            {
                OperatingRange = KlaOperatingRange.CurrentCultivation,
                RemovalTargetDoPercent = 10,
            },
        };
        definition.Validate();
        Assert.Equal(100, definition.ProtocolSettings.OxygenRemovalAgitationRpm);
        Assert.Equal(5, definition.ProtocolSettings.MinimumRemovalTargetDoPercent);
        Assert.Equal(20, definition.ProtocolSettings.MaximumRemovalTargetDoPercent);
        Assert.Equal(KlaRemovalGasRoute.AirToVent, definition.OperationalPolicy.RemovalRoute);
        Assert.True(definition.OperationalPolicy.KeepFlowmeterRunningDuringRemoval);
        Assert.False(definition.OperationalPolicy.NitrogenOpenDuringRemoval);
        Assert.False(definition.OperationalPolicy.RequireAirPrestage);
        var abiotic = definition with { Protocol = KlaAssayProtocol.Abiotic };
        Assert.Equal(KlaRemovalGasRoute.NitrogenToReactor, abiotic.OperationalPolicy.RemovalRoute);
        Assert.True(abiotic.OperationalPolicy.NitrogenOpenDuringRemoval);
        Assert.True(abiotic.OperationalPolicy.RequireStableDoBeforeAirSwitch);
        Assert.True(abiotic.OperationalPolicy.RequireAirPrestage);
    }

    [Fact]
    public void Reanalysis_CannotOverwriteHistoricalRevision()
    {
        var store = Store;
        var doc = store.CreateTest("Revisions", Definition(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single));
        var folder = store.InitializeRunFolder(doc.FolderName, new() { AgitationRpm = 400, AirflowLpm = 3 });
        var analysis = new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 42.125 };
        store.SaveRunAnalysis(doc.FolderName, folder, analysis);
        var historicalPath = Path.Combine(_root, doc.FolderName, "Corridas", folder, "analise-rev-001.json");
        var historical = File.ReadAllBytes(historicalPath);
        analysis.KlaPerHour = 100;
        Assert.Throws<InvalidOperationException>(() => store.SaveRunAnalysis(doc.FolderName, folder, analysis));
        Assert.Equal(42.125, store.LoadRunAnalysis(doc.FolderName, folder)!.KlaPerHour);
        analysis.RevisionNumber = 2;
        store.SaveRunAnalysis(doc.FolderName, folder, analysis);
        Assert.Equal(historical, File.ReadAllBytes(historicalPath));
        Assert.Equal(100, store.LoadRunAnalysis(doc.FolderName, folder)!.KlaPerHour);
    }

    [Fact]
    public void QueuedRevisions_CannotOverwriteOrRollBackBeforeTheyReachDisk()
    {
        using var writer = new BackgroundFileWriter();
        var store = new KlaTestStore(_root, writer);
        var doc = store.CreateTest("Queued", Definition(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single));
        var folder = store.InitializeRunFolder(doc.FolderName, new() { AgitationRpm = 400, AirflowLpm = 3 });
        var first = new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 42 };
        store.SaveRunAnalysis(doc.FolderName, folder, first);
        var second = new KlaAnalysisRevision { RevisionNumber = 2, KlaPerHour = 72 };
        store.SaveRunAnalysis(doc.FolderName, folder, second);
        Assert.Throws<InvalidOperationException>(() => store.SaveRunAnalysis(doc.FolderName, folder, first));
        second.KlaPerHour = 100;
        Assert.Throws<InvalidOperationException>(() => store.SaveRunAnalysis(doc.FolderName, folder, second));
        Assert.Equal(72, store.LoadRunAnalysis(doc.FolderName, folder)!.KlaPerHour);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
