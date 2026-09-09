using System;
using System.Collections.Generic;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Active motor control route in power assay or tare pipeline.</summary>
public enum PowerMotorRoute
{
    Modbus = (int)MotorRoute.Modbus,
    UartFallback = (int)MotorRoute.UartFallback,
}

/// <summary>
/// Coordinates the motor control route for all power assays and tare sweeps.
/// Prioritizes direct Modbus control (which supports up to 1000 RPM) and falls back
/// automatically to UART/CN1 (capped at 965 RPM) if Modbus is unavailable or fails.
/// </summary>
public sealed class PowerMotorRouteCoordinator : MotorRouteCoordinator
{
    public PowerMotorRouteCoordinator(
        ICommandArbiter arbiter,
        IDeviceService device,
        CommandOwner owner = CommandOwner.PowerAssay)
        : base(arbiter, device, owner)
    {
    }

    /// <summary>
    /// Evaluates if an assay document has any condition exceeding the effective route ceiling.
    /// </summary>
    public bool ValidateConditions(PowerTestDocument doc, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (IsUartFallback)
        {
            foreach (var c in doc.Conditions)
            {
                if (c.AgitationRpm > UartFallbackMaxRpm)
                {
                    errorMessage = $"Em modo de fallback UART, a rotação máxima é de {UartFallbackMaxRpm:F0} rpm. " +
                                   $"A condição de {c.AgitationRpm:F0} rpm requer comunicação Modbus com o servo drive.";
                    return false;
                }
            }
        }

        errorMessage = null;
        return true;
    }
}
