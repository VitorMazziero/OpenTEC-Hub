namespace OpenTECHub.Simulator;

/// <summary>
/// A biological cultivation phase defining specific growth rate, carrying capacity, and OUR characteristics.
/// </summary>
public sealed record CultivationPhase(
    string Name,
    double DurationSeconds,
    double SpecificGrowthRatePerSecond,
    double CarryingCapacityAu,
    double SpecificOurPerAu,
    double AcidificationRatePerAu);

/// <summary>
/// Trajectory of biological kinetics driving oxygen uptake rate (OUR) and biomass growth across a cultivation.
/// </summary>
public sealed class CultivationProfile
{
    public string Name { get; }
    public IReadOnlyList<CultivationPhase> Phases { get; }

    public CultivationProfile(string name, IReadOnlyList<CultivationPhase> phases)
    {
        if (phases == null || phases.Count == 0)
        {
            throw new ArgumentException("Profile must contain at least one phase.", nameof(phases));
        }

        Name = name;
        Phases = phases;
    }

    /// <summary>Finds the active phase at the given elapsed simulation time.</summary>
    public CultivationPhase GetPhase(double elapsedSeconds)
    {
        var accumulated = 0.0;
        foreach (var phase in Phases)
        {
            accumulated += phase.DurationSeconds;
            if (elapsedSeconds <= accumulated)
            {
                return phase;
            }
        }

        // Return the last phase (e.g. stationary / death) indefinitely
        return Phases[^1];
    }

    /// <summary>
    /// Default standard single-phase logistic growth matching original simulator constants.
    /// </summary>
    public static CultivationProfile Default => new(
        "default",
        [
            new CultivationPhase("Logistic Growth", double.PositiveInfinity, 0.00035, 12.0, 0.55, 0.0009),
        ]);

    /// <summary>
    /// Realistic bacterial batch cultivation (E. coli):
    /// 1. Lag phase (30m): cell adaptation, low OUR, negligible division
    /// 2. Exponential phase (4h): maximum specific growth rate µ_max, high OUR surge
    /// 3. Stationary phase (4h): substrate exhaustion, zero net growth, maintenance OUR
    /// </summary>
    public static CultivationProfile BatchEColi => new(
        "batch-ecoli",
        [
            new CultivationPhase("Lag (Adaptation)", 30 * 60, 0.00005, 15.0, 0.20, 0.0002),
            new CultivationPhase("Exponential (Active)", 4 * 3600, 0.00055, 12.0, 0.75, 0.0012),
            new CultivationPhase("Stationary (Exhausted)", 4 * 3600, 0.0, 12.0, 0.25, 0.0001),
        ]);

    /// <summary>
    /// Realistic fed-batch cultivation with feed-limiting growth:
    /// 1. Batch phase (2h)
    /// 2. Fed-batch constant feed (6h): substrate-limited growth
    /// 3. Harvest stationary phase (2h)
    /// </summary>
    public static CultivationProfile FedBatch => new(
        "fed-batch",
        [
            new CultivationPhase("Initial Batch", 2 * 3600, 0.00045, 15.0, 0.60, 0.0008),
            new CultivationPhase("Fed-Batch Linear Feeding", 6 * 3600, 0.00020, 30.0, 0.85, 0.0015),
            new CultivationPhase("Stationary / Final", 2 * 3600, 0.0, 30.0, 0.30, 0.0002),
        ]);

    /// <summary>
    /// Step-test profile for cascade PID disturbance rejection verification:
    /// Injects periodic discrete steps in OUR without changing biomass.
    /// </summary>
    public static CultivationProfile StepTest => new(
        "step-test",
        [
            new CultivationPhase("Baseline OUR (Low)", 300, 0.0, 10.0, 0.2, 0.0005),
            new CultivationPhase("Step 1 (Medium OUR)", 600, 0.0, 10.0, 0.6, 0.0005),
            new CultivationPhase("Step 2 (High OUR Surge)", 600, 0.0, 10.0, 1.2, 0.0005),
            new CultivationPhase("Step 3 (Return to Baseline)", 600, 0.0, 10.0, 0.2, 0.0005),
        ]);

    public static CultivationProfile FromName(string name) => (name?.Trim().ToLowerInvariant()) switch
    {
        "batch-ecoli" or "batchecoli" or "batch" => BatchEColi,
        "fed-batch" or "fedbatch" => FedBatch,
        "step-test" or "steptest" or "step" => StepTest,
        _ => Default,
    };
}
