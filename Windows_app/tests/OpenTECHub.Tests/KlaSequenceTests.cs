using System;
using System.Linq;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaSequenceTests
{
    private readonly Guid _condition = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddHours(1);
    private KlaTestDocument Session(KlaAssayProtocol protocol = KlaAssayProtocol.Biotic) => new()
    {
        Protocol = protocol, SequenceLimits = new(),
        Settings = new() { MaxDegassingTimeMinutes = 1 },
        ProtocolSettings = new() { AerationReturn = new() { MaximumGasOffSeconds = 60, MinimumInterAssaySeconds = 30 } },
        Conditions = [new() { ConditionId = _condition, AgitationRpm = 400, AirflowLpm = 3, RequestedReplicates = 2 }],
    };
    private KlaTestRunSummary Run(int rep, int attempt, bool accepted, KlaRestorationState restoration = KlaRestorationState.Confirmed) => new()
    {
        RunId = Guid.NewGuid(), ConditionId = _condition, ReplicateNumber = rep, AttemptNumber = attempt,
        Phase = accepted ? RunPhase.Accepted : RunPhase.Rejected,
        Decision = accepted ? DecisionQuality.Acceptable : DecisionQuality.Inconclusive,
        Outcome = new() { OperatorDecision = accepted ? KlaOperatorDecision.Accepted : KlaOperatorDecision.Rejected,
            KlaQuality = accepted ? KlaScientificQuality.Valid : KlaScientificQuality.Inconclusive, Restoration = restoration },
        CompletedUtc = _now.AddMinutes(-2), RemovalSeconds = 20,
    };

    [Fact]
    public void Rejected_attempt_does_not_consume_the_next_replicate_and_history_is_preserved()
    {
        var doc = Session();
        doc.Runs.Add(Run(1, 1, false));
        var pending = KlaSequence.Pending(doc, doc.Conditions);
        Assert.Equal(new KlaQueueItem(_condition, 1, 2), pending[0]);
        doc.Runs.Add(Run(1, 2, true));
        KlaSequence.RefreshCounters(doc, doc.Conditions[0]);
        Assert.Equal(1, doc.Conditions[0].AcceptedReplicates);
        Assert.Equal(1, doc.Conditions[0].CompletedReplicates);
        Assert.Equal(0, doc.Conditions[0].RejectedReplicates);
        Assert.Equal(new KlaQueueItem(_condition, 2, 1), Assert.Single(KlaSequence.Pending(doc, doc.Conditions)));
        Assert.Equal(2, doc.Runs.Count);
    }

    [Theory]
    [InlineData(KlaRestorationState.Pending)] [InlineData(KlaRestorationState.Failed)]
    [InlineData(KlaRestorationState.NotRecorded)] [InlineData(KlaRestorationState.NotRequired)]
    public void Biotic_queue_requires_confirmed_return_even_after_operator_acceptance(KlaRestorationState state)
    {
        var doc = Session(); doc.Runs.Add(Run(1, 1, true, state));
        Assert.False(KlaSequence.Check(doc, new(_condition, 2, 1), _now).CanStart);
    }

    [Fact]
    public void Operator_pending_review_blocks_next_run_even_with_confirmed_return()
    {
        var doc = Session(); doc.Runs.Add(Run(1, 1, false) with
            { Phase = RunPhase.Reviewing, Outcome = new() { Restoration = KlaRestorationState.Confirmed } });
        Assert.False(KlaSequence.Check(doc, new(_condition, 2, 1), _now).CanStart);
    }

    [Fact]
    public void Attempts_and_total_runs_have_independent_limits()
    {
        var doc = Session(); doc.SequenceLimits = new() { MaximumAttemptsPerReplicate = 2, MaximumRuns = 3 };
        doc.Runs.Add(Run(1, 1, false)); doc.Runs.Add(Run(1, 2, false));
        Assert.False(KlaSequence.Check(doc, new(_condition, 1, 3), _now).CanStart);
        Assert.True(KlaSequence.Check(doc, new(_condition, 2, 1), _now).CanStart);
        doc.Runs.Add(Run(2, 1, false));
        Assert.False(KlaSequence.Check(doc, new(_condition, 2, 2), _now).CanStart);
    }

    [Fact]
    public void Budget_reserves_the_entire_next_pulse_and_interval_is_after_previous_completion()
    {
        var doc = Session(); doc.SequenceLimits = new() { MaximumRemovalSeconds = 70 };
        doc.Runs.Add(Run(1, 1, true));
        Assert.Contains("acumulado", KlaSequence.Check(doc, new(_condition, 2, 1), _now).Reason);
        doc.SequenceLimits = new() { MaximumRemovalSeconds = 100 };
        doc.Runs[0] = doc.Runs[0] with { CompletedUtc = _now.AddSeconds(-10) };
        var ready = KlaSequence.Check(doc, new(_condition, 2, 1), _now);
        Assert.False(ready.CanStart); Assert.Equal(20, ready.WaitSeconds);
        Assert.True(KlaSequence.Check(doc, new(_condition, 2, 1), _now.AddSeconds(20)).CanStart);
    }

    [Fact]
    public void Multiple_accepted_attempts_remain_one_replicate()
    {
        var doc = Session(); doc.Runs.Add(Run(1, 1, true)); doc.Runs.Add(Run(1, 2, true));
        KlaSequence.RefreshCounters(doc, doc.Conditions[0]);
        Assert.Equal(1, doc.Conditions[0].AcceptedReplicates);
        Assert.Equal(2, Assert.Single(KlaSequence.Pending(doc, doc.Conditions)).ReplicateNumber);
    }

    [Fact]
    public void Measurement_context_distinguishes_cultivation_time_and_simulation()
    {
        var context = new KlaMeasurementContext { Medium = "Meio A", CultivationId = "Cultivo 7", TimeWindow = "Antes alimentação", Source = KlaMeasurementSource.Physical };
        Assert.True(context.IsDefinedFor(KlaAssayProtocol.Biotic));
        Assert.False((context with { TimeWindow = "" }).IsDefinedFor(KlaAssayProtocol.Biotic));
        Assert.True(context.CompatibleWith(context with { Medium = " meio a " }));
        Assert.False(context.CompatibleWith(context with { Source = KlaMeasurementSource.Simulation }));
        Assert.False(context.CompatibleWith(context with { CultivationId = "Cultivo 8" }));
        Assert.False(context.CompatibleWith(context with { TimeWindow = "Após alimentação" }));
    }

    [Fact]
    public void Exposure_counts_removal_and_pre_stabilization_but_not_recovery_or_review()
    {
        KlaRawDataPoint Point(double t, RunPhase phase) => new(_now.AddSeconds(t), t, phase, 10000, 50, 3, 3, 100, false, true, false);
        Assert.Equal(50, KlaSequence.RemovalExposure(new[] { Point(0, RunPhase.Preflight), Point(10, RunPhase.Deoxygenating),
            Point(30, RunPhase.PrestagingAir), Point(45, RunPhase.SwitchingToReactor), Point(60, RunPhase.Reoxygenating), Point(100, RunPhase.Reviewing) }));
    }
}
