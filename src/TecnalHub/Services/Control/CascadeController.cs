using TecnalHub.Protocol;

namespace TecnalHub.Services.Control;

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
/// The oxygen cascade: a velocity-form PID whose scalar effort is split across the
/// agitation and aeration actuators.
/// </summary>
/// <remarks>
/// <para>
/// This composes the control law (<see cref="VelocityPidController"/>) with the actuator
/// split (<see cref="ActuatorWindowAllocator"/>) and produces exactly the three values
/// the combined cascade frame carries - flow, oxygen setpoint and motor rpm - so its
/// output maps straight onto <see cref="CommandBuilders.CascadeActuation"/>.
/// </para>
/// <para>
/// <b>What is deferred.</b> The effort split is a linear window allocation here. The
/// manuscript's kLa gradient-path allocation - fit a bicubic B-spline of kLa over
/// agitation × aeration, start at maximum actuator headroom, then track steepest ascent -
/// is a later Phase 2 work package gated on the surface export decision
/// (<c>docs/DECISIONS.md</c> D-008). It will replace the allocator, not the controller.
/// Gain scheduling and the OUR soft sensor are likewise still to come. The default
/// actuator bands below are provisional simulator values, not a field configuration.
/// </para>
/// </remarks>
public sealed class CascadeController
{
    /// <summary>Actuator name for agitation, shared with the allocator and the wire mapping.</summary>
    public const string AgitationActuator = "agitation";

    /// <summary>Actuator name for aeration.</summary>
    public const string AerationActuator = "aeration";

    private readonly VelocityPidController _pid;
    private readonly ActuatorWindowAllocator _allocator;

    public CascadeController(
        CascadeTuning tuning,
        ActuatorWindow agitation,
        ActuatorWindow aeration,
        double oxygenSetpoint)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(agitation);
        ArgumentNullException.ThrowIfNull(aeration);

        _pid = new VelocityPidController(tuning, oxygenSetpoint);
        _allocator = new ActuatorWindowAllocator(
            agitation with { Name = AgitationActuator },
            aeration with { Name = AerationActuator });
    }

    /// <summary>
    /// A ready-to-run oxygen cascade with provisional actuator bands.
    /// </summary>
    /// <remarks>
    /// Agitation carries the low half of the effort and aeration the high half, sharing a
    /// deliberate overlap so the two hand over smoothly rather than stepping. The numbers
    /// are simulator defaults; the field configuration and the kLa-driven split arrive
    /// with the surface (see the class remarks).
    /// </remarks>
    public static CascadeController CreateDefault(double oxygenSetpoint = 30.0)
        => new(
            new CascadeTuning(),
            new ActuatorWindow(AgitationActuator, Min: 200, Max: 800, EffortStart: 0, EffortEnd: 65),
            new ActuatorWindow(AerationActuator, Min: 0.5, Max: 5.0, EffortStart: 35, EffortEnd: 100),
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

    /// <summary>The configured actuator windows, for the tuning workspace's stacked bar.</summary>
    public IReadOnlyList<ActuatorWindow> Windows => _allocator.Windows;

    /// <summary>
    /// Advances the cascade by <paramref name="dtSeconds"/> against a new dissolved-oxygen
    /// reading and returns the actuation to command.
    /// </summary>
    public CascadeActuationResult Update(double dissolvedOxygenPercent, double dtSeconds)
    {
        var terms = _pid.Update(dissolvedOxygenPercent, dtSeconds);
        var rpm = _allocator.Allocate(AgitationActuator, terms.Output);
        var flow = _allocator.Allocate(AerationActuator, terms.Output);

        return new CascadeActuationResult(
            AgitationRpm: (int)Math.Round(rpm, MidpointRounding.AwayFromZero),
            AerationLpm: flow,
            OxygenSetpoint: _pid.Setpoint,
            Terms: terms);
    }

    /// <summary>
    /// Builds the combined cascade frame for a step's result.
    /// </summary>
    /// <remarks>
    /// The one place the cascade meets the wire. It goes through
    /// <see cref="CommandBuilders.CascadeActuation"/> so the frozen key order and the
    /// inverted <c>v_Flow</c> rule are applied in exactly one tested location, never
    /// re-derived here. It does not send: the caller queues it on the shared command
    /// queue, so a running cascade and an operator cannot fight over the link.
    /// </remarks>
    public static TecnalCommand BuildCommand(CascadeActuationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return CommandBuilders.CascadeActuation(
            result.AerationLpm, result.OxygenSetpoint, result.AgitationRpm);
    }

    /// <summary>Applies new tuning without discarding the probe history if the window is unchanged.</summary>
    public void Retune(CascadeTuning tuning) => _pid.Retune(tuning);

    /// <summary>Arms the loop bumplessly at a known control effort.</summary>
    public void Preload(double effortPercent) => _pid.Preload(effortPercent);

    /// <summary>Clears all loop state when the cascade is disarmed.</summary>
    public void Reset() => _pid.Reset();
}
