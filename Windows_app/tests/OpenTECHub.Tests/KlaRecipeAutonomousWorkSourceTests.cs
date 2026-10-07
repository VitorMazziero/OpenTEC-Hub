using System.IO;
using System.Text.Json.Nodes;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeAutonomousWorkSourceTests
{
    private sealed class Producer(Action resumed) : IRecipeResourceProducer
    {
        public string NodeId => "producer";
        public IReadOnlyList<ActuatorId> Resources => [ActuatorId.Agitation, ActuatorId.Aeration];
        public bool Suspended; public int Resumes;
        private void Resume() { Suspended = false; Resumes++; resumed(); }
        public Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct)
        { Suspended = true; return Task.FromResult<IRecipeResourceSuspension>(new Suspension(this)); }
        private sealed class Suspension(Producer producer) : IRecipeResourceSuspension
        {
            public void Resume() => producer.Resume();
            public void Stop() => producer.Suspended = false;
        }
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, false)] [InlineData(KlaAssayProtocol.Biotic, false)]
    [InlineData(KlaAssayProtocol.Abiotic, true)] [InlineData(KlaAssayProtocol.Biotic, true)]
    public async Task Concrete_provider_runs_single_or_matrix_preserves_first_reservation_and_recaptures_later_pulses(KlaAssayProtocol protocol, bool multiple)
    {
        var root = Path.Combine(Path.GetTempPath(), "recipe-source-" + Guid.NewGuid().ToString("N"));
        using var fixture = new RecipeAssayRestorationTests.Fixture(virtualTimers: true);
        using var writer = new BackgroundFileWriter(synchronous: true);
        var clock = fixture.Clock; var arbiter = fixture.Arbiter;
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], "source");
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorControlMode(true));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, 30));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowmeterLoopEnabled(true));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, fixture.Rig));
        var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
        var store = new KlaTestStore(root, writer);
        var profile = KlaRecipeOperationalProfileTests.Profile(clock, protocol);
        profile = profile with
        {
            MaximumRetry = profile.MaximumRetry with { MaximumBlockSeconds = 1800, MinimumInterAssaySeconds = .02, MaximumCumulativeGasOffSecondsPerCultivation = 1000 },
            MaximumRecoverySeconds = 30, RecoveryStabilitySeconds = 2, AgitationToleranceRpm = 2, FlowToleranceLpm = .05,
            Quality = profile.Quality with { AllowedConditionalReasonCodes = ["constant_process_conditions_not_independently_verified",
                "unknown_probe_response_rate_is_conditional", "constant_representative_our_not_independently_verified",
                "physical_saturation_unavailable_balance_not_checked", "ols_interval_is_conditional_on_equilibrium_and_correlated_errors"] },
            Template = profile.Template with
            {
                Settings = profile.Template.Settings with { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 15,
                    MaxReoxygenationTimeMinutes = 4, DOMinPercent = 10, DOMaxPercent = 75, AirPrestageLeadPercent = 0,
                    StabilityDerivativeSpanSeconds = 2, StabilityDerivativeThresholdPercentPerSecond = .05, StabilityRequiredSamples = 3,
                    PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3, PrestageFlowStabilityStdDevLpm = 0 },
                ProtocolSettings = profile.Template.ProtocolSettings with { OperatingRange = KlaOperatingRange.CurrentCultivation,
                    RemovalTargetDoPercent = 10, InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2,
                    AerationReturn = profile.Template.ProtocolSettings.AerationReturn with { MinimumInterAssaySeconds = .02 } }
            }
        };
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", clock, true); registry.Register(profile);
        var router = new KlaRecipeExecutionRouter(registry);
        using var api = new KlaAssayApi(Path.Combine(root, "api.json"), router, clock);
        var factory = new KlaRecipeAssayExecutionFactory(fixture.Device, arbiter, store, new KlaAnalysisEngine(), settings, clock, true);
        var source = new KlaRecipeAutonomousWorkSource(registry, router, api, factory, store, settings, clock, writer,
            Path.Combine(root, "journal"), () => "culture-1");
        var coordinator = new RecipeResourceCoordinator(arbiter, clock);
        var desired = 300;
        var producer = new Producer(() => arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(desired += 10)));
        coordinator.Register(producer);
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("protocol", protocol.ToString()); node.Set("maximumBlockSeconds", 1800);
        node.Set("minimumIntervalSeconds", .02); node.Set("maximumAttemptsPerCultivation", 10); node.Set("maximumCultivationGasOffSeconds", 1000);
        if (multiple)
        {
            node.Set("conditionsMode", nameof(RecipeKlaConditionMode.Multiple));
            node.Set("conditions", new JsonArray(new JsonObject { ["agitationRpm"] = 450, ["airflowLpm"] = 3, ["replicates"] = 2 },
                new JsonObject { ["agitationRpm"] = 500, ["airflowLpm"] = 4, ["replicates"] = 2 }));
        }
        var recipe = new RecipeDocument(); recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), node,
            RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "kla", ConnectorNames.In), new("kla", ConnectorNames.Out, "end", ConnectorNames.In)]);
        var plan = source.CreateWork(recipe, Guid.NewGuid(), coordinator);
        using var cancellation = new CancellationTokenSource();
        var execution = plan.Assays.Single().Execute(null, cancellation.Token);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (source.ActiveInvocations.Count == 0 && !execution.IsCompleted) await Task.Delay(1, timeout.Token);
            if (execution.IsCompleted) await execution;
            var active = source.ActiveInvocations.Single();
            Assert.Equal(0, producer.Resumes); Assert.True(producer.Suspended);
            await KlaRecipeOrchestratorTests.Drive(execution, active.Orchestrator, fixture, active.Request,
                () => producer.Suspended, KlaRecipeOrchestratorTests.Scenario.Normal, cancellation);
            var result = await execution;
            Assert.True(result.Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings,
                $"{result.Status}: {result.Reason}");
            Assert.Equal(multiple ? 4 : 1, result.Attempts.Length);
            Assert.Equal(multiple ? new double[] { 300, 310, 320, 330 } : new double[] { 300 },
                result.Pulses.Select(p => p.RecipePulse!.Invocation.Restoration.BeforeAssay.AgitationSetpointRpm));
            Assert.Equal(result.Attempts.Length, producer.Resumes); Assert.False(producer.Suspended);
            Assert.Empty(source.ActiveInvocations);
            var reopened = store.LoadTest(result.SessionFolder);
            Assert.NotNull(reopened);
            Assert.Equal(result.Context, reopened.RecipeRequest!.Context);
            Assert.Equal(result.Attempts.Length + 1,
                File.ReadAllLines(Path.Combine(root, result.SessionFolder, KlaAutomaticResultsSummary.FileName)).Length);
        }
        finally
        {
            cancellation.Cancel();
            if (!execution.IsCompleted && source.ActiveInvocations.SingleOrDefault() is { } active)
                await KlaRecipeOrchestratorTests.Drive(execution, active.Orchestrator, fixture, active.Request,
                    () => producer.Suspended, KlaRecipeOrchestratorTests.Scenario.Normal, cancellation);
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            api.Dispose(); Directory.Delete(root, recursive: true);
        }
    }
}



