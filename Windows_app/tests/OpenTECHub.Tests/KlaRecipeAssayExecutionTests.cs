using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeAssayExecutionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FactoryRejectsPhysicalEnvironmentOrPhysicalCapability(bool isolatedEnvironment, bool isolatedCapability)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "recipe-factory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new KlaTestStore(directory);
            var factory = new KlaRecipeAssayExecutionFactory(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(),
                new MemorySettingsService(), fixture.Clock, isolatedEnvironment);
            Assert.Throws<InvalidOperationException>(() => factory.Create(fixture.Lease, new KlaTestDocument(),
                new() { MaximumTelemetryAgeSeconds = 5 }, new()
                {
                    InstallationId = "simulator-A", ProfileId = "qualified-test-profile", ProfileVersion = "1",
                    Protocols = [KlaAssayProtocol.Abiotic], EvidenceId = "test", IsIsolatedSimulation = isolatedCapability
                }));
            Assert.Empty(fixture.Device.Sent);
            Assert.True(fixture.Lease.IsAssayAuthorityCurrent);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, false, false, false)]
    [InlineData(KlaAssayProtocol.Biotic, false, false, false)]
    [InlineData(KlaAssayProtocol.Abiotic, true, false, false)]
    [InlineData(KlaAssayProtocol.Abiotic, false, true, false)]
    [InlineData(KlaAssayProtocol.Abiotic, false, false, true)]
    [InlineData(KlaAssayProtocol.Biotic, false, false, true)]
    public async Task ApiPulseUsesCommonRecoveryAnalysisAndPersistence(KlaAssayProtocol protocol, bool deadline, bool failPersistence, bool completeNormally)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "recipe-execution-" + Guid.NewGuid().ToString("N"));
        KlaAssayApi? activeApi = null;
        Guid activeRequest = Guid.Empty;
        try
        {
            using var writer = new BackgroundFileWriter(synchronous: true);
            var store = new KlaTestStore(directory, writer);
            var invocation = RecipeExecutionContractTests.Request(protocol);
            invocation = invocation with
            {
                Context = invocation.Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId, NodeId = fixture.Lease.Authority.BlockId },
                Restoration = fixture.Contract(), AcquisitionDeadlineUtc = fixture.Clock.GetUtcNow().AddMinutes(10),
                Definition = invocation.Definition with
                {
                    Settings = invocation.Definition.Settings with
                    {
                        MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 15, DOMinPercent = 10, DOMaxPercent = 85,
                        AirPrestageLeadPercent = 0, StabilityDerivativeSpanSeconds = 2,
                        StabilityDerivativeThresholdPercentPerSecond = .05, StabilityRequiredSamples = 3,
                        PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3, PrestageFlowStabilityStdDevLpm = 0
                    },
                    ProtocolSettings = invocation.Definition.ProtocolSettings with
                    {
                        OperatingRange = KlaOperatingRange.CurrentCultivation, RemovalTargetDoPercent = 10,
                        InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2
                    }
                }
            };
            var request = KlaRecipePulseMapper.Create(invocation, "simulator-A", invocation.Definition.Conditions[0].ConditionId,
                1, 1, fixture.Clock.GetUtcNow().AddMinutes(1));
            var document = store.CreateTest("recipe pulse", request.Definition);
            document.NitrogenSourceConfirmedUtc = fixture.Clock.GetUtcNow();
            document.NitrogenIsolationConfirmedUtc = fixture.Clock.GetUtcNow();
            var capabilities = new KlaAssayExecutionCapabilities
            {
                InstallationId = "simulator-A", ProfileId = invocation.Quality.ProfileId, ProfileVersion = invocation.Quality.Version,
                Protocols = [KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic], EvidenceId = "isolated-test", IsIsolatedSimulation = true
            };
            var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
            var factory = new KlaRecipeAssayExecutionFactory(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(), settings,
                fixture.Clock, isIsolatedEnvironment: true);
            var executor = factory.Create(fixture.Lease, document, new()
                {
                    MaximumTelemetryAgeSeconds = 5,
                    MinimumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 30 : null,
                    MaximumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 100 : null,
                    MaximumOxygenSlopePercentPerSecond = protocol == KlaAssayProtocol.Biotic ? .05 : null
                }, capabilities);
            var api = activeApi = new KlaAssayApi(Path.Combine(directory, "api.json"), executor, fixture.Clock);
            activeRequest = request.RequestId;
            api.Create(request); await api.StartAsync(request.RequestId);
            for (var index = 0; index < 100 && fixture.Device.Sent.Count == 0; index++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                fixture.Device.PushTelemetry(fixture.Sample(285, 2, command: 1, oxygen: 80));
                await Task.Delay(1);
            }
            Assert.NotEmpty(fixture.Device.Sent);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, document.FolderName),
                $"receita-{request.RequestId:N}-BeforeActuation.json", SearchOption.AllDirectories));
            var running = await api.StartAsync(request.RequestId);
            Assert.Equal(KlaAssayApiState.Running, running.State);
            fixture.Device.Sent.Clear();
            Task<KlaAssayApiObservation> cancelling;
            if (completeNormally)
            {
                void Push(double oxygen, double flow, GasRoute route, long command)
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    fixture.Device.PushTelemetry(fixture.Sample(300, flow, route, command: command, oxygen: oxygen));
                }
                if (protocol == KlaAssayProtocol.Abiotic)
                {
                    Push(80, 0, GasRoute.Closed, 2);
                    Push(70, 0, GasRoute.VentAndNitrogen, 3);
                    Push(10, 0, GasRoute.VentAndNitrogen, 3);
                    for (var index = 0; index < 6; index++) Push(10, 2, GasRoute.VentAndNitrogen, 4);
                    Push(10, 2, GasRoute.Reactor, 5);
                    for (var index = 1; index <= 400; index++)
                        Push(100 - 90 * Math.Exp(-40.0 * index / 3600), 2, GasRoute.Reactor, 5);
                    fixture.Device.Sent.Clear();
                    Push(99, 0, GasRoute.Closed, 6);
                }
                else
                {
                    Push(80, 2, GasRoute.VentAndNitrogen, 2);
                    for (var index = 1; index <= 10; index++) Push(80 - index, 2, GasRoute.VentAndNitrogen, 2);
                    Push(70, 2, GasRoute.Reactor, 3);
                    for (var index = 0; index < 4; index++) Push(70, 2, GasRoute.Reactor, 3);
                    fixture.Device.Sent.Clear();
                    for (var index = 0; index < 6; index++) Push(70, 2, GasRoute.Reactor, 4);
                }
                cancelling = api.WaitForCompletionAsync(request.RequestId);
            }
            else if (deadline)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(11));
                cancelling = api.WaitForCompletionAsync(request.RequestId);
            }
            else cancelling = api.CancelWithRecoveryAsync(request.RequestId);
            await fixture.DriveRoute(command: 20);
            if (failPersistence) writer.Run(Path.Combine(directory, document.FolderName, "controlled-error"),
                () => throw new IOException("terminal persistence failure"));
            await fixture.PushStable(command: 20);
            var completed = await cancelling.WaitAsync(TimeSpan.FromSeconds(3));
            if (completeNormally) Assert.True(completed.State is KlaAssayApiState.Completed or KlaAssayApiState.Inconclusive);
            else Assert.Equal(failPersistence ? KlaAssayApiState.PersistenceFailed : KlaAssayApiState.Cancelled, completed.State);
            Assert.Equal(KlaRestorationState.Confirmed, completed.Result!.Outcome.Restoration);
            Assert.Equal(!failPersistence, completed.Result.PersistenceReceiptId is not null);
            Assert.Equal(!failPersistence, fixture.Lease.HasReturnedSuccessfully);
            var saved = store.LoadTest(document.FolderName)!;
            var run = Assert.Single(saved.Runs);
            Assert.Equal(KlaOperatorDecision.Pending, run.Outcome!.OperatorDecision);
            Assert.NotEqual(RunPhase.Accepted, run.Phase);
            var analysis = store.LoadRunAnalysis(document.FolderName, run.FolderName);
            Assert.NotNull(analysis);
            Assert.NotNull(analysis.DeterministicResult);
            if (completeNormally)
            {
                Assert.True(analysis.DeterministicResult.Input!.Samples.Length > (protocol == KlaAssayProtocol.Abiotic ? 200 : 5));
                Assert.Equal(store.ReadRecipeAttemptReceipt(document.FolderName, run.FolderName, request.RequestId,
                    KlaAttemptPersistencePhase.Terminal)!.RawDataSha256, analysis.RawDataSha256);
            }
            Assert.Equal(completed, await api.StartAsync(request.RequestId));
            api.Dispose(); activeApi = null;
            using var reopened = new KlaAssayApi(Path.Combine(directory, "api.json"), executor, fixture.Clock);
            var historical = await reopened.StartAsync(request.RequestId);
            Assert.Equal(completed.State, historical.State);
            Assert.Equal(completed.Result.PersistenceReceiptId, historical.Result!.PersistenceReceiptId);
        }
        finally
        {
            if (activeApi is not null)
            {
                var cleanup = activeApi.WaitForCompletionAsync(activeRequest);
                for (var attempt = 0; attempt < 10 && !cleanup.IsCompleted; attempt++)
                {
                    fixture.Clock.Advance(TimeSpan.FromHours(1));
                    fixture.Device.PushTelemetry(fixture.Sample(command: 100));
                    await Task.Delay(10);
                }
                try { await cleanup.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                activeApi.Dispose();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
