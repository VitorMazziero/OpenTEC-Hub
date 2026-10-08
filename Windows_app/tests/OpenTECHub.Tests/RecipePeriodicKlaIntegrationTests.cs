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
    [InlineData(KlaAssayProtocol.Abiotic, false, false, true, true)] [InlineData(KlaAssayProtocol.Biotic, false, false, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, true, false, true, true)] [InlineData(KlaAssayProtocol.Biotic, true, false, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, true, true, true)] [InlineData(KlaAssayProtocol.Biotic, false, true, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, false, true, true, true)]
    [InlineData(KlaAssayProtocol.Biotic, false, false, true, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, true, false, true, true, true)]
    [InlineData(KlaAssayProtocol.Biotic, true, false, true, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, true, true, true, true)]
    [InlineData(KlaAssayProtocol.Biotic, false, true, true, true, true)]
    [InlineData(KlaAssayProtocol.Abiotic, false, false, true, true, true, true)]
    [InlineData(KlaAssayProtocol.Biotic, false, false, true, true, true, true)]
    public async Task Cascade_exit_pause_or_emergency_during_common_assay_awaits_terminal_group(KlaAssayProtocol protocol, bool pause, bool emergency, bool graph = false, bool concrete = false, bool ramp = false, bool completeAssay = false)
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
        var registry = new KlaRecipeOperationalProfileRegistry("test", clock, true);
        var qualified = KlaRecipeOperationalProfileTests.Profile(clock, protocol) with
        {
            Capabilities = capabilities, Template = template.Definition, Quality = template.Quality, MaximumRetry = template.Retry,
            MaximumRecoverySeconds = 30, RecoveryStabilitySeconds = 2, AgitationToleranceRpm = 2, FlowToleranceLpm = .05,
            RecoveryCriteria = new() { MaximumTelemetryAgeSeconds = 5, MinimumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 30 : null,
                MaximumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 100 : null,
                MaximumOxygenSlopePercentPerSecond = protocol == KlaAssayProtocol.Biotic ? .05 : null }
        };
        registry.Register(qualified);
        var router = concrete ? new KlaRecipeExecutionRouter(registry) : new KlaRecipeExecutionRouter(capabilities);
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
        var concreteSource = new KlaRecipeAutonomousWorkSource(registry, router, api, factory, store, settings, clock, writer,
            Path.Combine(root, "recipe-journal"), () => template.Context.CultivationId);
        using var engine = new RecipeEngine(arbiter, arbiter, settings, clock,
            periodicWorkSource: graph ? null : source,
            autonomousWorkSource: concrete ? concreteSource : graph ? new GraphSource(source, capabilities, () => result!) : null,
            rampExecution: ramp ? RecipeRampApplicationConfiguration.Create(root, true, writer) : null);
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
            if (concrete) assayNode.Set("minimumIntervalSeconds", .02);
            recipe.Nodes.AddRange([periodicNode, assayNode]);
            recipe.Connections.AddRange([new("start", ConnectorNames.Out, "periodic", ConnectorNames.In),
                new("periodic", ConnectorNames.Out, "kla", ConnectorNames.In)]);
        }
        if (ramp)
        {
            var delay = RecipeNode.Create(NodeType.Timer, id: "ramp-delay");
            delay.Set("duracao", 1); delay.Set("unidade", nameof(TimeUnit.Seconds));
            var rampNode = RecipeNode.Create(NodeType.LinearSetpointRamp, id: "ramp");
            rampNode.Set("cascadeNodeId", "casc");
            rampNode.Set("cancellationPolicy", nameof(RampCancellationPolicy.RestoreSnapshot));
            rampNode.Set("lines", new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject {
                ["variable"] = "Oxygen", ["oxygenTarget"] = "ActiveCascadeReference", ["startSource"] = "Explicit",
                ["initialSetpoint"] = 80, ["finalSetpoint"] = 90, ["endAfterSeconds"] = 1000 }));
            var afterRamp = RecipeNode.Create(NodeType.LogEvent, id: "after-ramp");
            afterRamp.Set("mensagem", "only after completed ramp");
            recipe.Nodes.AddRange([delay, rampNode, afterRamp]);
            recipe.Connections.AddRange([new("start", ConnectorNames.Out, "ramp-delay", ConnectorNames.In),
                new("ramp-delay", ConnectorNames.Out, "ramp", ConnectorNames.In),
                new("ramp", ConnectorNames.Out, "after-ramp", ConnectorNames.In)]);
        }
        try
        {
            await engine.StartAsync(recipe);
            fixture.Device.PushTelemetry(fixture.Sample(300, 2, command: 10, oxygen: 80));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (engine.CascadeTermsFor("casc") is null || clock.PendingTimers == 0) await Task.Delay(1, timeout.Token);
            if (ramp)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                while (Directory.GetFiles(root, "start.json", SearchOption.AllDirectories).Length == 0)
                    await Task.Delay(1, timeout.Token);
                // The next slot starts after the ramp's durable preparation.
                await Task.Delay(10, timeout.Token);
                clock.Advance(TimeSpan.FromSeconds(4));
            }
            else clock.Advance(TimeSpan.FromSeconds(5));
            if (concrete)
            {
                while (concreteSource.ActiveInvocations.Count == 0) await Task.Delay(1, timeout.Token);
                var active = concreteSource.ActiveInvocations.Single(); request = active.Request; orchestrator = active.Orchestrator;
            }
            else while (matrix is null) await Task.Delay(1, timeout.Token);
            var exit = false; var resumed = false; var emergencyCommandCount = 0;
            double? resumedReference = null;
            DateTimeOffset? resumedAt = null;
            double? ReadRampReference()
            {
                for (var step = 8000; step <= 8100; step++)
                {
                    var reference = step / 100d;
                    if (engine.TryConfirmRampCascadeReference("casc", new(SetpointVariable.Oxygen,
                        RampOxygenTarget.ActiveCascadeReference, reference, true), engine.ExecutionId) is not null)
                        return reference;
                }
                return null;
            }
            using var cancellation = new CancellationTokenSource();
            await KlaRecipeOrchestratorTests.Drive(engine.Completion, orchestrator!, fixture, request!, () => false,
                KlaRecipeOrchestratorTests.Scenario.Normal, cancellation, current =>
                {
                    if (completeAssay)
                    {
                        if (!exit && engine.AutonomousResults.Count == 1)
                        {
                            if (ramp)
                            {
                                var reference = ReadRampReference();
                                if (reference is null) return;
                                resumedReference ??= reference;
                                resumedAt ??= clock.GetUtcNow();
                                if (clock.GetUtcNow() - resumedAt.Value < TimeSpan.FromSeconds(5))
                                {
                                    clock.Advance(TimeSpan.FromSeconds(1));
                                    return;
                                }
                                Assert.True(reference > resumedReference, "Rampa não voltou a avançar após devolver o controle.");
                                Assert.True(reference - resumedReference < .15, "Rampa saltou o tempo suspenso do ensaio.");
                            }
                            exit = true;
                            gate.Set("operacao", nameof(ManualGateOperation.Pass));
                        }
                        return;
                    }
                    if (pause && exit && !resumed && (concrete ? concreteSource.ActiveInvocations.Count == 0 && engine.AutonomousResults.Count > 0 : matrix!.IsCompleted))
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
            if (concrete)
            {
                result = engine.AutonomousResults.Single();
                scheduleId = result.PeriodicInvocation!.ScheduleRunId;
                journal = new RecipePeriodicJournal(Path.Combine(root, "recipe-journal"), engine.ExecutionId, result.Context.RecipeSha256, writer, clock);
            }
            Assert.True(exit, $"Ensaio terminou antes da recuperação: {result?.Status}, {result?.Reason}; engine {engine.State}, {engine.StatusReason}");
            Assert.True(engine.State == (emergency ? RecipeRunState.Stopped : RecipeRunState.Completed), engine.StatusReason);
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
                if (completeAssay) Assert.Contains(result.Status, new[] { KlaRecipeTerminalStatus.Completed, KlaRecipeTerminalStatus.CompletedWithWarnings });
                else Assert.Equal(KlaRecipeTerminalStatus.Cancelled, result.Status);
                Assert.True(result.PreAssayStateRestored); Assert.True(result.PersistenceConfirmed);
                Assert.Single(result.Attempts);
            }
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(store.ReadRecipeResult(result.SessionFolder, request!.Context.InvocationId)));
            Assert.Single(result.Pulses[0].RecipePulse!.Invocation.Restoration.BeforeAssay.Controllers);
            Assert.Null(engine.CascadeTermsFor("casc"));
            if (ramp)
            {
                var path = Assert.Single(Directory.GetFiles(root, "terminal.json", SearchOption.AllDirectories));
                var terminal = JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(File.ReadAllText(path))!;
                Assert.Equal(emergency ? RecipeRampTerminalStatus.EmergencyStopped : RecipeRampTerminalStatus.Cancelled, terminal.Status);
                Assert.Equal(!emergency, terminal.HasVerifiedRecovery);
                if (emergency) Assert.Equal(RecipeRampReturnOutcome.SuppressedForEmergency, terminal.ReturnOutcome);
                else Assert.Equal(80, Assert.Single(terminal.Recovery!.References).Reference);
                Assert.True(terminal.ActiveSeconds < 15, $"Rampa acumulou tempo durante o ensaio: {terminal.ActiveSeconds}");
                Assert.Equal(emergency ? NodeState.Error : NodeState.Cancelled, engine.NodeStateOf("ramp"));
                Assert.Equal(NodeState.Waiting, engine.NodeStateOf("after-ramp"));
                Assert.False(engine.WasTraversed(recipe.Connections.Single(edge => edge.SourceNodeId == "ramp")));
            }
            Assert.Equal(new[] { RecipePeriodicSlotState.Started, completeAssay ? RecipePeriodicSlotState.Completed : RecipePeriodicSlotState.Cancelled }, journal!.Read(scheduleId).Select(r => r.State));
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
