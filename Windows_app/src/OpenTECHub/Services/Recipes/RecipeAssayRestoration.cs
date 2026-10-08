using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

/// <summary>Physical return evidence, not a persistence receipt and not permission to resume producers.</summary>
public sealed record RecipeAssayRecoveryResult(Guid SnapshotId, KlaRestorationState Restoration,
    DateTimeOffset CompletedUtc, bool EmergencyStopped, string? Reason, string? ConfirmationJson);

public sealed record RecipeAssayRecoveryCriteria
{
    public required double MaximumTelemetryAgeSeconds { get; init; }
    public double? MinimumOxygenPercent { get; init; }
    public double? MaximumOxygenPercent { get; init; }
    public double? MaximumOxygenSlopePercentPerSecond { get; init; }
    public void Validate()
    {
        ContractGuard.Positive(MaximumTelemetryAgeSeconds);
        if (MinimumOxygenPercent.HasValue != MaximumOxygenPercent.HasValue ||
            MinimumOxygenPercent.HasValue != MaximumOxygenSlopePercentPerSecond.HasValue)
            throw new ArgumentException("Faixa e estabilidade de OD devem ser configuradas juntas.");
        if (MinimumOxygenPercent is { } minimum)
        {
            ContractGuard.NonNegative(minimum); ContractGuard.Positive(MaximumOxygenPercent!.Value);
            ContractGuard.NonNegative(MaximumOxygenSlopePercentPerSecond!.Value);
            if (minimum >= MaximumOxygenPercent || MaximumOxygenPercent > 100)
                throw new ArgumentException("Faixa de retomada de OD inválida.");
        }
    }
}

/// <summary>
/// Applies the complete frozen actuator state while KlaAssay still holds the reservation.
/// Recovery has its own deadline; callers must not pass the cancelled acquisition token.
/// Ownership is returned later, after this evidence is durably stored.
/// </summary>
public sealed class RecipeAssayRestoration(IDeviceService device, TimeProvider time)
{
    public async Task<RecipeAssayRecoveryResult> RestoreAsync(RecipeAssayResourceLease lease,
        KlaRecipeRestorationContract contract, RecipeAssayRecoveryCriteria criteria,
        CancellationToken recoveryCancellation = default)
    {
        contract.Validate(); criteria.Validate();
        var snapshot = contract.BeforeAssay;
        lease.ValidateRecoverySnapshot(snapshot);
        var commands = RecipeAssayReturnState.Validate(snapshot);
        var motor = commands[ActuatorId.Agitation]; var gas = commands[ActuatorId.Aeration];
        var modbus = RecipeAssayReturnState.Flag(motor, CommandKeys.MotorControlMode);
        var baselineFlowCommand = device.Latest?.FlowCommandId ?? -1;
        var sync = new object();
        var changed = Signal();
        long version = 0;
        var samples = new Queue<(SensorSnapshot Sample, long At, long Version)>();
        void OnTelemetry(SensorSnapshot sample)
        {
            TaskCompletionSource signal;
            lock (sync)
            {
                samples.Enqueue((sample, time.GetTimestamp(), ++version));
                if (samples.Count > 256) samples.Dequeue();
                signal = changed; changed = Signal();
            }
            signal.TrySetResult();
        }
        void OnState(ConnectionStateChange _)
        {
            TaskCompletionSource signal;
            lock (sync) { signal = changed; changed = Signal(); }
            signal.TrySetResult();
        }
        device.TelemetryReceived += OnTelemetry;
        device.StateChanged += OnState;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(contract.MaximumRecoverySeconds), time);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(recoveryCancellation, deadline.Token);
        var token = cancellation.Token;
        var started = time.GetTimestamp();
        var oxygen = new Queue<(double Time, double Value)>();
        try
        {
            EnsureAuthority();
            await lease.DrainAssayCommandsAsync(token).ConfigureAwait(false);
            // Return gas immediately; motor route changes follow a stop/route/reference sequence.
            Send(gas);
            Send(CommandBuilders.MotorSetpoint(0));
            await lease.DrainAssayCommandsAsync(token).ConfigureAwait(false);
            long routeBaseline; lock (sync) routeBaseline = version;
            Send(motor.SelectKeys(key => key == CommandKeys.MotorControlMode));
            await lease.DrainAssayCommandsAsync(token).ConfigureAwait(false);
            await WaitForRouteAsync(routeBaseline).ConfigureAwait(false);
            long finalBaseline; lock (sync) finalBaseline = version;
            Send(motor.SelectKeys(key => key != CommandKeys.MotorControlMode));
            foreach (var command in commands.Where(pair => pair.Key is not (ActuatorId.Agitation or ActuatorId.Aeration)))
                Send(command.Value);
            await lease.DrainAssayCommandsAsync(token).ConfigureAwait(false);
            var evidence = await WaitForStableAsync(finalBaseline).ConfigureAwait(false);
            EnsureAuthority();
            if (!lease.ControllersPreserved(snapshot))
                throw new InvalidOperationException("Controlador anterior encerrado ou alterado durante o ensaio.");
            var result = new RecipeAssayRecoveryResult(snapshot.SnapshotId, KlaRestorationState.Confirmed, time.GetUtcNow(), false, null, evidence);
            lease.RecordRecoveryEvidence(result);
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var revoked = !lease.IsAssayAuthorityCurrent;
            return new(snapshot.SnapshotId, KlaRestorationState.Failed, time.GetUtcNow(), revoked,
                revoked ? "Autoridade revogada: recuperação não pode reativar saídas." :
                deadline.IsCancellationRequested ? "Prazo próprio de recuperação esgotado." : error.Message, null);
        }
        finally
        {
            device.TelemetryReceived -= OnTelemetry;
            device.StateChanged -= OnState;
        }

        void EnsureAuthority()
        {
            token.ThrowIfCancellationRequested();
            if (time.GetElapsedTime(started).TotalSeconds > contract.MaximumRecoverySeconds)
                throw new TimeoutException("Prazo próprio de recuperação esgotado.");
            if (device.State != ConnectionState.Connected || !lease.IsAssayAuthorityCurrent)
                throw new InvalidOperationException("Link ou autoridade indisponível durante recuperação.");
        }

        void Send(OpenTECCommand command)
        {
            EnsureAuthority();
            if (!lease.DispatchAssay(command, separateFrame: true).Accepted)
                throw new InvalidOperationException("Comando de recuperação recusado.");
        }

        async Task<(SensorSnapshot Sample, long At, long Version)> NextAsync(long after)
        {
            while (true)
            {
                EnsureAuthority();
                Task pending;
                lock (sync)
                {
                    while (samples.Count > 0)
                    {
                        var sample = samples.Dequeue();
                        if (sample.Version > after && time.GetElapsedTime(sample.At).TotalSeconds <= criteria.MaximumTelemetryAgeSeconds)
                            return sample;
                    }
                    pending = changed.Task;
                }
                await pending.WaitAsync(token).ConfigureAwait(false);
            }
        }

        bool RouteConfirmed(SensorSnapshot sample) => sample.MotorControlViaModbus == modbus &&
            sample.ServoMotorRouteAck == (modbus ? 1 : 0) && sample.ServoCommandPending == false;

        async Task WaitForRouteAsync(long after)
        {
            while (true)
            {
                var observation = await NextAsync(after).ConfigureAwait(false); after = observation.Version;
                if (RouteConfirmed(observation.Sample)) return;
            }
        }

        async Task<string> WaitForStableAsync(long after)
        {
            long? stableSince = null; long? previousSample = null;
            while (true)
            {
                var observation = await NextAsync(after).ConfigureAwait(false);
                var consecutive = observation.Version == after + 1; after = observation.Version;
                var sample = observation.Sample; var at = observation.At;
                var freshGap = consecutive && (previousSample is null || time.GetElapsedTime(previousSample.Value, at).TotalSeconds <= criteria.MaximumTelemetryAgeSeconds);
                previousSample = at;
                var physical = freshGap && RouteConfirmed(sample) && sample.FlowmeterOnline &&
                    !sample.FlowCommandPending && sample.FlowCommandId > baselineFlowCommand && sample.FlowCommandAck == sample.FlowCommandId &&
                    sample.FlowControlEnabled == RecipeAssayReturnState.Flag(gas, CommandKeys.FlowmeterComm) &&
                    sample.FlowValve1 == (RecipeAssayReturnState.Flag(gas, CommandKeys.Valve1) ? 1 : 0) &&
                    sample.FlowValve2 == (RecipeAssayReturnState.Flag(gas, CommandKeys.Valve2) ? 1 : 0) &&
                    sample.FlowValveMain == (RecipeAssayReturnState.Flag(gas, CommandKeys.V_Flow) ? 1 : 0) &&
                    double.IsFinite(sample.FlowSetpoint) && Math.Abs(sample.FlowSetpoint - snapshot.AirflowSetpointLpm) <= contract.FlowToleranceLpm &&
                    double.IsFinite(sample.FlowRate) && Math.Abs(sample.FlowRate -
                        (RecipeAssayReturnState.Flag(gas, CommandKeys.V_Flow) ? 0 : snapshot.AirflowSetpointLpm)) <= contract.FlowToleranceLpm &&
                    sample.HasServoSample && sample.ServoOnline && double.IsFinite(sample.ServoRpm) &&
                    Math.Abs(sample.ServoRpm - snapshot.AgitationSetpointRpm) <= contract.AgitationToleranceRpm && GainsConfirmed(sample);
                var nowSeconds = time.GetElapsedTime(started, at).TotalSeconds;
                if (criteria.MinimumOxygenPercent is { } minimum)
                {
                    if (!freshGap) oxygen.Clear();
                    var validOxygen = sample.OxygenUpdated && double.IsFinite(sample.OxygenCalibrated) &&
                        sample.OxygenCalibrated >= minimum && sample.OxygenCalibrated <= criteria.MaximumOxygenPercent;
                    if (validOxygen) oxygen.Enqueue((nowSeconds, sample.OxygenCalibrated)); else oxygen.Clear();
                    while (oxygen.Count > 2 && nowSeconds - oxygen.ElementAt(1).Time >= contract.StabilitySeconds) oxygen.Dequeue();
                    var slope = KlaTestRunner.TryCalculateSlope(oxygen.Select(p => (p.Time, p.Value)).ToArray(), contract.StabilitySeconds);
                    physical &= validOxygen && oxygen.Count >= 2 && nowSeconds - oxygen.Peek().Time >= contract.StabilitySeconds &&
                        slope.HasValue && Math.Abs(slope.Value) <= criteria.MaximumOxygenSlopePercentPerSecond;
                }
                if (!physical) { stableSince = null; continue; }
                stableSince ??= at;
                if (time.GetElapsedTime(stableSince.Value, at).TotalSeconds < contract.StabilitySeconds) continue;
                return JsonSerializer.Serialize(new { SnapshotId = snapshot.SnapshotId, ReceivedUtc = time.GetUtcNow(),
                    sample.FlowCommandId, sample.FlowCommandAck, sample.FlowSetpoint, sample.FlowRate, sample.FlowValve1,
                    sample.FlowValve2, sample.FlowValveMain, sample.FlowControlEnabled, sample.MotorControlViaModbus,
                    sample.ServoMotorRouteAck, sample.ServoRpm, sample.OxygenCalibrated, sample.FlowKp, sample.FlowKi,
                    sample.FlowFfGain, sample.FlowFfOffset, sample.FlowRampRate,
                    TransportOnlyKeys = commands.Values.SelectMany(c => c.Keys).Where(key => key is CommandKeys.MaxFlow or CommandKeys.OxygenMonitor).ToArray() });
            }
        }

        bool GainsConfirmed(SensorSnapshot sample)
        {
            foreach (var (key, echo) in new (string, double?)[] { (CommandKeys.FlowKp, sample.FlowKp),
                (CommandKeys.FlowKi, sample.FlowKi), (CommandKeys.FlowFfGain, sample.FlowFfGain),
                (CommandKeys.FlowFfOffset, sample.FlowFfOffset), (CommandKeys.FlowRampRate, sample.FlowRampRate) })
                if (gas.Contains(key) && (!echo.HasValue || !double.IsFinite(echo.Value) ||
                    Math.Abs(echo.Value - RecipeAssayReturnState.Number(gas, key)) > 1e-9)) return false;
            return true;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Resends the complete previous state until it is confirmed, up to <paramref name="maximumAttempts"/> times (D-060).
    /// A transient device, such as a flowmeter that drops off the Hub for a few seconds, gets another chance; a revoked
    /// authority (emergency or link loss) is never used to resend outputs.
    /// </summary>
    public static async Task<RecipeAssayRecoveryResult> WithRetriesAsync(Func<Task<RecipeAssayRecoveryResult>> restore,
        Func<bool> authorityCurrent, int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(restore); ArgumentNullException.ThrowIfNull(authorityCurrent);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        var failures = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            var recovery = await restore().ConfigureAwait(false);
            if (recovery.Restoration == KlaRestorationState.Confirmed)
                return failures.Count == 0 ? recovery : recovery with
                    { Reason = $"Retorno confirmado na tentativa {attempt}/{maximumAttempts} ({string.Join("; ", failures)})." };
            failures.Add($"tentativa {attempt}/{maximumAttempts}: {recovery.Reason}");
            if (attempt >= maximumAttempts || recovery.EmergencyStopped || !authorityCurrent())
                return recovery with { Reason = string.Join("; ", failures) };
        }
    }
}
