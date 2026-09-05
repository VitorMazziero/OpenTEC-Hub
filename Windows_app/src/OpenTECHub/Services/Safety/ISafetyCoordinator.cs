using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Safety;

/// <summary>
/// The outcome of an owner-aware safe stop execution.
/// </summary>
/// <param name="Accepted">True when the safe frame was successfully delivered to the transport.</param>
/// <param name="Refused">Any actuators that could not be written due to arbitration conflicts.</param>
/// <param name="FailureReason">User-facing pt-BR explanation if the stop could not be executed.</param>
public sealed record SafetyStopResult(
    bool Accepted,
    IReadOnlyList<ActuatorId> Refused,
    string? FailureReason = null)
{
    public static SafetyStopResult Success() => new(true, []);

    public static SafetyStopResult Refusal(IReadOnlyList<ActuatorId> refused, string reason)
        => new(false, refused, reason);

    public static SafetyStopResult TransportFailure(string reason)
        => new(false, [], reason);
}

/// <summary>
/// Coordinates global emergency safe stops across all automation subsystems, ensuring
/// that ownership conflicts never prevent delivering safe frames to physical actuators.
/// </summary>
public interface ISafetyCoordinator
{
    /// <summary>
    /// Coordinates an owner-aware global safe stop: stops/aborts active recipes, cascades, and assays;
    /// transitions ownership safely; delivers the safe frame(s) to the hardware under valid ownership;
    /// ensures all actuators are returned to Manual; and returns the outcome.
    /// </summary>
    Task<SafetyStopResult> ExecuteGlobalSafeStopAsync(
        OpenTECCommand safeFrame,
        OpenTECCommand? separateDisableFrame = null,
        string reason = "parada segura do operador",
        CancellationToken cancellationToken = default);
}
