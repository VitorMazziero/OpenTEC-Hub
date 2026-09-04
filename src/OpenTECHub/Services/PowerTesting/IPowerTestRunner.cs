using OpenTECHub.Protocol;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Runs phase-1 impeller-power conditions from live servo telemetry.</summary>
public interface IPowerTestRunner : IDisposable
{
    PowerTestDocument? CurrentTest { get; }
    PowerRun? CurrentRun { get; }
    PowerCondition? CurrentCondition { get; }
    PowerRunPhase Phase { get; }
    bool IsRunning { get; }
    bool IsInReview { get; }
    bool IsPausedByOperator { get; }
    bool IsPausedForMeasurement { get; }
    double PhaseElapsedSeconds { get; }
    double TotalElapsedSeconds { get; }
    double CurrentRpm { get; }
    double CurrentTorquePercent { get; }
    double CurrentTorqueCi95Percent { get; }
    double CurrentTorqueCiTargetPercent { get; }
    int CurrentAttempt { get; }
    string StatusMessage { get; }
    IReadOnlyList<PowerDataPoint> CurrentRunPoints { get; }
    IReadOnlyList<PowerGlobalSeriesSample> GlobalSeriesSamples { get; }

    event Action? StateChanged;
    event Action<PowerDataPoint>? DataPointAdded;
    event Action<string>? Logged;

    bool CanStart(PowerTestDocument doc, out string? reason);
    void PrepareTest(PowerTestDocument doc);
    Task StartTestAsync(PowerTestDocument doc, CancellationToken cancellationToken = default);
    Task StartRunAsync(PowerCondition condition, int replicateNumber, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SkipCurrentConditionAsync(string reason = "Condição pulada pelo operador");
    Task ResumeAfterMeasurementAsync(CancellationToken cancellationToken = default);
    Task SubmitManualEnergyAsync(
        double electricalPowerW,
        string? instrument = null,
        string? note = null,
        CancellationToken cancellationToken = default);
    Task StopRunAndReviewAsync(string reason = "Parada pelo operador");
    Task AcceptRunAsync();
    Task RejectRunAsync(string reason);
    Task RepeatRunAsync(CancellationToken cancellationToken = default);
    Task CompleteTestAsync();
    Task AbortTestAsync(string reason);
}
