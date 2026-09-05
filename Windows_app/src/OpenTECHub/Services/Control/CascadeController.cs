using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Control;

/// <summary>
/// One cascade step's commanded actuation, with the loop terms that produced it.
/// </summary>
/// <param name="AgitationRpm">Agitation command, rounded to the integer the wire carries.</param>
/// <param name="AerationLpm">Aeration command, in litres per minute.</param>
/// <param name="OxygenSetpoint">The dissolved-oxygen target echoed to the device.</param>
/// <param name="Terms">The decomposed PID evaluation, for the live-terms display.</param>
public sealed record CascadeActuationResult(
    int AgitationRpm,
    double AerationLpm,
    double OxygenSetpoint,
    CascadeTerms Terms);

/// <summary>
/// The oxygen cascade: a dual-loop PID controller whose scalar effort is split across the
/// agitation and aeration actuators.
/// </summary>
public sealed class CascadeController
{
    /// <summary>Actuator name for agitation, shared with the allocator and the wire mapping.</summary>
    public const string AgitationActuator = "agitation";

    /// <summary>Actuator name for aeration.</summary>
    public const string AerationActuator = "aeration";

    private readonly CascadeTwoLoopPidController _pid;
    private CascadeAllocation _allocation;

    public CascadeController(
        CascadeTuning tuning,
        ActuatorWindow agitation,
        ActuatorWindow aeration,
        double oxygenSetpoint)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(agitation);
        ArgumentNullException.ThrowIfNull(aeration);

        _pid = new CascadeTwoLoopPidController(tuning, oxygenSetpoint);
        _allocation = new WindowAllocation(
            agitation with { Name = AgitationActuator },
            aeration with { Name = AerationActuator });
    }

    /// <summary>
    /// A ready-to-run oxygen cascade with provisional actuator bands.
    /// </summary>
    public static CascadeController CreateDefault(double oxygenSetpoint = 30.0)
        => new(
            new CascadeTuning(),
            new ActuatorWindow(AgitationActuator, Min: 200, Max: 800, EffortStart: 0, EffortEnd: 40),
            new ActuatorWindow(AerationActuator, Min: 0.5, Max: 5.0, EffortStart: 30, EffortEnd: 70),
            oxygenSetpoint);

    /// <summary>The dissolved-oxygen target, in percent.</summary>
    public double OxygenSetpoint
    {
        get => _pid.Setpoint;
        set => _pid.Setpoint = value;
    }

    /// <summary>The active tuning.</summary>
    public CascadeTuning Tuning => _pid.Tuning;

    /// <summary>The most recent decomposed evaluation.</summary>
    public CascadeTerms LastTerms => _pid.LastTerms;

    /// <summary>The current control effort in percent, before allocation.</summary>
    public double Effort => _pid.Output;

    /// <summary>The active effort-to-actuator mapping.</summary>
    public CascadeAllocation Allocation => _allocation;

    /// <summary>The configured actuator windows.</summary>
    public IReadOnlyList<ActuatorWindow> Windows => _allocation.Windows;

    /// <summary>
    /// Swaps the effort-to-actuator mapping without disturbing the controller's probe history.
    /// </summary>
    public void SetAllocation(CascadeAllocation allocation)
        => _allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));

    /// <summary>
    /// Advances the cascade by <paramref name="dtSeconds"/> against a new dissolved-oxygen
    /// reading and returns the actuation to command.
    /// </summary>
    public CascadeActuationResult Update(double dissolvedOxygenPercent, double dtSeconds)
    {
        var terms = _pid.Update(
            dissolvedOxygenPercent,
            dtSeconds,
            _allocation.Windows,
            isCascadeMode: _allocation is WindowAllocation);

        var (rpm, flow) = _allocation.Allocate(terms.Output);

        return new CascadeActuationResult(
            AgitationRpm: (int)Math.Round(rpm, MidpointRounding.AwayFromZero),
            AerationLpm: flow,
            OxygenSetpoint: _pid.Setpoint,
            Terms: terms);
    }

    /// <summary>
    /// Builds the combined cascade frame for a step's result.
    /// </summary>
    public static OpenTECCommand BuildCommand(CascadeActuationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return CommandBuilders.CascadeActuation(
            result.AerationLpm, result.OxygenSetpoint, result.AgitationRpm);
    }

    /// <summary>Applies new tuning without discarding the probe history if the window is unchanged.</summary>
    public void Retune(CascadeTuning tuning) => _pid.Retune(tuning);

    /// <summary>Arms the loop bumplessly at a known control effort.</summary>
    public void Preload(double effortPercent) => _pid.Preload(effortPercent);

    /// <summary>Clears the reported integral contribution without disturbing the probe history.</summary>
    public void ResetIntegral() => _pid.ResetIntegral();

    /// <summary>Clears all loop state when the cascade is disarmed.</summary>
    public void Reset() => _pid.Reset();
}
