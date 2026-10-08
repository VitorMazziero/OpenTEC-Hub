using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampBlockRunnerTests
{
    [Fact]
    public async Task RecipeAdvancesPastRampOnlyAfterItsTerminalReceiptExists()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var writer = new BackgroundFileWriter();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ramp-graph-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        var runtime = new RecipeRampExecutionConfiguration(store, (engine, configuration) => new(engine, configuration,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10));
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()),
            TimeProvider.System, rampExecution: runtime);
        arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var ramp = RecipeNode.Create(NodeType.LinearSetpointRamp, id: "ramp");
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
                advanced.TrySetResult(System.Text.Json.JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(System.IO.File.ReadAllText(path))!);
            }
            catch (Exception error) { advanced.TrySetException(error); }
        };
        double reference = 25;
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (command.Contains(CommandKeys.TempSetpoint)) Volatile.Write(ref reference,
                RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint));
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
        }
        finally { telemetryStop.Cancel(); await telemetry; await engine.StopAsync("graph verified"); }
    }

    [Theory]
    [InlineData(false, RampCancellationPolicy.HoldLastReferences)]
    [InlineData(true, RampCancellationPolicy.HoldLastReferences)]
    [InlineData(true, RampCancellationPolicy.RestoreSnapshot)]
    public async Task InvocationPreparesExecutesAndPersistsItsCompletionOrCancellation(bool cancel, RampCancellationPolicy policy)
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
            FinalSetpoint = 30, EndAfterSeconds = cancel ? 60 : .05 }] }, null, RampTemperatureRoute.NativeModule);
        var expectedSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        double reference = 25;
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (!command.Contains(CommandKeys.TempSetpoint)) return;
            var value = RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint);
            Volatile.Write(ref reference, value);
            if (value == (cancel ? 28 : 30)) expectedSent.TrySetResult();
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
                    device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = value, TempSetpointCommanded = true,
                        Temperature = value, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
                }
            }
            catch (OperationCanceledException) when (telemetryStop.IsCancellationRequested) { }
        });
        try
        {
            var runner = new RecipeRampBlockRunner(engine, arbiter, store, frozen => new(engine, frozen,
                new(2, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
                new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
                new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))));
            using var cancellation = new CancellationTokenSource();
            var running = runner.ExecuteAsync("ramp", configuration, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), cancellation.Token);
            await expectedSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancel) cancellation.Cancel();
            var terminal = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? RecipeRampTerminalStatus.Cancelled : RecipeRampTerminalStatus.Completed, terminal.Status);
            Assert.Equal(cancel ? policy == RampCancellationPolicy.RestoreSnapshot ? 25 : 28 : 30, Volatile.Read(ref reference));
            Assert.Equal(cancel && policy == RampCancellationPolicy.RestoreSnapshot, terminal.HasVerifiedRecovery);
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
