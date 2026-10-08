using System.Text.Json.Nodes;
using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The recipe execution engine (Phase 3 WP4 part 2): it deactivates manual control by claiming every
/// actuator on start, drives the shared arbiter under <see cref="CommandOwner.Recipe"/>, reacts to
/// live telemetry, and safe-aborts on link loss. Driven against an injected clock and a recording
/// device, with capped delays so the async run finishes fast without wall-clock waits.
/// </summary>
public sealed class RecipeEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadeExitWaitsForAssociatedRampRecoveryBeforeRemovingController(bool stopRecipe)
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out var gate));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot,
            Lines = [new() { Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit,
                InitialSetpoint = 28, FinalSetpoint = 30, EndAfterSeconds = 60 },
                new() { Variable = SetpointVariable.Oxygen, StartSource = SetpointStartSource.Explicit,
                    InitialSetpoint = 45, OxygenTarget = RampOxygenTarget.ActiveCascadeReference,
                    FinalSetpoint = 50, EndAfterSeconds = 60 }] }, "casc");
        using var writer = new BackgroundFileWriter();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cascade-ramp-close-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (!command.Contains(CommandKeys.TempSetpoint)) return;
            var value = RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint);
            if (value == 28) started.TrySetResult();
            if (value == 25) restoring.TrySetResult();
        };
        var runner = new RecipeRampBlockRunner(engine, arbiter, store, frozen => new(engine, frozen,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)),
            new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5))));
        var ramp = runner.ExecuteAsync("ramp", configuration, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task ending;
        if (stopRecipe) ending = engine.StopAsync("cancel with active ramp");
        else
        {
            gate.Set("operacao", nameof(ManualGateOperation.Pass));
            PushFrame(device, clock, oxygen: 25);
            ending = Task.CompletedTask;
        }
        await restoring.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(ramp.IsCompleted);
        if (stopRecipe) Assert.False(ending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => engine.TrackRampLifecycle("casc"));
        Assert.Empty(System.IO.Directory.GetFiles(root, "terminal.json", System.IO.SearchOption.AllDirectories));
        device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = 25, TempSetpointCommanded = true,
            Temperature = 25, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
        var terminal = await ramp.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RecipeRampTerminalStatus.Cancelled, terminal.Status);
        Assert.True(terminal.HasVerifiedRecovery);
        Assert.NotNull(terminal.Recovery!.Controller);
        Assert.True(store.ReadTerminal(engine.ExecutionId, terminal.InvocationId)!.HasVerifiedRecovery);
        await ending.WaitAsync(TimeSpan.FromSeconds(3));
        if (!stopRecipe) await engine.StopAsync("exit verified");
    }

    [Fact]
    public async Task MixedRampReturnConfirmsSavedControllerAndTemperatureWhileCascadeRemainsPaused()
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot,
            Lines = [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 },
                new() { Variable = SetpointVariable.Oxygen, OxygenTarget = RampOxygenTarget.ActiveCascadeReference,
                    FinalSetpoint = 50, EndAfterSeconds = 60 }] }, "casc");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId, NodeId = "ramp" };
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        var capture = await engine.Resources!.ReserveForAssayAsync(context, resources, TimeSpan.FromSeconds(5));
        var start = engine.CaptureRampStartCheckpoint(configuration, capture.Authority, Guid.NewGuid());
        capture.AbortBeforeAssay();
        using var destination = new RecipeRampFrameDestination(engine, start.Configuration,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        Assert.True(await destination.TryApplyAsync([new(SetpointVariable.Temperature, null, 30, true),
            new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 50, true)], default));
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mixed-ramp-return-" + Guid.NewGuid().ToString("N")), writer);
        await store.PersistStartAsync(start);
        var activeClock = new RecipeRampActiveClock(clock);
        using var producer = new RecipeRampResourceProducer("ramp", resources, activeClock);
        engine.Resources.Register(producer);
        var restoredFrameSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.CommandSent += json =>
        {
            var command = OpenTECCommand.Parse(json);
            if (command.Contains(CommandKeys.TempSetpoint) && RecipeAssayReturnState.Number(command, CommandKeys.TempSetpoint) == 25)
                restoredFrameSent.TrySetResult();
        };
        var returning = engine.Resources.RecoverRampAsync(start, producer, store, destination,
            RecipeRampTerminalStatus.Cancelled, "cancelled", TimeSpan.FromSeconds(5));
        await restoredFrameSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var oxygenReference = new LinearRampSample(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference,
            start.InitialState.ConfirmedStarts[SetpointVariable.Oxygen], true);
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", oxygenReference));
        Assert.Null(store.ReadTerminal(engine.ExecutionId, start.InvocationId));
        Assert.False(returning.IsCompleted);
        device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = 25, TempSetpointCommanded = true,
            Temperature = 25, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
        var terminal = await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(terminal.HasVerifiedRecovery);
        Assert.Equal(start.InitialState.Controller, terminal.Recovery!.Controller);
        Assert.Equal(0, terminal.ActiveSeconds);
        Assert.Equal(2, destination.FinalConfirmations.Length);
        Assert.True(store.ReadTerminal(engine.ExecutionId, start.InvocationId)!.HasVerifiedRecovery);
        Assert.True(producer.StopToken.IsCancellationRequested);
        Assert.NotNull(engine.TryConfirmRampCascadeReference("casc", oxygenReference));
        await engine.StopAsync("mixed restoration verified");
    }

    [Fact]
    public async Task RampFrameComposesTemperatureFeedbackWithTheAssociatedCascadeReference()
    {
        var (engine, device, _, clock) = Build();
        await using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        var barrier = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId },
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        barrier.AbortBeforeAssay();
        var configuration = new RecipeRampBlockConfiguration(new() { Lines = [
            new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 },
            new() { Variable = SetpointVariable.Oxygen, OxygenTarget = RampOxygenTarget.ActiveCascadeReference,
                FinalSetpoint = 42, EndAfterSeconds = 90 }] }, "casc");
        using var destination = new RecipeRampFrameDestination(engine, configuration,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Temperature, null, 30, true),
            new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 42, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(new() { TempControlViaBath = false, TempSetpoint = 30, TempSetpointCommanded = true,
            Temperature = 30, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0, SensorCommOk = true });
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, destination.FinalConfirmations.Length);
        Assert.Equal(RecipeRampConfirmationEvidence.ControllerReference,
            destination.FinalConfirmations.Single(receipt => receipt.Variable == SetpointVariable.Oxygen).Evidence);
        engine.Pause();
        Assert.False(await destination.TryConfirmFinalAsync(target, default));
        Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("mixed cascade frame verified");
    }
    [Fact]
    public async Task RampCascadeConfirmationRequiresMatchingLiveReferenceCurrentOwnershipAndOpenBarrier()
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        var target = new LinearRampSample(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 42, true);
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target));
        Assert.Throws<ArgumentException>(() => engine.TryConfirmRampCascadeReference("casc", target with { AtFinalTarget = false }));
        Assert.Throws<ArgumentException>(() => engine.TryConfirmRampCascadeReference("casc", target with { OxygenTarget = RampOxygenTarget.MonitorReference }));
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        var initialBarrier = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId },
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        initialBarrier.AbortBeforeAssay();
        Assert.True(engine.TrySetCascadeOxygenReference("casc", 42));
        Assert.Null(engine.TryConfirmRampCascadeReference("missing", target));
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target with { Reference = 43 }));
        var commandCount = device.Sent.Count;
        var confirmation = engine.TryConfirmRampCascadeReference("casc", target);
        Assert.NotNull(confirmation);
        Assert.Equal(42, confirmation.Reference);
        Assert.Equal(RecipeRampConfirmationEvidence.ControllerReference, confirmation.Evidence);
        Assert.Equal(commandCount, device.Sent.Count);
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target, Guid.NewGuid()));
        Assert.False(engine.TryApplyRampFrame([target with { Reference = 43 }], "casc", 0, Guid.NewGuid()));
        Assert.NotNull(engine.TryConfirmRampCascadeReference("casc", target));
        var destination = new RecipeRampCascadeDestination(engine, "casc");
        Assert.False(await destination.TryConfirmFinalAsync([target], default));
        Assert.True(await destination.TryApplyAsync([target], default));
        Assert.True(await destination.TryConfirmFinalAsync([target], default));
        Assert.Equal(42, Assert.Single(destination.FinalConfirmations).Reference);
        var lease = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId },
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target));
        Assert.False(await destination.TryConfirmFinalAsync([target], default));
        Assert.Empty(destination.FinalConfirmations);
        lease.AbortBeforeAssay();
        Assert.NotNull(engine.TryConfirmRampCascadeReference("casc", target));
        engine.Pause();
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target));
        engine.Resume();
        Assert.NotNull(engine.TryConfirmRampCascadeReference("casc", target));
        Assert.True(engine.TrySetCascadeOxygenReference("casc", 43));
        Assert.False(await destination.TryConfirmFinalAsync([target], default));
        Assert.Empty(destination.FinalConfirmations);
        Assert.True(await destination.TryApplyAsync([target], default));
        Assert.True(await destination.TryConfirmFinalAsync([target], default));
        arbiter.Claim(CommandOwner.Manual, [ActuatorId.Oxygen], "manual-reference-takeover");
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target));
        Assert.False(await destination.TryApplyAsync([target], default));
        Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("ramp confirmation verified");
        Assert.Null(engine.TryConfirmRampCascadeReference("casc", target));
    }

    [Fact]
    public async Task RampCaptureUsesQuiescentAssociatedControllerInsteadOfOxygenMonitorOrMeasuredOxygen()
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        Assert.True(engine.TrySetCascadeOxygenReference("casc", 42));
        var configuration = new RecipeRampBlockConfiguration(new() { Lines = [new()
        { Variable = SetpointVariable.Oxygen, OxygenTarget = RampOxygenTarget.ActiveCascadeReference,
            FinalSetpoint = 50, EndAfterSeconds = 120 }] }, "casc");
        var lease = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId, NodeId = "ramp" },
            RecipeRampInitialState.ResourcesFor(configuration.Definition), TimeSpan.FromSeconds(5));
        var initial = engine.CaptureRampInitialState(configuration, lease.Authority);
        var reference = Assert.Single(initial.References);
        Assert.Equal(42, reference.Reference);
        Assert.Equal(RampReferenceEvidence.ControllerReference, reference.Evidence);
        Assert.Equal("casc", initial.Controller!.ControllerId);
        Assert.Empty(initial.Commands);
        var trajectory = new LinearSetpointRampTrajectory(configuration.Definition, initial.ConfirmedStarts, (_, value) => value);
        Assert.Equal(42, trajectory.Sample(0)[0].Reference);
        lease.AbortBeforeAssay();
        Assert.Throws<InvalidOperationException>(() => engine.CaptureRampInitialState(configuration, lease.Authority));
        await engine.StopAsync("ramp capture complete");
    }

    [Fact]
    public async Task RampFrameUsesLiveDestinationsAndRefusesWholeMixedFrameDuringAssay()
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        // Controller registration precedes completion of its first wire frame. Quiesce that
        // frame before counting commands from the ramp; later steps require fresh telemetry.
        var initialBarrier = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId },
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        initialBarrier.AbortBeforeAssay();
        ImmutableArray<LinearRampSample> frame = [
            new(SetpointVariable.Temperature, null, 31, false),
            new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 42, false)];
        var monitors = device.Sent.Count(json => OpenTECCommand.Parse(json).Contains(CommandKeys.OxygenMonitor));
        Assert.True(engine.TryApplyRampFrame(frame, "casc", .1));
        Assert.True(engine.TryReadCascadeOxygenReference("casc", out var reference));
        Assert.Equal(42, reference);
        Assert.Equal(monitors, device.Sent.Count(json => OpenTECCommand.Parse(json).Contains(CommandKeys.OxygenMonitor)));
        Assert.Contains(device.Sent, json => double.TryParse(OpenTECCommand.Parse(json).GetRawValue(CommandKeys.TempSetpoint),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value == 31);
        var before = device.Sent.Count;
        Assert.Throws<InvalidOperationException>(() => engine.TryApplyRampFrame([
            new(SetpointVariable.Temperature, null, 32, false), new(SetpointVariable.Flow, null, 2, false)], null, .1));
        Assert.Equal(before, device.Sent.Count);
        var occupied = await arbiter.ReserveAsync(CommandOwner.Recipe, engine.ExecutionId, "other-temperature",
            [ActuatorId.Temperature], TimeSpan.FromSeconds(5));
        Assert.False(engine.TryApplyRampFrame([
            new(SetpointVariable.Temperature, null, 32, false),
            new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, 47, false)], "casc", .1));
        Assert.True(engine.TryReadCascadeOxygenReference("casc", out reference));
        Assert.Equal(42, reference);
        Assert.Equal(before, device.Sent.Count);
        arbiter.ReleaseReservation(occupied);
        var lease = await engine.Resources!.ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId },
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        Assert.False(engine.TryApplyRampFrame(frame, "casc", .1));
        Assert.Equal(before, device.Sent.Count);
        lease.AbortBeforeAssay();
        engine.Pause();
        Assert.False(engine.TryApplyRampFrame(frame, "casc", .1));
        engine.Resume();
        Assert.True(engine.TryApplyRampFrame(frame, "casc", .1));
        arbiter.Claim(CommandOwner.Manual, [ActuatorId.Oxygen], "manual-reference-takeover");
        before = device.Sent.Count;
        Assert.False(engine.TryApplyRampFrame(frame, "casc", .1));
        Assert.Equal(before, device.Sent.Count);
        await engine.StopAsync("ramp frame verified");
        Assert.False(engine.TryApplyRampFrame(frame, "casc", .1));
    }

    [Fact]
    public async Task CascadeReferenceDestinationNeverWritesMonitorAndRejectsSuspendedOrInactiveController()
    {
        var (engine, device, arbiter, clock) = Build();
        await using var run = engine;
        Assert.False(engine.TryReadCascadeOxygenReference("casc", out _));
        Assert.False(engine.TrySetCascadeOxygenReference("casc", 40));
        foreach (var invalid in new[] { -1, 101, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.TrySetCascadeOxygenReference("casc", invalid));
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        Assert.True(engine.TryReadCascadeOxygenReference("casc", out _));
        var monitorCommands = device.Sent.Count(json => OpenTECCommand.Parse(json).Contains(CommandKeys.OxygenMonitor));
        Assert.True(engine.TrySetCascadeOxygenReference("casc", 42));
        Assert.True(engine.TryReadCascadeOxygenReference("casc", out var updated));
        Assert.Equal(42, updated);
        Assert.Equal(monitorCommands, device.Sent.Count(json => OpenTECCommand.Parse(json).Contains(CommandKeys.OxygenMonitor)));
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId };
        var lease = await engine.Resources!.ReserveForAssayAsync(context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5));
        Assert.False(engine.TrySetCascadeOxygenReference("casc", 50));
        Assert.True(engine.TryReadCascadeOxygenReference("casc", out updated));
        Assert.Equal(42, updated);
        lease.AbortBeforeAssay();
        engine.Pause();
        Assert.False(engine.TrySetCascadeOxygenReference("casc", 50));
        engine.Resume();
        Assert.True(engine.TrySetCascadeOxygenReference("casc", 50));
        await engine.StopAsync("reference destination complete");
        Assert.False(engine.TrySetCascadeOxygenReference("casc", 60));
    }

    private static (RecipeEngine Engine, RecordingDeviceService Device, CommandArbiter Arbiter, TestClock Clock) Build()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        var settings = new MemorySettingsService(new AppSettings());
        var engine = new RecipeEngine(arbiter, arbiter, settings, clock, journal: null, delay: CappedDelay);
        return (engine, device, arbiter, clock);
    }

    /// <summary>Real but tiny delays: fast enough for a test, cooperative enough to observe cancellation.</summary>
    private static Task CappedDelay(TimeSpan requested, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(requested.TotalMilliseconds, 0, 5)), ct);

    [Fact]
    public async Task Captured_cascade_state_is_verified_before_physical_return_and_resume()
    {
        var (engine, device, arbiter, clock) = Build(); using var run = engine;
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowmeterLoopEnabled(true));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowRoute(1, 10, GasRoute.Reactor, GasRigConfiguration.Default));
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId };
        var lease = await engine.Resources!.ReserveForAssayAsync(context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5));
        var snapshot = lease.CaptureReturnSnapshot(GasRigConfiguration.Default, clock, device.Latest, clock.GetUtcNow());
        var controller = Assert.Single(snapshot.Controllers);
        Assert.Equal("casc", controller.ControllerId); Assert.True(controller.WasActive);
        Assert.Contains("ErrorWindow", controller.StateJson); Assert.Contains("Allocation", controller.StateJson);
        Assert.Contains("MeasurementHistory", controller.StateJson);
        lease.BeginAssay(snapshot);
        clock.Advance(TimeSpan.FromHours(2));
        PushFrame(device, clock, oxygen: 5);
        Assert.True(lease.ControllersPreserved(snapshot));
        var before = engine.CascadeTermsFor("casc");
        device.Sent.Clear();
        var contract = new KlaRecipeRestorationContract { BeforeAssay = snapshot, MaximumRecoverySeconds = 30,
            StabilitySeconds = 2, AgitationToleranceRpm = 2, FlowToleranceLpm = .05 };
        var recovering = new RecipeAssayRestoration(arbiter, clock).RestoreAsync(lease, contract,
            new() { MaximumTelemetryAgeSeconds = 5 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (device.Sent.Count < 3) await Task.Delay(1, timeout.Token);
        var echo = new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25, Temperature = 30,
            MotorControlViaModbus = true, ServoMotorRouteAck = 1, ServoCommandPending = false,
            HasServoTelemetry = true, HasServoSample = true, ServoOnline = true, ServoRpm = snapshot.AgitationSetpointRpm,
            FlowmeterOnline = true, FlowControlEnabled = true, FlowRate = snapshot.AirflowSetpointLpm,
            FlowSetpoint = snapshot.AirflowSetpointLpm, FlowValve2 = 1, FlowCommandId = 10, FlowCommandAck = 10 };
        device.PushTelemetry(echo);
        while (device.Sent.Count < 5) await Task.Delay(1, timeout.Token);
        for (var i = 0; i < 5; i++) { clock.Advance(TimeSpan.FromSeconds(1)); device.PushTelemetry(echo); await Task.Yield(); }
        var evidence = await recovering.WaitAsync(timeout.Token);
        Assert.Equal(KlaRestorationState.Confirmed, evidence.Restoration);
        Assert.Equal(before, engine.CascadeTermsFor("casc")); Assert.True(lease.ControllersPreserved(snapshot));
        await RecipeAssayRestorationTests.Fixture.ReturnWithStore(lease, contract, clock, new(new() { Restoration = evidence.Restoration }, 40)
            { ReturnSnapshotId = snapshot.SnapshotId });
        Assert.True(lease.HasReturnedSuccessfully);
        await engine.StopAsync("test complete");
    }

    [Fact]
    public async Task Assay_suspension_preserves_cascade_state_and_resumes_without_integrating_the_gap()
    {
        var (engine, device, arbiter, clock) = Build();
        using var run = engine;
        await engine.StartAsync(CascadeWithGateRecipe(out _));
        PushFrame(device, clock, oxygen: 25);
        await WaitForCascadeStartedAsync(engine);
        var template = RecipeExecutionContractTests.Request();
        var context = template.Context with { RecipeRunId = engine.ExecutionId };
        var lease = await engine.Resources!.ReserveForAssayAsync(context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5));
        var before = engine.CascadeTermsFor("casc")!.Value;
        var count = device.Sent.Count;
        var snapshot = template.Restoration.BeforeAssay with
        {
            Actuators = template.Restoration.BeforeAssay.Actuators.Select(a => a with
                { OwnerExecutionId = context.RecipeRunId.ToString() }).Append(new ActuatorReturnSnapshot
                { Actuator = ActuatorId.Oxygen, Owner = CommandOwner.Recipe,
                    OwnerExecutionId = context.RecipeRunId.ToString(), DesiredCommandJson = "{\"oxygenMonitor\":30}",
                    ConfirmationChannel = "transport-only" }).ToImmutableArray(),
        };
        lease.BeginAssay(snapshot);
        clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 5; i++) PushFrame(device, clock, oxygen: 5);
        Assert.False(engine.ApplyLiveTuning(engine.Current!.Node("casc")!));
        Assert.Equal(before, engine.CascadeTermsFor("casc"));
        Assert.Equal(count, device.Sent.Count);

        await lease.ReturnAsync(new(new() { Restoration = KlaRestorationState.Confirmed }, 40)
            { ReturnSnapshotId = snapshot.SnapshotId, PersistenceReceiptId = "simulation-receipt" });
        var rebased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.NodeStateChanged += id => { if (id == "casc") rebased.TrySetResult(); };
        PushFrame(device, clock, oxygen: 25);
        await rebased.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var after = engine.CascadeTermsFor("casc")!.Value;
        Assert.Equal(before.Output, after.Output);
        Assert.Equal(before.Integral, after.Integral);
        Assert.Equal(0, after.Derivative); Assert.Equal(0, after.DeltaOutput);
        Assert.Equal(count, device.Sent.Count); // rebase frame holds restored output
        Assert.Equal(RecipeRunState.Running, engine.State);
        await engine.StopAsync("test complete");
    }

    [Fact]
    public async Task Suspended_cascade_still_observes_buffered_exit_condition_and_cannot_resume_after_end()
    {
        var (engine, device, _, clock) = Build(); using var run = engine;
        await engine.StartAsync(CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40));
        PushFrame(device, clock, oxygen: 25, temperature: 30);
        await WaitForCascadeStartedAsync(engine);
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine.ExecutionId };
        var lease = await engine.Resources!.ReserveForAssayAsync(context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++) PushFrame(device, clock, oxygen: 5, temperature: 30);
        PushFrame(device, clock, oxygen: 5, temperature: 45);
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
        lease.AbortBeforeAssay();
        Assert.Null(engine.CascadeTermsFor("casc"));
    }

    [Fact]
    public async Task Starting_claims_every_actuator_so_manual_control_is_deactivated()
    {
        var (engine, _, arbiter, _) = Build();

        await engine.StartAsync(HoldingRecipe());

        // The claim runs synchronously in StartAsync, before the run task launches.
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));

        // A manual write is now refused — the manual surfaces are inert while the recipe runs.
        var manual = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(500));
        Assert.False(manual.Accepted);

        await engine.StopAsync("fim do teste");
    }

    [Fact]
    public async Task Stopping_returns_every_actuator_to_manual()
    {
        var (engine, _, arbiter, _) = Build();
        await engine.StartAsync(HoldingRecipe());

        await engine.StopAsync("operador parou");

        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
        Assert.Equal(RecipeRunState.Stopped, engine.State);
    }

    [Fact]
    public void Cannot_start_when_disconnected()
    {
        var (engine, device, _, _) = Build();
        device.PushState(ConnectionState.Disconnected);

        Assert.False(engine.CanStart(SetpointRecipe(SetpointVariable.Temperature, 37), out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task Cannot_start_an_invalid_recipe()
    {
        var (engine, _, _, _) = Build();
        var invalid = new RecipeDocument(); // no blocks

        Assert.False(engine.CanStart(invalid, out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(invalid));
    }

    [Fact]
    public async Task Setpoint_block_dispatches_the_wire_frame_under_recipe_ownership()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(device.Sent, s => s.Contains("tempSetpoint") && s.Contains("37"));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Monitor_block_completes_when_telemetry_satisfies_the_condition()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40));
        device.PushTelemetry(new SensorSnapshot { Temperature = 45 });

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Link_loss_safe_aborts_the_running_recipe()
    {
        var (engine, device, _, _) = Build();
        await engine.StartAsync(MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 1000));

        // The arbiter revokes the recipe's ownership on link loss; the engine aborts.
        device.PushState(ConnectionState.Disconnected);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Stopped, engine.State);
        Assert.Contains("aborto seguro", engine.StatusReason ?? "");
    }

    [Fact]
    public async Task Cascade_block_dispatches_a_combined_frame_and_settles()
    {
        var (engine, device, _, clock) = Build();

        await engine.StartAsync(CascadeRecipe());

        // Drive dissolved oxygen at the setpoint so the finite loop settles and the recipe finishes.
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 30 });
            await Task.Delay(10);
        }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The combined cascade frame carries flow, oxygen and motor together.
        Assert.Contains(device.Sent, s => s.Contains("oxygenMonitor") && s.Contains("motorSetpoint") && s.Contains("flowSetpoint"));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Temperature_setpoint_waits_for_the_routed_bath_to_confirm_and_settle()
    {
        var (engine, device, _, clock) = Build();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            TempControlViaBath = true,
            BathOnline = true,
            BathCommEnabled = true,
            BathCommandCompletionPending = true,
            BathCascadeState = "controlling",
            BathState = "running",
            TempSetpoint = 25.0,
            Temperature = 25.0,
        });

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));
        await Task.Delay(20);
        Assert.Equal(RecipeRunState.Running, engine.State);

        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            TempControlViaBath = true,
            BathOnline = true,
            BathCommEnabled = true,
            BathCommandCompletionPending = false,
            BathCascadeState = "controlling",
            BathState = "done",
            TempSetpoint = 37.0,
            Temperature = 37.2,
        });

        // D-3: the reactor has to stay inside the band for the settle time (30 s default).
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);
        clock.Advance(TimeSpan.FromSeconds(31));
        device.PushTelemetry(BathFrame(37.2, 37, pending: false, state: "done"));

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Temperature_block_restarts_the_settle_time_when_the_reactor_leaves_the_band()
    {
        var (engine, device, _, clock) = Build();
        device.PushTelemetry(BathFrame(25, 37, pending: true, state: "running"));
        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));

        device.PushTelemetry(BathFrame(37.1, 37, pending: false, state: "done"));
        await Task.Delay(20);
        clock.Advance(TimeSpan.FromSeconds(20));
        device.PushTelemetry(BathFrame(38.0, 37, pending: false, state: "done"));  // overshoot
        await Task.Delay(20);
        clock.Advance(TimeSpan.FromSeconds(20));
        device.PushTelemetry(BathFrame(37.0, 37, pending: false, state: "done"));
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        clock.Advance(TimeSpan.FromSeconds(31));
        device.PushTelemetry(BathFrame(37.0, 37, pending: false, state: "done"));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Temperature_block_holds_while_tempval_is_invalid()
    {
        var (engine, device, _, clock) = Build();
        device.PushTelemetry(BathFrame(25, 37, pending: false, state: "done"));
        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));

        device.PushTelemetry(BathFrame(37.0, 37, pending: false, state: "done") with { TemperatureValid = false });
        await Task.Delay(20);
        clock.Advance(TimeSpan.FromSeconds(60));
        device.PushTelemetry(BathFrame(37.0, 37, pending: false, state: "done") with { TemperatureValid = false });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);
        await engine.StopAsync("teste");
    }

    [Fact]
    public async Task Routed_bath_wait_does_not_succeed_when_telemetry_or_route_disappears()
    {
        var (engine, device, _, clock) = Build();
        device.PushTelemetry(BathFrame(25, 37, pending: true, state: "running"));

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));
        device.PushTelemetry(new SensorSnapshot { HasBathTelemetry = false, Temperature = 37 });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        device.PushTelemetry(BathFrame(37.1, 37, pending: false, state: "done"));
        await Task.Delay(20);
        clock.Advance(TimeSpan.FromSeconds(31));
        device.PushTelemetry(BathFrame(37.1, 37, pending: false, state: "done"));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Multi_setpoint_waits_for_flow_and_bath_independently()
    {
        var (engine, device, _, clock) = Build();
        device.PushTelemetry(BathFrame(25, 37, pending: true, state: "running"));

        await engine.StartAsync(MultiSetpointRecipe(flow: 2.5, temperature: 37));
        device.PushTelemetry(BathFrame(25, 37, pending: true, state: "running") with
        {
            FlowmeterOnline = true,
            FlowSetpoint = 2.5,
        });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        // The bath hold starts when its waiter processes an in-band observation, after
        // flow confirmation. Advancing 31 seconds after an arbitrary 20 ms sleep races
        // that transition on a busy test host. Drive a continuing, settled sequence.
        var settled = BathFrame(37.1, 37, pending: false, state: "done") with
        {
            FlowmeterOnline = true,
            FlowSetpoint = 2.5,
        };
        for (var second = 0; second < 90 && engine.State == RecipeRunState.Running; second++)
        {
            device.PushTelemetry(settled);
            await Task.Delay(10);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_with_internal_loop_remains_running_after_stabilization()
    {
        var (engine, device, _, clock) = Build();

        await engine.StartAsync(CascadeInfiniteRecipe());
        for (var i = 0; i < 8; i++)
        {
            PushFrame(device, clock, oxygen: 30);
            await Task.Delay(10);
        }

        Assert.Equal(RecipeRunState.Running, engine.State);
        await engine.StopAsync("teste");
        Assert.Equal(RecipeRunState.Stopped, engine.State);
    }

    [Fact]
    public async Task Link_loss_holds_the_recipe_cascade_and_resumes_commands_after_reconnection()
    {
        var (engine, device, arbiter, clock) = Build();
        await engine.StartAsync(CascadeInfiniteRecipe());
        for (var i = 0; i < 3; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }
        var cascadeFrames = device.Sent.Count;
        Assert.True(cascadeFrames > 0);

        // D-065: power loss on the Hub side. The recipe keeps its ownership and waits.
        device.PushState(ConnectionState.Reconnecting);
        await Task.Delay(50);
        Assert.Equal(RecipeRunState.Running, engine.State);
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.NotNull(engine.Waiting);
        Assert.Contains("Enlace", engine.Waiting!.Detail);

        clock.Advance(TimeSpan.FromMinutes(15));
        device.PushState(ConnectionState.Connected);
        Assert.Null(engine.Waiting);
        var afterReturn = device.Sent.Count;
        for (var i = 0; i < 3; i++) { PushFrame(device, clock, oxygen: 22); await Task.Delay(10); }

        Assert.True(device.Sent.Count > afterReturn, "the cascade commands again after the link returns");
        Assert.Equal(RecipeRunState.Running, engine.State);
        await engine.StopAsync("teste");
    }

    [Fact]
    public async Task No_new_block_starts_while_the_link_is_held()
    {
        var (engine, device, arbiter, clock) = Build();
        var recipe = MultiSetpointRecipe(flow: 3, temperature: 30);
        recipe.Nodes.Insert(1, RecipeNode.Create(NodeType.ManualIntervention, id: "gate"));
        recipe.Connections.Clear();
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "gate", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("gate", ConnectorNames.Out, "sp", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("sp", ConnectorNames.Out, "end", ConnectorNames.In));
        device.PushState(ConnectionState.Connected);
        await engine.StartAsync(recipe);
        device.PushState(ConnectionState.Reconnecting);
        recipe.Node("gate")!.Set("operacao", nameof(ManualGateOperation.Pass));
        PushFrame(device, clock, oxygen: 25);
        await Task.Delay(100);

        Assert.Equal(NodeState.Waiting, engine.NodeStateOf("sp"));
        Assert.Equal(RecipeRunState.Running, engine.State);
        await engine.StopAsync("teste");
    }

    [Fact]
    public async Task Cascade_loop_exits_when_its_saida_loop_manual_gate_passes()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithGateRecipe(out var gate);

        await engine.StartAsync(recipe);
        // A couple of frames while the gate holds Continuar Cascata (Bloquear) — the loop runs.
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);

        // Operator flips the gate to Pular Cascata (Passar): the loop must end and the recipe finish.
        gate.Set("operacao", nameof(ManualGateOperation.Pass));
        PushFrame(device, clock, oxygen: 25);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_gate_still_wins_when_a_self_loop_edge_is_drawn_first()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithGateRecipe(out var gate);

        // A hand-drawn Saída Loop -> Entrada Loop self-loop, listed BEFORE the gate's edge. Picking
        // the first loop edge would read the cascade itself as the condition and ignore the gate.
        recipe.Connections.Insert(0, new RecipeConnection("casc", ConnectorNames.LoopOut, "casc", ConnectorNames.LoopIn));

        await engine.StartAsync(recipe);
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);

        gate.Set("operacao", nameof(ManualGateOperation.Pass));
        PushFrame(device, clock, oxygen: 25);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_loop_exits_when_its_saida_loop_timer_elapses()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithGateRecipe(out var gate);

        // Swap the manual gate for a 30 s Temporizador: the loop must run, then leave on time.
        recipe.Nodes.Remove(gate);
        var timer = RecipeNode.Create(NodeType.Timer, id: "gate");
        timer.Set("duracao", 30.0);
        timer.Set("unidade", nameof(TimeUnit.Seconds));
        recipe.Nodes.Add(timer);

        await engine.StartAsync(recipe);
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);

        await WaitForCascadeStartedAsync(engine);
        // Past the 30 s duration. Several frames, so the loop is certain to observe one even if the
        // engine has not re-entered its frame wait when the first is pushed.
        clock.Advance(TimeSpan.FromSeconds(40));
        for (var i = 0; i < 4; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_loop_exits_when_its_saida_loop_monitor_condition_is_met()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);

        await engine.StartAsync(recipe);
        // Oxygen valid so the cascade runs; temperature below 40 keeps the loop going.
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10); }
        // Temperature reaches the exit condition.
        PushFrame(device, clock, oxygen: 25, temperature: 45);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_observes_the_latest_exit_frame_even_when_no_further_frame_arrives()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        await engine.StartAsync(recipe);
        // No scheduling delays and no follow-up frame: the final observation must not be lost.
        for (var i = 0; i < 20; i++) PushFrame(device, clock, oxygen: 25, temperature: 30);
        PushFrame(device, clock, oxygen: 25, temperature: 45);
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_monitor_condition_honours_the_consecutive_confirmations()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        recipe.Node("mon")!.Set("confirmacoes", 3);

        await engine.StartAsync(recipe);
        // One frame over the target, then one under: the run resets, so the loop must NOT exit.
        PushFrame(device, clock, oxygen: 25, temperature: 45); await Task.Delay(10);
        PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10);
        PushFrame(device, clock, oxygen: 25, temperature: 45); await Task.Delay(10);
        PushFrame(device, clock, oxygen: 25, temperature: 45); await Task.Delay(10);
        Assert.Equal(RecipeRunState.Running, engine.State);

        // The third consecutive frame over the target completes the debounce.
        for (var i = 0; i < 3; i++) { PushFrame(device, clock, oxygen: 25, temperature: 45); await Task.Delay(10); }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_monitor_condition_gives_up_at_its_timeout()
    {
        var (engine, device, _, clock) = Build();
        // A target the telemetry never reaches; without the timeout the loop would run forever.
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 400);
        recipe.Node("mon")!.Set("tempoLimiteMs", 30_000);

        await engine.StartAsync(recipe);
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);
        await WaitForCascadeStartedAsync(engine);
        clock.Advance(TimeSpan.FromSeconds(40));
        for (var i = 0; i < 4; i++) { PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10); }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    private static async Task WaitForCascadeStartedAsync(RecipeEngine engine)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.CascadeTermsFor("casc") is null)
        {
            Assert.False(engine.Completion.IsCompleted, $"Cascade ended before initialization: {engine.State} — {engine.StatusReason}");
            await Task.Delay(10, timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cascade_exit_deadline_does_not_depend_on_another_telemetry_frame(bool timerExit)
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 400);
        if (timerExit)
        {
            recipe.Nodes.Remove(recipe.Node("mon")!);
            var timer = RecipeNode.Create(NodeType.Timer, id: "mon");
            timer.Set("duracao", 30.0); timer.Set("unidade", nameof(TimeUnit.Seconds));
            recipe.Nodes.Add(timer);
        }
        else recipe.Node("mon")!.Set("tempoLimiteMs", 30_000);
        await engine.StartAsync(recipe);
        PushFrame(device, clock, oxygen: 25, temperature: 30);
        await WaitForCascadeStartedAsync(engine);
        clock.Advance(TimeSpan.FromSeconds(40));
        // Deliberately send no more telemetry. Deadline handling is independent of sensor cadence.
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_monitor_on_another_variable_exits_even_while_the_oxygen_probe_is_silent()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);

        await engine.StartAsync(recipe);
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);

        // O₂ goes silent while temperature reaches the target. The condition does not depend on O₂,
        // so the cascade's own oxygen guard must not keep the loop from leaving.
        for (var i = 0; i < 4; i++)
        {
            PushFrame(device, clock, oxygen: SensorReadings.NotReceived, temperature: 45);
            await Task.Delay(10);
        }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_executes_in_agitation_only_mode()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        var cascadeNode = recipe.Nodes.First(n => n.Type == NodeType.CascadeControl);
        cascadeNode.Set("modo", nameof(OpenTECHub.Services.Control.CascadeMode.AgitationOnly));

        await engine.StartAsync(recipe);
        await DriveCascadeUntilCompletion(engine, device, clock);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_executes_in_aeration_only_mode()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        var cascadeNode = recipe.Nodes.First(n => n.Type == NodeType.CascadeControl);
        cascadeNode.Set("modo", nameof(OpenTECHub.Services.Control.CascadeMode.AerationOnly));

        await engine.StartAsync(recipe);
        await DriveCascadeUntilCompletion(engine, device, clock);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    private static async Task DriveCascadeUntilCompletion(
        RecipeEngine engine,
        RecordingDeviceService device,
        TestClock clock)
    {
        // Keep publishing the satisfied monitor state until the async cascade loop has
        // consumed it. A one-shot frame can arrive before the loop subscribes on a busy CI host.
        for (var i = 0; i < 50 && !engine.Completion.IsCompleted; i++)
        {
            PushFrame(device, clock, oxygen: 25, temperature: i < 2 ? 30 : 45);
            await Task.Delay(10);
        }
    }

    private static void PushFrame(RecordingDeviceService device, TestClock clock, double oxygen, double temperature = 0)
    {
        clock.Advance(TimeSpan.FromSeconds(3));
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygen, Temperature = temperature });
    }

    // ── External devices: the flowmeter (air flow) ─────────────────────────────

    [Fact]
    public async Task Flow_setpoint_holds_the_recipe_until_the_flowmeter_echoes_it_back()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint") && s.Contains("2.5"))));

        // An echo from a flowmeter that is not online proves nothing: the block keeps holding.
        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false, FlowSetpoint = 2.5 });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowSetpoint = 2.5 });
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task A_silent_flowmeter_is_reported_as_a_wait_and_the_operator_can_skip_the_block()
    {
        var (engine, device, _, clock) = Build();
        var log = new List<RecipeLogEntry>();
        engine.Logged += entry => { lock (log) { log.Add(entry); } };

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint"))));

        // Past the grace with no usable frame: the engine declares the hold rather than moving on.
        Assert.True(await Eventually(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
            return engine.Waiting is not null;
        }));
        Assert.Equal("Fluxômetro", engine.Waiting!.Device);
        Assert.Equal("sp", engine.Waiting.NodeId);
        Assert.Equal(RecipeRunState.Running, engine.State);

        engine.SkipWait();
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
        lock (log)
        {
            Assert.Contains(log, e => e.Severity == RecipeLogSeverity.Warning && e.Message.Contains("pulado"));
        }
    }

    [Fact]
    public async Task The_operator_can_stop_the_recipe_while_it_holds_for_the_flowmeter()
    {
        var (engine, device, arbiter, clock) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint"))));
        Assert.True(await Eventually(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
            return engine.Waiting is not null;
        }));

        await engine.StopAsync("parada pelo operador");

        Assert.Equal(RecipeRunState.Stopped, engine.State);
        Assert.Null(engine.Waiting);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
    }

    [Fact]
    public async Task Zeroing_the_flow_never_holds_because_a_stop_must_not_wait_on_the_device()
    {
        var (engine, _, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 0));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
    }

    [Fact]
    public async Task Enabling_the_aeration_loop_writes_the_hub_flag_and_holds_until_it_is_echoed()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(LoopRecipe(ControlLoop.Aeration, LoopOperation.Enable));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowmeterComm"))));

        // The Hub has not acknowledged the loop yet, so the block is still holding.
        device.PushTelemetry(new SensorSnapshot { FlowControlEnabled = false });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        device.PushTelemetry(new SensorSnapshot { FlowControlEnabled = true });
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Disabling_the_aeration_loop_safe_stops_and_clears_the_flag_without_holding()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(LoopRecipe(ControlLoop.Aeration, LoopOperation.Disable));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
        Assert.Contains(device.Sent, s => s.Contains("flowmeterComm") && s.Contains("flowSetpoint"));
    }

    /// <summary>Polls a condition the running engine reaches on its own loop, with a hard cap.</summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    // ── Recipe fixtures ────────────────────────────────────────────────────────

    /// <summary>Start → Monitor (never satisfied) → Fim: holds until stopped or aborted.</summary>
    private static RecipeDocument HoldingRecipe()
        => MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 100000);

    private static RecipeDocument MonitorRecipe(MeasuredVariable variable, ComparisonOperator op, double target)
    {
        var recipe = new RecipeDocument { Name = "Monitor" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", variable.ToString());
        monitor.Set("condicao", op.ToString());
        monitor.Set("valorAlvo", target);
        monitor.Set("confirmacoes", 1);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, monitor, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "mon", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("mon", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument LoopRecipe(ControlLoop loop, LoopOperation operation)
    {
        var recipe = new RecipeDocument { Name = "Malha" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var setLoop = RecipeNode.Create(NodeType.SetLoop, id: "loop");
        setLoop.Set("malha", loop.ToString());
        setLoop.Set("operacao", operation.ToString());
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, setLoop, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "loop", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("loop", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument SetpointRecipe(SetpointVariable variable, double value)
    {
        var recipe = new RecipeDocument { Name = "Setpoint" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var setpoint = RecipeNode.Create(NodeType.SetSetpoint, id: "sp");
        setpoint.Set("variavel", variable.ToString());
        setpoint.Set("valor", value);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, setpoint, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "sp", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("sp", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument CascadeRecipe()
    {
        var recipe = new RecipeDocument { Name = "Cascata" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("spO2", 30.0);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument MultiSetpointRecipe(double flow, double temperature)
    {
        var recipe = new RecipeDocument { Name = "Múltiplos setpoints" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var setpoint = RecipeNode.Create(NodeType.MultiSetpoint, id: "sp");
        setpoint.Set("pontos", new JsonArray
        {
            new JsonObject { ["variavel"] = nameof(SetpointVariable.Flow), ["valor"] = flow, ["histerese"] = 0.0 },
            new JsonObject { ["variavel"] = nameof(SetpointVariable.Temperature), ["valor"] = temperature, ["histerese"] = 0.0 },
        });
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, setpoint, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "sp", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("sp", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static SensorSnapshot BathFrame(double reactorTemperature, double target, bool pending, string state)
        => new()
        {
            HasBathTelemetry = true,
            TempControlViaBath = true,
            BathOnline = true,
            BathCommEnabled = true,
            BathCommandCompletionPending = pending,
            BathCascadeState = "controlling",
            BathState = state,
            TempSetpoint = target,
            Temperature = reactorTemperature,
        };

    private static RecipeDocument CascadeInfiniteRecipe()
    {
        var recipe = CascadeRecipe();
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "casc", ConnectorNames.LoopIn));
        return recipe;
    }

    /// <summary>Start → Cascade → End, with the cascade's Saída Loop wired to a manual gate and back.</summary>
    private static RecipeDocument CascadeWithGateRecipe(out RecipeNode gate)
    {
        var recipe = new RecipeDocument { Name = "Cascata com portão" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        gate = RecipeNode.Create(NodeType.ManualIntervention, id: "gate"); // default Hold = Continuar Cascata
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, gate, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    /// <summary>Start → Cascade → End, with the cascade's Saída Loop wired to a monitor exit condition.</summary>
    private static RecipeDocument CascadeWithMonitorRecipe(MeasuredVariable variable, ComparisonOperator op, double target)
    {
        var recipe = new RecipeDocument { Name = "Cascata com condição" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", variable.ToString());
        monitor.Set("condicao", op.ToString());
        monitor.Set("valorAlvo", target);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, monitor, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "mon", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("mon", ConnectorNames.Out, "casc", ConnectorNames.LoopIn));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }
}
