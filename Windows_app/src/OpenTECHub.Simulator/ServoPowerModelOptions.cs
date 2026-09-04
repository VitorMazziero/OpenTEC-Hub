namespace OpenTECHub.Simulator;

/// <summary>One impeller stage in the simulator's deliberately small power model.</summary>
/// <param name="Name">Human-readable geometry name, used only in diagnostics.</param>
/// <param name="DiameterM">Impeller diameter, in metres.</param>
/// <param name="PowerNumber">Toy steady-state <c>Np</c> for this stage.</param>
/// <param name="TareTorquePercentAtZero">Stage share of the dry-running torque at zero speed.</param>
/// <param name="TareTorquePercentPerRpm">Linear stage share of dry-running torque versus rpm.</param>
public sealed record ServoImpellerStage(
    string Name,
    double DiameterM,
    double PowerNumber,
    double TareTorquePercentAtZero,
    double TareTorquePercentPerRpm);

/// <summary>A measured standard-deviation point for servo torque, in percent of rated torque.</summary>
public sealed record ServoTorqueNoisePoint(double Rpm, double SigmaTorquePercent);

/// <summary>
/// Scientific and dynamic inputs for the simulator's impeller-power servo model.
/// </summary>
/// <remarks>
/// This is a structural test model, not a fitted representation of the vessel. Its purpose is to
/// give the power-assay runner a deterministic physical response through the real command and
/// telemetry paths. Geometry and <c>Np</c> are intentionally configurable so tests can state their
/// own expected result instead of depending on these illustrative defaults.
/// </remarks>
public sealed record ServoPowerModelOptions
{
    public double LiquidDensityKgM3 { get; init; } = 998.0;

    public double MotorRatedTorqueNm { get; init; } = 1.27;

    public double SpeedTimeConstantSeconds { get; init; } = 1.5;

    public double TorqueTimeConstantSeconds { get; init; } = 8.0;

    /// <summary>
    /// Simple phase-1 gas coupling. At this flow the liquid-load component reaches
    /// <see cref="GassedLiquidPowerRatio"/>; a flooding knee remains a phase-2 concern.
    /// </summary>
    public double ReferenceGasFlowLpm { get; init; } = 5.0;

    public double GassedLiquidPowerRatio { get; init; } = 0.82;

    public double VesselDiameterM { get; init; } = 0.190;

    public bool SimulateFloodingKnee { get; init; } = true;

    public double VentFlowPulseMagnitude { get; init; } = 2.0;

    public double VentFlowPulseDurationSeconds { get; init; } = 3.5;

    public IReadOnlyList<ServoImpellerStage> Impellers { get; init; } =
    [
        // Two small Rushton stages. Their combined tare preserves the measured empty-shaft
        // baseline used by the original servo simulator: 1.25 % + 0.00122 %/rpm.
        new("Rushton inferior (ilustrativo)", 0.060, 5.0, 0.625, 0.00061),
        new("Rushton superior (ilustrativo)", 0.060, 5.0, 0.625, 0.00061),
    ];

    public IReadOnlyList<ServoTorqueNoisePoint> TorqueNoiseCurve { get; init; } =
    [
        // Bench session 2026-09-03_1340. Values are percentages of nominal torque.
        new(0.0, 0.06),
        new(300.0, 0.62),
        new(600.0, 0.41),
        new(1000.0, 0.41),
    ];

    public static ServoPowerModelOptions Default { get; } = new();
}
