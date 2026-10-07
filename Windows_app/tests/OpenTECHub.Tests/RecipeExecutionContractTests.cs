using System.Collections.Immutable;
using System.Text.Json.Nodes;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeExecutionContractTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple)]
    public void Requests_round_trip_without_losing_return_state(KlaAssayProtocol protocol, KlaCaptureMode mode)
    {
        var request = Request(protocol, mode);
        var json = RecipeContractSerializer.Serialize(request);
        var copy = RecipeContractSerializer.ReadKlaRequest(json);
        Assert.Equal(json, RecipeContractSerializer.Serialize(copy));
        Assert.Equal(request.Context, copy.Context);
        Assert.Equal(300, copy.Restoration.BeforeAssay.AgitationSetpointRpm);
        Assert.Equal(CommandOwner.Recipe, copy.Restoration.BeforeAssay.Actuators[0].Owner);
        Assert.Equal(request.Restoration.BeforeAssay.Controllers[0], copy.Restoration.BeforeAssay.Controllers[0]);
        Assert.Equal(RecipeContractSerializer.Fingerprint(request), RecipeContractSerializer.Fingerprint(copy));
    }

    [Fact]
    public void Parallel_schedule_runs_first_at_two_hours_then_every_four_hours_without_catch_up()
    {
        var schedule = new PeriodicBlockSchedule { InitialDelaySeconds = 2 * 3600,
            PeriodSeconds = 4 * 3600 };
        Assert.Equal(7200, schedule.DueAfterSeconds(0));
        Assert.Equal(21600, schedule.DueAfterSeconds(1));
        Assert.Equal(36000, schedule.DueAfterSeconds(2));
        Assert.Equal(0, schedule.FirstFutureSlot(7199));
        Assert.Equal(1, schedule.FirstFutureSlot(7200));
        Assert.Equal(2, schedule.FirstFutureSlot(21900));
        Assert.Equal(3, schedule.FirstFutureSlot(50000));

        var request = Request() with { PeriodicInvocation = new()
        { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic", TargetNodeId = "kla",
            CoordinatedCascadeNodeId = "oxygen-cascade", SlotIndex = 1, Schedule = schedule } };
        var copy = RecipeContractSerializer.ReadKlaRequest(RecipeContractSerializer.Serialize(request));
        Assert.Equal(21600, copy.PeriodicInvocation!.Schedule.DueAfterSeconds(copy.PeriodicInvocation.SlotIndex));
        var result = Result(request, Attempt(request));
        RecipeContractSerializer.ReadKlaResult(RecipeContractSerializer.Serialize(result)).ValidateAgainst(copy);
    }

    [Fact]
    public void Parallel_schedule_rejects_invalid_intervals_and_result_with_different_slot()
    {
        var schedule = new PeriodicBlockSchedule { InitialDelaySeconds = 7200, PeriodSeconds = 14400 };
        Assert.Throws<ArgumentException>(() => (schedule with { PeriodSeconds = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (schedule with { InitialDelaySeconds = double.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.DueAfterSeconds(-1));
        var request = Request() with { PeriodicInvocation = new()
        { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic", TargetNodeId = "kla",
            CoordinatedCascadeNodeId = "oxygen-cascade", SlotIndex = 0, Schedule = schedule } };
        var result = Result(request, Attempt(request));
        Assert.Throws<ArgumentException>(() => (result with { PeriodicInvocation = result.PeriodicInvocation! with { SlotIndex = 1 } })
            .ValidateAgainst(request));
        Assert.Throws<ArgumentException>(() => (request with { PeriodicInvocation = request.PeriodicInvocation! with
            { TargetNodeId = "another-block" } }).Validate());
        new PeriodicBlockInvocation { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic",
            TargetNodeId = "logging", SlotIndex = 0, Schedule = schedule }.Validate();
    }

    [Fact]
    public void Cascade_handoff_requires_pause_ack_before_assay_and_restore_ack_before_resume()
    {
        var now = DateTimeOffset.UtcNow;
        var handoff = new RecipeActuatorHandoff { HandoffId = Guid.NewGuid(),
            ControllerNodeId = "oxygen-cascade", TargetNodeId = "kla",
            ReturnSnapshotId = Guid.NewGuid(), Resources = [ActuatorId.Agitation, ActuatorId.Aeration],
            Phase = RecipeActuatorHandoffPhase.Requested, Revision = 0,
            UpdatedUtc = now, AcknowledgedBy = "periodic-coordinator" };
        Assert.Throws<InvalidOperationException>(() => handoff.Advance(
            RecipeActuatorHandoffPhase.AssayActive, now, "kla"));
        handoff = handoff.Advance(RecipeActuatorHandoffPhase.CascadeQuiesced, now, "oxygen-cascade");
        handoff = handoff.Advance(RecipeActuatorHandoffPhase.AssayActive, now, "kla");
        Assert.Throws<InvalidOperationException>(() => handoff.Advance(
            RecipeActuatorHandoffPhase.CascadeResumed, now, "oxygen-cascade"));
        handoff = handoff.Advance(RecipeActuatorHandoffPhase.Restoring, now, "coordinator");
        handoff = handoff.Advance(RecipeActuatorHandoffPhase.CascadeResumed, now, "oxygen-cascade");
        Assert.Equal(4, handoff.Revision);
        Assert.Throws<InvalidOperationException>(() => handoff.Advance(
            RecipeActuatorHandoffPhase.AssayActive, now, "kla"));
    }

    [Fact]
    public void Failed_or_emergency_handoff_cannot_resume_late()
    {
        var now = DateTimeOffset.UtcNow;
        var handoff = new RecipeActuatorHandoff { HandoffId = Guid.NewGuid(),
            ControllerNodeId = "oxygen-cascade", TargetNodeId = "kla",
            ReturnSnapshotId = Guid.NewGuid(), Resources = [ActuatorId.Agitation, ActuatorId.Aeration],
            Phase = RecipeActuatorHandoffPhase.AssayActive, Revision = 2,
            UpdatedUtc = now, AcknowledgedBy = "kla" };
        Assert.Throws<ArgumentException>(() => handoff.Advance(
            RecipeActuatorHandoffPhase.RestorationFailed, now, "coordinator"));
        var failed = handoff.Advance(RecipeActuatorHandoffPhase.RestorationFailed, now,
            "coordinator", "flow confirmation missing");
        Assert.Throws<InvalidOperationException>(() => failed.Advance(
            RecipeActuatorHandoffPhase.CascadeResumed, now, "oxygen-cascade"));
        var emergency = handoff.Advance(RecipeActuatorHandoffPhase.EmergencyStopped, now,
            "safety", "operator emergency stop");
        Assert.Throws<InvalidOperationException>(() => emergency.Advance(
            RecipeActuatorHandoffPhase.CascadeResumed, now, "oxygen-cascade"));
    }

    [Fact]
    public void Snapshot_detaches_settings_and_identity_detects_changed_payload()
    {
        var request = Request();
        var frozen = RecipeContractSerializer.Snapshot(request);
        Assert.NotSame(request.Definition.Settings, frozen.Definition.Settings);
        Assert.NotSame(request.Definition.ProtocolSettings, frozen.Definition.ProtocolSettings);
        Assert.NotEqual(RecipeContractSerializer.Fingerprint(request),
            RecipeContractSerializer.Fingerprint(request with { FailurePolicy = KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration }));
    }

    [Theory]
    [InlineData("schemaVersion", "99")]
    [InlineData("kind", "\"unknown\"")]
    public void Unknown_envelope_is_rejected(string property, string value)
    {
        var root = JsonNode.Parse(RecipeContractSerializer.Serialize(Request()))!.AsObject();
        root[property] = JsonNode.Parse(value);
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(root.ToJsonString()));
    }

    [Fact]
    public void Unknown_fields_enum_names_integers_and_duplicates_are_rejected()
    {
        var json = RecipeContractSerializer.Serialize(Request());
        var root = JsonNode.Parse(json)!.AsObject();
        root["payload"]!["retry"]!["unrecognizedBudget"] = 1;
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(root.ToJsonString()));
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(json.Replace("\"Abiotic\"", "\"Alien\"")));
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(json.Replace("\"Abiotic\"", "0")));
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")));
        root = JsonNode.Parse(json)!.AsObject();
        root["payload"]!.AsObject().Remove("restoration");
        Assert.Throws<RecipeFormatException>(() => RecipeContractSerializer.ReadKlaRequest(root.ToJsonString()));
    }

    [Fact]
    public void Return_target_cannot_override_pre_assay_state()
    {
        var request = Request();
        var changed = request with { Definition = request.Definition with
        { ProtocolSettings = request.Definition.ProtocolSettings with { ReturnAgitationRpm = 100 } } };
        Assert.Throws<ArgumentException>(changed.Validate);
        Assert.Throws<ArgumentException>(() => (request with { Restoration = request.Restoration with
        { BeforeAssay = request.Restoration.BeforeAssay with { Actuators = [] } } }).Validate());
    }

    [Fact]
    public void Contextual_protection_and_quality_are_validated()
    {
        var abiotic = Request();
        Assert.Throws<ArgumentException>(() => (abiotic with { Quality = abiotic.Quality with { RequireValidOur = true } }).Validate());
        var biotic = Request(KlaAssayProtocol.Biotic);
        Assert.Throws<ArgumentException>(() => (biotic with { Definition = biotic.Definition with
        { ProtocolSettings = biotic.Definition.ProtocolSettings with { AerationReturn = new() } } }).Validate());
        Assert.Throws<ArgumentException>(() => (biotic with { Retry = biotic.Retry with { MinimumInterAssaySeconds = 0 } }).Validate());
        Assert.Throws<ArgumentException>(() => (biotic with { Retry = biotic.Retry with { MaximumGasOffSecondsPerAttempt = double.NaN } }).Validate());
    }

    [Fact]
    public void Single_capture_does_not_turn_retries_into_planned_replicates()
    {
        var request = Request();
        Assert.Equal(2, request.Retry.MaximumAttemptsPerReplicate);
        Assert.Single(request.Definition.Conditions);
        Assert.Equal(1, request.Definition.Conditions[0].RequestedReplicates);
        Assert.Throws<ArgumentException>(() => (request with { Definition = request.Definition with
        { Conditions = [request.Definition.Conditions[0] with { RequestedReplicates = 2 }] } }).Validate());
    }

    [Fact]
    public void Result_preserves_failed_attempt_and_automatic_authorship()
    {
        var request = Request();
        var accepted = Attempt(request);
        var failed = accepted with { AttemptId = Guid.NewGuid(), AttemptNumber = 1,
            KlaPerHour = null, KlaQuality = KlaScientificQuality.Inconclusive,
            Decision = KlaAutomaticDecision.NotSelected, ReasonCodes = ["insufficient_window"] };
        var result = Result(request, failed, accepted with { AttemptNumber = 2 });
        var copy = RecipeContractSerializer.ReadKlaResult(RecipeContractSerializer.Serialize(result));
        Assert.Null(copy.Attempts[0].KlaPerHour);
        Assert.Equal(RecipeDecisionAuthor.AutomaticPolicy, copy.Attempts[1].DecisionAuthor);
        Assert.Equal(2, copy.Attempts.Length);
    }

    [Fact]
    public void Success_or_retry_requires_restoration_and_durable_saving()
    {
        var request = Request();
        var attempt = Attempt(request);
        Assert.Throws<ArgumentException>(() => (attempt with { Restoration = KlaRestorationState.Failed }).Validate());
        Assert.Throws<ArgumentException>(() => (attempt with { Decision = KlaAutomaticDecision.Retry, PersistenceConfirmed = false }).Validate());
        Assert.Throws<ArgumentException>(() => (Result(request, attempt) with { PreAssayStateRestored = false }).Validate());
        Assert.Throws<ArgumentException>(() => Result(request, attempt, attempt).Validate());
        Assert.Throws<ArgumentException>(() => Result(request, attempt, attempt with { AttemptId = Guid.NewGuid(), AttemptNumber = 2 }).Validate());
    }

    [Fact]
    public void Completion_is_checked_against_snapshot_identity_and_replica_coverage()
    {
        var request = Request();
        var result = Result(request, Attempt(request));
        result.ValidateAgainst(request);
        Assert.Throws<ArgumentException>(() => (result with { Attempts =
            [result.Attempts[0] with { ReturnSnapshotId = Guid.NewGuid() }] }).ValidateAgainst(request));
        Assert.Throws<ArgumentException>(() => (result with { Attempts =
            [result.Attempts[0] with { AttemptNumber = 2 }] }).ValidateAgainst(request));
        var multiple = Request(mode: KlaCaptureMode.Multiple);
        Assert.Throws<ArgumentException>(() => Result(multiple, Attempt(multiple)).ValidateAgainst(multiple));
    }

    [Fact]
    public void Conditional_selection_requires_explicit_policy_and_our_requirement_is_independent()
    {
        var request = Request(KlaAssayProtocol.Biotic);
        var attempt = Attempt(request) with { KlaQuality = KlaScientificQuality.Conditional, ReasonCodes = ["probe_response_unknown"] };
        var result = Result(request, attempt) with { Status = KlaRecipeTerminalStatus.CompletedWithWarnings };
        Assert.Throws<ArgumentException>(() => result.ValidateAgainst(request));
        var allowed = request with { Quality = request.Quality with { AllowedConditionalReasonCodes = ["probe_response_unknown"] } };
        result.ValidateAgainst(allowed);
        Assert.Throws<ArgumentException>(() => result.ValidateAgainst(allowed with
            { Quality = allowed.Quality with { RequireValidOur = true } }));
    }

    [Fact]
    public void Legacy_auto_accept_and_invalid_acquisition_bounds_cannot_bypass_recipe_policy()
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => (request with { Definition = request.Definition with
            { Settings = request.Definition.Settings with { AutoAcceptRuns = true } } }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { Definition = request.Definition with
            { Settings = request.Definition.Settings with { DOMaxPercent = double.NaN } } }).Validate());
    }

    [Fact]
    public void Ramp_round_trip_preserves_independent_relative_end_times_and_units()
    {
        var definition = new LinearSetpointRampDefinition { Lines =
        [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 37, EndAfterSeconds = 3600 },
         new() { Variable = SetpointVariable.Agitation, StartSource = SetpointStartSource.Explicit,
             InitialSetpoint = 200, FinalSetpoint = 500, EndAfterSeconds = 1200 }] };
        var copy = RecipeContractSerializer.ReadRamp(RecipeContractSerializer.Serialize(definition));
        Assert.Equal(3600, copy.Lines[0].EndAfterSeconds);
        Assert.Equal(1200, copy.Lines[1].EndAfterSeconds);
        Assert.Equal("°C", copy.Lines[0].SetpointUnit);
        Assert.Equal("rpm", copy.Lines[1].SetpointUnit);
        Assert.Null(copy.Lines[0].InitialSetpoint);
        Assert.Equal(definition.Lines[1], copy.Lines[1]);
    }

    [Fact]
    public void Ramp_rejects_ambiguous_context_duplicate_variables_and_invalid_numbers()
    {
        var line = new LinearSetpointRampLine { Variable = SetpointVariable.Oxygen, FinalSetpoint = 30, EndAfterSeconds = 60 };
        Assert.Throws<ArgumentException>(line.Validate);
        line = line with { OxygenTarget = RampOxygenTarget.ActiveCascadeReference };
        line.Validate();
        Assert.Throws<ArgumentException>(() => (line with { EndAfterSeconds = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (line with { EndAfterSeconds = double.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentException>(() => (line with { InitialSetpoint = 20 }).Validate());
        Assert.Throws<ArgumentException>(() => (line with { Variable = SetpointVariable.Ph }).Validate());
        Assert.Throws<ArgumentException>(() => new LinearSetpointRampDefinition { Lines = [line, line] }.Validate());
    }

    [Fact]
    public void Ramp_cannot_cross_off_sentinel_into_invalid_device_band()
    {
        var line = new LinearSetpointRampLine { Variable = SetpointVariable.Agitation,
            StartSource = SetpointStartSource.Explicit, InitialSetpoint = 0, FinalSetpoint = 300, EndAfterSeconds = 60 };
        Assert.Throws<ArgumentException>(line.Validate);
        var captured = line with { StartSource = SetpointStartSource.CurrentConfirmed, InitialSetpoint = null };
        captured.Validate();
        Assert.Throws<ArgumentException>(() => captured.ValidateResolvedStart(0));
        captured.ValidateResolvedStart(100);
        var flow = captured with { Variable = SetpointVariable.Flow, FinalSetpoint = 2 };
        flow.ValidateResolvedStart(0);
    }

    [Fact]
    public void Existing_recipe_v1_does_not_require_execution_contract_or_change_on_round_trip()
    {
        const string json = """
        {"schemaVersion":1,"name":"Legada","createdUtc":"2026-10-01T00:00:00Z","modifiedUtc":"2026-10-01T00:00:00Z",
         "nodes":[{"id":"start","type":"Start"},{"id":"sp","type":"WriteSetpoint","parameters":{"variavel":"Temperature","valor":30}},
                  {"id":"end","type":"End"}],
         "connections":[{"source":"start","target":"sp"},{"source":"sp","target":"end"}]}
        """;
        var recipe = RecipeSerializer.Deserialize(json);
        var copy = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe));
        Assert.Equal(1, RecipeSerializer.CurrentVersion);
        Assert.Equal(NodeType.SetSetpoint, copy.Node("sp")!.Type);
        Assert.Equal(30, copy.Node("sp")!.Number("valor"));
        Assert.Equal(3, copy.Nodes.Count);
        Assert.Equal(2, copy.Connections.Count);
    }

    [Theory]
    [InlineData("KlaAssay")]
    [InlineData("LinearSetpointRamp")]
    [InlineData("ForeignBlock")]
    public void Unsupported_recipe_blocks_are_rejected_instead_of_silently_removed(string type)
    {
        var json = "{\"schemaVersion\":1,\"nodes\":[{\"id\":\"future\",\"type\":\"" + type + "\"}]}";
        Assert.Throws<RecipeFormatException>(() => RecipeSerializer.Deserialize(json));
    }

    [Fact]
    public void Aggregate_verifies_each_recaptured_pulse_without_allowing_matrix_policy_changes()
    {
        var request = Request();
        request = request with { Definition = request.Definition with { Settings = request.Definition.Settings with
            { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 5 } } };
        var snapshot = request.Restoration.BeforeAssay with { SnapshotId = Guid.NewGuid(),
            CapturedUtc = request.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1) };
        var invocation = request with { Restoration = request.Restoration with { BeforeAssay = snapshot },
            AcquisitionDeadlineUtc = request.AcquisitionDeadlineUtc.AddSeconds(-1) };
        var pulse = KlaRecipePulseMapper.Create(invocation, "test", request.Definition.Conditions[0].ConditionId,
            1, 1, snapshot.CapturedUtc.AddSeconds(1));
        var attempt = Attempt(request) with { AttemptId = pulse.RequestId, ReturnSnapshotId = snapshot.SnapshotId };
        var result = Result(request, attempt) with { Pulses = [pulse] };
        result.ValidateAgainst(request);
        RecipeContractSerializer.ReadKlaResult(RecipeContractSerializer.Serialize(result)).ValidateAgainst(request);
        Assert.Throws<ArgumentException>(() => (result with { Attempts = [attempt with
            { ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId }] }).ValidateAgainst(request));
        var changed = KlaRecipePulseMapper.Create(invocation with { Quality = invocation.Quality with { Version = "other" } },
            "test", request.Definition.Conditions[0].ConditionId, 1, 1, snapshot.CapturedUtc.AddSeconds(1));
        Assert.Throws<ArgumentException>(() => (result with { Pulses = [changed] }).ValidateAgainst(request));
        Assert.Throws<ArgumentNullException>(() => (result with { Pulses = [null!] }).Validate());
    }

    internal static KlaRecipeRequest Request(KlaAssayProtocol protocol = KlaAssayProtocol.Abiotic,
        KlaCaptureMode mode = KlaCaptureMode.Single)
    {
        var context = new RecipeInvocationContext { RecipeRunId = Guid.NewGuid(), InvocationId = Guid.NewGuid(),
            NodeId = "kla", CultivationId = "culture-1", RecipeSha256 = new string('a', 64) };
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Context = context,
            Definition = new() { Protocol = protocol, CaptureMode = mode,
                Conditions = mode == KlaCaptureMode.Single ? [new(Guid.NewGuid(), 0, 300, 2, 1)] :
                    [new(Guid.NewGuid(), 0, 300, 2, 2), new(Guid.NewGuid(), 1, 400, 3, 1)],
                ProtocolSettings = new() { AerationReturn = new() { MinimumDoPercent = 20,
                    MaximumDoDropPoints = 10, MaximumGasOffSeconds = 60, MaximumRecoverySeconds = 120,
                    MinimumInterAssaySeconds = 30 } } },
            Quality = new() { ProfileId = "qualified-test-profile", Version = "1" },
            Retry = new() { MaximumAttemptsPerReplicate = 2, MaximumAttemptsPerCultivation = 10,
                MinimumInterAssaySeconds = 30, MaximumBlockSeconds = 600, MaximumGasOffSecondsPerAttempt = 60,
                MaximumCumulativeGasOffSecondsPerCultivation = 300, RecoverableReasons = [KlaRetryReason.InsufficientWindow] },
            AcquisitionDeadlineUtc = now.AddMinutes(10),
            Restoration = new() { MaximumRecoverySeconds = 120, AgitationToleranceRpm = 10,
                FlowToleranceLpm = 0.1, StabilitySeconds = 10,
                BeforeAssay = new() { SnapshotId = Guid.NewGuid(), CapturedUtc = now,
                    AgitationSetpointRpm = 300, AirflowSetpointLpm = 2, GasRoute = GasRoute.Reactor,
                    AirInletInput = GasInput.Input2,
                    Actuators = [new() { Actuator = ActuatorId.Agitation, Owner = CommandOwner.Recipe,
                        OwnerExecutionId = context.RecipeRunId.ToString(), DesiredCommandJson = "{\"motorSetpoint\":300}", ConfirmationChannel = "servo" },
                        new() { Actuator = ActuatorId.Aeration, Owner = CommandOwner.Recipe,
                        OwnerExecutionId = context.RecipeRunId.ToString(), DesiredCommandJson = "{\"flowSetpoint\":2}", ConfirmationChannel = "flowmeter" }],
                    Controllers = [new() { ControllerId = "cascade", WasActive = true,
                        StateVersion = "1", StateJson = "{\"oxygenSetpoint\":30,\"integral\":0.5}" }] } },
        };
    }

    private static KlaRecipeAttemptResult Attempt(KlaRecipeRequest request) => new()
    {
        AttemptId = Guid.NewGuid(), ConditionId = request.Definition.Conditions[0].ConditionId,
        ReplicateNumber = 1, AttemptNumber = 1, RunFolder = "run-1", KlaPerHour = 40,
        KlaQuality = KlaScientificQuality.Valid, OurQuality = KlaScientificQuality.NotApplicable,
        Restoration = KlaRestorationState.Confirmed, ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId,
        PersistenceConfirmed = true, DecisionAuthor = RecipeDecisionAuthor.AutomaticPolicy,
        Decision = KlaAutomaticDecision.Selected, PolicyVersion = "1", DecidedUtc = DateTimeOffset.UtcNow,
    };

    private static KlaRecipeResult Result(KlaRecipeRequest request, params KlaRecipeAttemptResult[] attempts) => new()
    {
        Context = request.Context, SessionId = Guid.NewGuid(), SessionFolder = "session-1",
        PeriodicInvocation = request.PeriodicInvocation,
        Status = KlaRecipeTerminalStatus.Completed, Attempts = attempts.ToImmutableArray(),
        PreAssayStateRestored = true, PersistenceConfirmed = true,
    };
}
