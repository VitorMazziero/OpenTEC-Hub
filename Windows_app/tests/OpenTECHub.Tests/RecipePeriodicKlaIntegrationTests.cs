using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipePeriodicKlaIntegrationTests
{
    private sealed class Source(Func<RecipeResourceCoordinator, Guid, string, RecipePeriodicWork> create) : IRecipePeriodicWorkSource
    {
        public IReadOnlyList<RecipePeriodicWork> CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator? resources)
            => [create(resources!, executionId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RecipeSerializer.Serialize(recipe)))).ToLowerInvariant())];
    }
    private sealed class GraphSource(Source source, KlaAssayExecutionCapabilities capability, Func<KlaRecipeResult> result) : IRecipeAutonomousWorkSource
    {
        public bool CanExecute(RecipeDocument recipe, out string? reason) { reason = null; return true; }
        public RecipeAutonomousExecutionPlan CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator resources)
        {
            var work = source.CreateWork(recipe, executionId, resources).Single();
            return new([new("kla", capability, async (invocation, ct) => { await work.Execute(invocation!, ct); return result(); })],
                [new(work.Identity, work.DispatchTolerance, work.Record)]);
        }
    }
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, false, false)] [InlineData(KlaAssayProtocol.Biotic, false, false)]
    [InlineData(KlaAssayProtocol.Abiotic, true, false)] [InlineData(KlaAssayProtocol.Biotic, true, false)]
    [InlineData(KlaAssayProtocol.Abiotic, false, true)] [InlineData(KlaAssayProtocol.Biotic, false, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, false, true)] [InlineData(KlaAssayProtocol.Biotic, false, false, true)]
    [InlineData(KlaAssayProtocol.Abiotic, true, false, true)] [InlineData(KlaAssayProtocol.Biotic, true, false, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, true, true)] [InlineData(KlaAssayProtocol.Biotic, false, true, true)]
    public async Task Cascade_exit_pause_or_emergency_during_common_assay_awaits_terminal_group(KlaAssayProtocol protocol, bool pause, bool emergency, bool graph = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "periodic-kla-" + Guid.NewGuid().ToString("N"));
        using var fixture = new RecipeAssayRestorationTests.Fixture(virtualTimers: true);
        using var writer = new BackgroundFileWriter(synchronous: true);
        var store = new KlaTestStore(root, writer);
        var clock = fixture.Clock; var arbiter = fixture.Arbiter;
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorControlMode(true));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowmeterLoopEnabled(true));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, fixture.Rig));
        var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
        var template = RecipeExecutionContractTests.Request(protocol);
        template = template with
        {
            AcquisitionDeadlineUtc = clock.GetUtcNow().AddHours(1),
            Retry = template.Retry with { MaximumBlockSeconds = 1800, MinimumInterAssaySeconds = .02 },
            Quality = template.Quality with { AllowedConditionalReasonCodes =
                ["constant_process_conditions_not_independently_verified", "unknown_probe_response_rate_is_conditional",
                 "constant_representative_our_not_independently_verified", "physical_saturation_unavailable_balance_not_checked",
                 "ols_interval_is_conditional_on_equilibrium_and_correlated_errors"] },
            Definition = template.Definition with
            {
                SequenceLimits = null,
                Settings = template.Definition.Settings with { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 15,
                    MaxReoxygenationTimeMinutes = 4, DOMinPercent = 10, DOMaxPercent = 75, AirPrestageLeadPercent = 0,
                    StabilityDerivativeSpanSeconds = 2, StabilityDerivativeThresholdPercentPerSecond = .05,
                    StabilityRequiredSamples = 3, PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3,
                    PrestageFlowStabilityStdDevLpm = 0 },
                ProtocolSettings = template.Definition.ProtocolSettings with { OperatingRange = KlaOperatingRange.CurrentCultivation,
                    RemovalTargetDoPercent = 10, InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2,
                    AerationReturn = template.Definition.ProtocolSettings.AerationReturn with { MinimumInterAssaySeconds = .02 } }
            }
        };
        var capabilities = new KlaAssayExecutionCapabilities { InstallationId = "test", ProfileId = template.Quality.ProfileId,
            ProfileVersion = template.Quality.Version, Protocols = [KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic],
            EvidenceId = "isolated-periodic", IsIsolatedSimulation = true };
        var factory = new KlaRecipeAssayExecutionFactory(fixture.Device, arbiter, store, new KlaAnalysisEngine(), settings, clock, true);
        var router = new KlaRecipeExecutionRouter(capabilities);
        using var api = new KlaAssayApi(Path.Combine(root, "api.json"), router, clock);
        KlaRecipeOrchestrator? orchestrator = null;
        KlaRecipeRequest? request = null; KlaRecipeResult? result = null;
        Task<KlaRecipeResult>? matrix = null;
        RecipePeriodicJournal? journal = null; Guid scheduleId = Guid.Empty;
        var source = new Source((resources, runId, hash) =>
        {
            journal = new RecipePeriodicJournal(Path.Combine(root, "recipe-journal"), runId, hash, writer, clock);
            var preparer = new KlaRecipePulsePreparer(resources, factory, settings, clock, capabilities,
                new() { MaximumTelemetryAgeSeconds = 5, MinimumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 30 : null,
                    MaximumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 100 : null,
                    MaximumOxygenSlopePercentPerSecond = protocol == KlaAssayProtocol.Biotic ? .05 : null }, TimeSpan.FromSeconds(30));
            orchestrator = new KlaRecipeOrchestrator(api, router, preparer, store, clock);
            return new(new() { ScheduleRunId = scheduleId = Guid.NewGuid(), SchedulerNodeId = "periodic", TargetNodeId = "kla",
                CoordinatedCascadeNodeId = "casc", SlotIndex = 0, Schedule = new() { InitialDelaySeconds = 5, PeriodSeconds = 600 } },
                TimeSpan.FromSeconds(1), async (invocation, ct) =>
                {
                    var context = template.Context with { RecipeRunId = runId, NodeId = "kla", RecipeSha256 = hash };
                    var initial = await resources.ReserveForAssayAsync(context,
                        [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(30), ct);
                    var snapshot = initial.CaptureReturnSnapshot(fixture.Rig, clock); initial.AbortBeforeAssay();
                    request = template with { Context = context, PeriodicInvocation = invocation,
                        Restoration = template.Restoration with { BeforeAssay = snapshot,
                            MaximumRecoverySeconds = 30, StabilitySeconds = 2, AgitationToleranceRpm = 2, FlowToleranceLpm = .05 } };
                    var document = store.CreateTest("periodic common assay", request.Definition);
                    document.NitrogenSourceConfirmedUtc = clock.GetUtcNow(); document.NitrogenIsolationConfirmedUtc = clock.GetUtcNow();
                    store.SaveTestManifest(document);
                    matrix = orchestrator.ExecuteAsync(request, document, ct);
                    result = await matrix;
                }, journal.RecordAsync);
        });
        using var engine = new RecipeEngine(arbiter, arbiter, settings, clock,
            periodicWorkSource: graph ? null : source,
            autonomousWorkSource: graph ? new GraphSource(source, capabilities, () => result!) : null);
        var recipe = new RecipeDocument { Name = "periodic common assay" };
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("spO2", 80.0); cascade.Set("nMinRpm", 300.0); cascade.Set("nMaxRpm", 350.0);
        cascade.Set("qMinVvm", 2.0); cascade.Set("qMaxVvm", 3.0);
        var gate = RecipeNode.Create(NodeType.ManualIntervention, id: "gate");
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), cascade, gate, RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "casc", ConnectorNames.In),
            new("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In),
            new("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn), new("casc", ConnectorNames.Out, "end", ConnectorNames.In)]);
        if (graph)
        {
            var periodicNode = RecipeNode.Create(NodeType.Periodic, id: "periodic");
            periodicNode.Set("initialDelay", 5); periodicNode.Set("initialDelayUnit", nameof(TimeUnit.Seconds));
            periodicNode.Set("period", 600); periodicNode.Set("periodUnit", nameof(TimeUnit.Seconds));
            periodicNode.Set("coordinatedCascadeId", "casc");
            var assayNode = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
            assayNode.Set("protocol", protocol.ToString()); assayNode.Set("profileId", capabilities.ProfileId);
            assayNode.Set("profileVersion", capabilities.ProfileVersion);
            recipe.Nodes.AddRange([periodicNode, assayNode]);
            recipe.Connections.AddRange([new("start", ConnectorNames.Out, "periodic", ConnectorNames.In),
                new("periodic", ConnectorNames.Out, "kla", ConnectorNames.In)]);
        }
        try
        {
            await engine.StartAsync(recipe);
            fixture.Device.PushTelemetry(fixture.Sample(300, 2, command: 10, oxygen: 80));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (engine.CascadeTermsFor("casc") is null) await Task.Delay(1, timeout.Token);
            clock.Advance(TimeSpan.FromSeconds(5));
            while (matrix is null) await Task.Delay(1, timeout.Token);
            var exit = false; var resumed = false; var emergencyCommandCount = 0;
            using var cancellation = new CancellationTokenSource();
            await KlaRecipeOrchestratorTests.Drive(engine.Completion, orchestrator!, fixture, request!, () => false,
                KlaRecipeOrchestratorTests.Scenario.Normal, cancellation, current =>
                {
                    if (pause && exit && !resumed && matrix.IsCompleted)
                    {
                        Assert.NotNull(engine.CascadeTermsFor("casc")); Assert.Equal(RecipeRunState.Paused, engine.State);
                        resumed = true; gate.Set("operacao", nameof(ManualGateOperation.Pass)); engine.Resume();
                    }
                    if (exit || current.CurrentPhase != RunPhase.Reoxygenating) return;
                    exit = true;
                    if (emergency)
                    {
                        arbiter.DispatchSafety(CommandBuilders.MotorSetpoint(0), "test emergency");
                        emergencyCommandCount = fixture.Device.Sent.Count;
                    }
                    else if (pause) engine.Pause(); else gate.Set("operacao", nameof(ManualGateOperation.Pass));
                });
            await engine.Completion.WaitAsync(timeout.Token);
            Assert.True(exit, $"Ensaio terminou antes da recuperação: {result?.Status}, {result?.Reason}; engine {engine.State}, {engine.StatusReason}");
            Assert.Equal(emergency ? RecipeRunState.Stopped : RecipeRunState.Completed, engine.State);
            Assert.NotNull(result);
            if (graph) Assert.Single(engine.AutonomousResults);
            if (emergency)
            {
                Assert.False(result.PreAssayStateRestored);
                Assert.Equal(emergencyCommandCount, fixture.Device.Sent.Count);
                Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Agitation));
            }
            else
            {
                Assert.Equal(KlaRecipeTerminalStatus.Cancelled, result.Status);
                Assert.True(result.PreAssayStateRestored); Assert.True(result.PersistenceConfirmed);
                Assert.Single(result.Attempts);
            }
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(store.ReadRecipeResult(result.SessionFolder, request!.Context.InvocationId)));
            Assert.Single(result.Pulses[0].RecipePulse!.Invocation.Restoration.BeforeAssay.Controllers);
            Assert.Null(engine.CascadeTermsFor("casc"));
            Assert.Equal(new[] { RecipePeriodicSlotState.Started, RecipePeriodicSlotState.Cancelled }, journal!.Read(scheduleId).Select(r => r.State));
        }
        finally
        {
            if (!engine.Completion.IsCompleted)
            {
                var stopping = engine.StopAsync("test cleanup");
                if (matrix is not null && request is not null)
                {
                    using var cleanup = new CancellationTokenSource();
                    await KlaRecipeOrchestratorTests.Drive(stopping, orchestrator!, fixture, request, () => false,
                        KlaRecipeOrchestratorTests.Scenario.Normal, cleanup);
                }
                await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            }
            api.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
