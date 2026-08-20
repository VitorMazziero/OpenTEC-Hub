namespace TecnalHub.Tests;

/// <summary>
/// A first-order dissolved-oxygen plant with transport dead time, for controller tests.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the dynamics the running app will meet - the same
/// <c>dC/dt = kLa·(C* − C) − uptake</c> and the same delay-line probe as
/// <c>TecnalHub.Simulator.DeviceModel</c> - so a controller that behaves here behaves
/// there. The dead time is the whole point: a controller tuned against a zero-lag plant
/// oscillates the moment it meets a real polarographic probe
/// (<c>docs/ARCHITECTURE.md</c> section 5).
/// </para>
/// <para>
/// The plant is driven by kLa directly; a test decides how its controller's output maps
/// to kLa, which keeps the plant independent of any particular actuator model.
/// </para>
/// </remarks>
internal sealed class FirstOrderDeadTimePlant
{
    private const double Saturation = 100.0;

    private readonly double _deadTimeSeconds;
    private readonly double _uptake;
    private readonly double _quantum;
    private readonly Queue<(double Time, double Value)> _delay = new();

    private double _time;
    private double _true;
    private double _reported;

    /// <param name="initialOxygen">Starting dissolved oxygen, in percent.</param>
    /// <param name="deadTimeSeconds">Probe transport lag. Real probes sit at 20-40 s.</param>
    /// <param name="uptake">Constant oxygen uptake rate, in percent per second.</param>
    /// <param name="quantum">
    /// Measurement quantisation step. Non-zero reproduces the probe's staircase, which the
    /// least-squares rate estimator exists to smooth. Zero disables it.
    /// </param>
    public FirstOrderDeadTimePlant(
        double initialOxygen = 50.0,
        double deadTimeSeconds = 25.0,
        double uptake = 1.4,
        double quantum = 0.0)
    {
        _deadTimeSeconds = deadTimeSeconds;
        _uptake = uptake;
        _quantum = quantum;
        _true = initialOxygen;
        _reported = initialOxygen;
    }

    /// <summary>The value the probe reports: delayed and, if configured, quantised.</summary>
    public double MeasuredOxygen => _reported;

    /// <summary>The true vessel oxygen, ahead of the probe by the dead time.</summary>
    public double TrueOxygen => _true;

    /// <summary>
    /// Advances the plant by <paramref name="dt"/> seconds under a commanded transfer
    /// coefficient and returns the newly reported measurement.
    /// </summary>
    public double Step(double kLa, double dt)
    {
        _time += dt;

        _true += ((kLa * (Saturation - _true)) - _uptake) * dt;
        _true = Math.Clamp(_true, 0.0, Saturation);

        _delay.Enqueue((_time, _true));
        while (_delay.Count > 0 && _time - _delay.Peek().Time >= _deadTimeSeconds)
        {
            _reported = _delay.Dequeue().Value;
        }

        return _quantum > 0
            ? Math.Round(_reported / _quantum, MidpointRounding.AwayFromZero) * _quantum
            : _reported;
    }

    /// <summary>
    /// The simulator's placeholder kLa as a function of actuation, so a controller that
    /// emits agitation and aeration can be closed onto the plant.
    /// </summary>
    /// <remarks>
    /// A power law, monotonic in both actuators with diminishing returns. Not the
    /// manuscript's fitted surface - that is a later Phase 2 deliverable - but coherent
    /// enough for a controller to climb.
    /// </remarks>
    public static double KLaFromActuation(int rpm, double flowLpm)
    {
        var n = Math.Max(rpm, 0) / 1000.0;
        var q = Math.Max(flowLpm, 0.0) / 10.0;
        return 0.055 * Math.Pow(n, 0.62) * Math.Pow(q, 0.38);
    }
}
