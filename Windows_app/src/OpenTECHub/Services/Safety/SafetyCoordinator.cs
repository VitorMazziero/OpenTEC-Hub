using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.Safety;

/// <summary>
/// Default implementation of <see cref="ISafetyCoordinator"/>.
/// Coordinates global safe stops, disengages active automation, ensures safe frame delivery,
/// and prevents false success reporting on disconnected or refused transports.
/// </summary>
public sealed class SafetyCoordinator : ISafetyCoordinator
{
    private readonly ICommandArbiter _arbiter;
    private readonly IDeviceService _device;
    private readonly IRecipeEngine? _recipeEngine;
    private readonly ICascadeService? _cascade;
    private readonly IKlaTestRunner? _klaRunner;
    private readonly IPowerTestRunner? _powerRunner;
    private readonly ILogger<SafetyCoordinator> _log;

    public SafetyCoordinator(
        ICommandArbiter arbiter,
        IDeviceService device,
        IRecipeEngine? recipeEngine = null,
        ICascadeService? cascade = null,
        IKlaTestRunner? klaRunner = null,
        IPowerTestRunner? powerRunner = null,
        ILogger<SafetyCoordinator>? log = null)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(device);

        _arbiter = arbiter;
        _device = device;
        _recipeEngine = recipeEngine;
        _cascade = cascade;
        _klaRunner = klaRunner;
        _powerRunner = powerRunner;
        _log = log ?? NullLogger<SafetyCoordinator>.Instance;
    }

    public async Task<SafetyStopResult> ExecuteGlobalSafeStopAsync(
        OpenTECCommand safeFrame,
        OpenTECCommand? separateDisableFrame = null,
        string reason = "parada segura do operador",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(safeFrame);

        _log.LogWarning("Global safe stop initiated ({Reason})", reason);

        // 1. If a recipe is running or paused, stop/abort it through its owning engine.
        if (_recipeEngine is not null && _recipeEngine.State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            _log.LogInformation("Stopping active recipe for safe stop...");
            try
            {
                await _recipeEngine.StopAsync($"parada segura: {reason}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error while stopping recipe during global safe stop");
            }
        }

        // 2. Disengage oxygen cascade if engaged.
        if (_cascade is not null && _cascade.IsEngaged)
        {
            _log.LogInformation("Disengaging active cascade for safe stop...");
            _cascade.Disengage($"parada segura: {reason}");
        }

        // 3. Abort assay runners if active.
        if (_klaRunner is not null && _klaRunner.IsRunning)
        {
            _log.LogInformation("Aborting kLa assay for safe stop...");
            try
            {
                await _klaRunner.AbortTestAsync($"parada segura: {reason}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Exception while aborting kLa runner");
            }
        }

        if (_powerRunner is not null && _powerRunner.IsRunning)
        {
            _log.LogInformation("Aborting power assay for safe stop...");
            try
            {
                await _powerRunner.AbortTestAsync($"parada segura: {reason}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Exception while aborting power runner");
            }
        }

        // 4. Check transport connectivity. A disconnected device cannot receive the stop frame;
        // reporting success would be a false positive (AUD-001 / AUD-003).
        if (_device.State != ConnectionState.Connected)
        {
            _log.LogError("Global safe stop failed: transport disconnected (state {State})", _device.State);
            // Ensure ownership is still returned to manual so controls do not stay locked by a phantom owner.
            _arbiter.ReturnToManual($"aborto seguro desconectado: {reason}", isSafeAbort: true);
            return SafetyStopResult.TransportFailure(
                "Equipamento desconectado. O comando de parada segura não pôde ser entregue ao hardware.");
        }

        // 5. Deliver the safe stop frame using the privileged safety path on the arbiter,
        // which forces delivery and resets all owners to Manual.
        var dispatchResult = _arbiter.DispatchSafety(safeFrame, reason, returnToManual: true);
        if (!dispatchResult.Accepted)
        {
            _log.LogError("Global safe stop frame refused by arbiter: {Refused}",
                string.Join(", ", dispatchResult.Refused));
            return SafetyStopResult.Refusal(
                dispatchResult.Refused,
                DispatchRefusal.Describe(dispatchResult, _arbiter.Ownership));
        }

        // 6. Deliver separate disable frame if provided (e.g. pump routing disable).
        if (separateDisableFrame is not null && !separateDisableFrame.IsEmpty)
        {
            var separateResult = _arbiter.DispatchSeparateSafetyFrame(separateDisableFrame, reason, returnToManual: false);
            if (!separateResult.Accepted)
            {
                _log.LogError("Global safe stop secondary frame refused: {Refused}",
                    string.Join(", ", separateResult.Refused));
                return SafetyStopResult.Refusal(
                    separateResult.Refused,
                    DispatchRefusal.Describe(separateResult, _arbiter.Ownership));
            }
        }

        _log.LogInformation("Global safe stop executed successfully under valid Manual ownership.");
        return SafetyStopResult.Success();
    }
}
