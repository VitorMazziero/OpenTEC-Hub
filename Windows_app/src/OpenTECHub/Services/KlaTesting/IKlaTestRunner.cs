using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.KlaTesting;

public interface IKlaTestRunner : IDisposable
{
    KlaTestDocument? CurrentTest { get; }
    KlaTestRun? CurrentRun { get; }
    KlaTestCondition? CurrentCondition { get; }
    MotorRouteCoordinator? RouteCoordinator => null;
    RunPhase Phase { get; }
    bool IsRunning { get; }
    bool IsInReview { get; }
    double CurrentDO { get; }
    double CurrentDORaw { get; }
    double CurrentFlowMeasured { get; }
    double? CurrentDODerivative { get; }
    int StabilityConfirmationCount { get; }
    int VentFlowStableCount { get; }
    double? VentFlowDeviation { get; }
    double PhaseElapsedSeconds { get; }
    double TotalElapsedSeconds { get; }
    string StatusMessage { get; }

    IReadOnlyList<KlaRawDataPoint> CurrentRunPoints { get; }
    IReadOnlyList<KlaGlobalSeriesSample> GlobalSeriesSamples { get; }

    event Action? StateChanged;
    event Action<KlaRawDataPoint>? DataPointAdded;
    event Action<string>? Logged;

    Task StartTestAsync(KlaTestDocument doc, CancellationToken ct = default);
    void PrepareTest(KlaTestDocument doc);
    Task StartRunAsync(KlaTestCondition condition, int replicateNumber, CancellationToken ct = default);
    Task StopRunAndReviewAsync(string reason = "Parada pelo operador");
    Task AcceptRunAsync(KlaAnalysisRevision analysis);
    Task RejectRunAsync(string reason);
    Task RepeatRunAsync();
    Task CompleteTestAsync();
    Task AbortTestAsync(string reason);

    void UpdateLiveSettings(KlaTestSettings settings);
    void SetDegassingAgitation(double rpm);
}
