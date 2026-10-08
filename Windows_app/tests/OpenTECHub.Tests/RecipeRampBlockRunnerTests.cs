using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampBlockRunnerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RecipeAdvancesPastRampOnlyAfterItsTerminalReceiptExists(bool useBlockDeadline, bool applicationComposition, bool simulation)
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var writer = new BackgroundFileWriter();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ramp-graph-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        var runtime = new RecipeRampExecutionConfiguration(store, (engine, configuration) => useBlockDeadline
            ? configuration.CompletionCriteria!.CreateDestination(engine, configuration, null) : new(engine, configuration,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))),
            TimeSpan.FromSeconds(3), useBlockDeadline ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10));
        if (applicationComposition) runtime = RecipeRampApplicationConfiguration.Create(root, simulation, writer);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()),
            TimeProvider.System, rampExecution: runtime);
        arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var ramp = RecipeNode.Create(NodeType.LinearSetpointRamp, id: "ramp");
        // Feedback arrives after the runtime fallback deadline, but within the frozen block deadline.
        ramp.Set("confirmationStabilitySeconds", 0);
        ramp.Set("maximumTelemetryGapSeconds", 1);
        ramp.Set("confirmationTimeoutSeconds", useBlockDeadline ? 1 : 3);
        ramp.Set("lines", new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject {
            ["variable"] = "Temperature", ["startSource"] = "Explicit", ["initialSetpoint"] = 28,
            ["finalSetpoint"] = 30, ["endAfterSeconds"] = .05 }));
        var after = RecipeNode.Create(NodeType.LogEvent, id: "after"); after.Set("mensagem", "after ramp");
        var recipe = new RecipeDocument();
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), ramp, after, RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.Add(new("start", ConnectorNames.Out, "ramp", ConnectorNames.In));
        recipe.Connections.Add(new("ramp", ConnectorNames.Out, "after", ConnectorNames.In));
        recipe.Connections.Add(new("after", ConnectorNames.Out, "end", ConnectorNames.In));
        var advanced = new TaskCompletionSource<RecipeRampTerminalCheckpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Logged += entry =>
        {
            if (entry.NodeId != "after") return;
            try
            {
                var path = Assert.Single(System.IO.Directory.GetFiles(root, "terminal.json", System.IO.SearchOption.AllDirectories));
                if (applicationComposition) Assert.Contains(System.IO.Path.Combine("AutomacaoRampas", simulation ? "simulacao" : "fisico"), path);
                advanced.TrySetResult(System.Text.Json.JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(System.IO.File.ReadAllText(path))!);
            }
            catch (Exception error) { advanced.TrySetException(error); }
        };
        double reference = 25;
        var finalSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (command.Contains(CommandKeys.TempSetpoint))
            {
                var value = RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint);
                Volatile.Write(ref reference, value);
                if (value == 30) finalSent.TrySetResult();
            }
        };
        using var telemetryStop = new CancellationTokenSource();
        var telemetry = Task.Run(async () =>
        {
            try
            {
                if (useBlockDeadline)
                {
                    await finalSent.Task.WaitAsync(telemetryStop.Token);
                    await Task.Delay(200, telemetryStop.Token);
                }
                while (true)
                {
                    await Task.Delay(10, telemetryStop.Token);
                    var value = Volatile.Read(ref reference);
                    device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = value, TempSetpointCommanded = true,
                        Temperature = value, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
                }
            }
            catch (OperationCanceledException) when (telemetryStop.IsCancellationRequested) { }
        });
        try
        {
            Assert.True(engine.CanStart(recipe, out var reason), reason);
            await engine.StartAsync(recipe);
            var terminal = await advanced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecipeRampTerminalStatus.Completed, terminal.Status);
            Assert.Equal(30, Assert.Single(terminal.FinalConfirmations).Reference);
            if (applicationComposition)
            {
                var initial = runtime.Store.ReadStart(engine.ExecutionId, terminal.InvocationId)!;
                Assert.Equal(1, initial.Configuration.CompletionCriteria!.TimeoutSeconds);
                Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, "AutomacaoRampas", simulation ? "fisico" : "simulacao")));
            }
        }
        finally { telemetryStop.Cancel(); await telemetry; await engine.StopAsync("graph verified"); }
    }

    [Fact]
    public void ApplicationFreezesLegacyCriteriaWithoutChangingOriginalConfiguration()
    {
        using var writer = new BackgroundFileWriter();
        var runtime = RecipeRampApplicationConfiguration.Create(System.IO.Path.GetTempPath(), false, writer);
        var legacy = new RecipeRampBlockConfiguration(new() { Lines = [new() {
            Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 }] }, null);
        var prepared = runtime.PrepareConfiguration!(legacy);
        Assert.Null(legacy.CompletionCriteria);
        Assert.Equal(RecipeRampCompletionCriteria.OperationalDefaults, prepared.CompletionCriteria);
        Assert.Same(legacy.Definition, prepared.Definition);
        Assert.Same(prepared, runtime.PrepareConfiguration(prepared));
        runtime.Validate();
    }

    [Theory]
    [InlineData(false, RampCancellationPolicy.HoldLastReferences, 0)]
    [InlineData(true, RampCancellationPolicy.HoldLastReferences, 0)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot, 0)]
    [InlineData(false, RampCancellationPolicy.HoldLastReferences, 1)]
    [InlineData(false, RampCancellationPolicy.HoldLastReferences, 2)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot, 3)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot, 4)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot, 5)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot, 6)]
    public async Task InvocationPreparesExecutesAndPersistsItsCompletionOrCancellation(bool cancel, RampCancellationPolicy policy, int pauseKind)
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), TimeProvider.System);
        var recipe = new RecipeDocument();
        var timer = RecipeNode.Create(NodeType.Timer, id: "timer");
        timer.Set("duracao", 600); timer.Set("unidade", nameof(TimeUnit.Seconds));
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), timer, RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.Add(new("start", ConnectorNames.Out, "timer", ConnectorNames.In));
        recipe.Connections.Add(new("timer", ConnectorNames.Out, "end", ConnectorNames.In));
        await engine.StartAsync(recipe);
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        using var writer = new BackgroundFileWriter();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ramp-runner-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = policy, Lines = [new() {
            Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit, InitialSetpoint = 28,
            FinalSetpoint = 30, EndAfterSeconds = cancel ? 60 : pauseKind != 0 ? .2 : .05 }] }, null, RampTemperatureRoute.NativeModule);
        var expectedSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoverySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        double reference = 25;
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (!command.Contains(CommandKeys.TempSetpoint)) return;
            var value = RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint);
            Volatile.Write(ref reference, value);
            if (value == 25) recoverySent.TrySetResult();
            if (pauseKind is 1 or 2 ? value >= 28 && value < 30 : value == (cancel ? 28 : 30))
                expectedSent.TrySetResult();
        };
        using var telemetryStop = new CancellationTokenSource();
        var telemetry = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(10, telemetryStop.Token);
                    var value = Volatile.Read(ref reference);
                    if (pauseKind is 4 or 5 && value == 25) continue;
                    device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = value, TempSetpointCommanded = true,
                        Temperature = value, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
                }
            }
            catch (OperationCanceledException) when (telemetryStop.IsCancellationRequested) { }
        });
        try
        {
            var runner = new RecipeRampBlockRunner(engine, arbiter, store, frozen => pauseKind == 6
                ? throw new InvalidOperationException("destination unavailable") : new(engine, frozen,
                new(2, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
                new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
                new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))));
            using var cancellation = new CancellationTokenSource();
            var running = runner.ExecuteAsync("ramp", configuration, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
                pauseKind is 4 or 5 ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(10), cancellation.Token);
            if (pauseKind == 6)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("destination unavailable", error.Message);
                var path = Assert.Single(System.IO.Directory.GetFiles(root, "terminal.json", System.IO.SearchOption.AllDirectories));
                var failed = System.Text.Json.JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(System.IO.File.ReadAllText(path))!;
                Assert.Equal(RecipeRampTerminalStatus.Faulted, failed.Status);
                Assert.Equal(RecipeRampReturnOutcome.Failed, failed.ReturnOutcome);
                Assert.False(failed.HasVerifiedRecovery);
                Assert.Equal(25, Volatile.Read(ref reference));
                Assert.False(expectedSent.Task.IsCompleted);
                Assert.NotNull(store.ReadStart(engine.ExecutionId, failed.InvocationId));
                return;
            }
            await expectedSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (pauseKind is 1 or 2)
            {
                RecipeAssayResourceLease? assay = null;
                if (pauseKind == 1) engine.Pause();
                else assay = await engine.Resources!.ReserveForAssayAsync(
                    RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId, NodeId = "assay" },
                    [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Temperature], TimeSpan.FromSeconds(3));
                await Task.Delay(50);
                var pausedReference = Volatile.Read(ref reference);
                var count = device.Sent.Count;
                await Task.Delay(300);
                Assert.False(running.IsCompleted);
                Assert.Equal(pausedReference, Volatile.Read(ref reference));
                Assert.Equal(count, device.Sent.Count);
                if (assay is not null) assay.AbortBeforeAssay(); else engine.Resume();
            }
            if (pauseKind == 3) arbiter.DispatchSafety(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 0), "emergency during ramp");
            else if (cancel) cancellation.Cancel();
            if (pauseKind is 4 or 5)
            {
                await recoverySent.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (pauseKind == 5)
                {
                    arbiter.DispatchSafety(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 0), "emergency during recovery");
                    arbiter.Claim(CommandOwner.Manual, [ActuatorId.Temperature], "later manual ownership notification");
                }
                else
                {
                    await Assert.ThrowsAsync<AggregateException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                    var failurePath = Assert.Single(System.IO.Directory.GetFiles(root, "terminal.json", System.IO.SearchOption.AllDirectories));
                    var failure = System.Text.Json.JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(System.IO.File.ReadAllText(failurePath))!;
                    Assert.Equal(RecipeRampTerminalStatus.Faulted, failure.Status);
                    Assert.Equal(RecipeRampReturnOutcome.Failed, failure.ReturnOutcome);
                    Assert.False(failure.HasVerifiedRecovery);
                    Assert.Equal(25, Volatile.Read(ref reference));
                    return;
                }
            }
            var terminal = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(pauseKind is 3 or 5 ? RecipeRampTerminalStatus.EmergencyStopped :
                cancel ? RecipeRampTerminalStatus.Cancelled : RecipeRampTerminalStatus.Completed, terminal.Status);
            Assert.Equal(pauseKind is 3 or 5 ? 0 : cancel ? policy == RampCancellationPolicy.RestoreSnapshot ? 25 : 28 : 30, Volatile.Read(ref reference));
            Assert.Equal(pauseKind is not (3 or 5) && cancel && policy == RampCancellationPolicy.RestoreSnapshot, terminal.HasVerifiedRecovery);
            if (pauseKind is 3 or 5) Assert.Equal(RecipeRampReturnOutcome.SuppressedForEmergency, terminal.ReturnOutcome);
            Assert.Equal(terminal.Status, store.ReadTerminal(engine.ExecutionId, terminal.InvocationId)!.Status);
            Assert.NotNull(store.ReadStart(engine.ExecutionId, terminal.InvocationId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync("ramp", configuration,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), default));
        }
        finally
        {
            telemetryStop.Cancel(); await telemetry;
            await engine.StopAsync("runner verified");
        }
    }
}
