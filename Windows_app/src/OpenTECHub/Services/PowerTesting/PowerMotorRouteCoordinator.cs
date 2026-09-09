using System;
using System.Collections.Generic;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Active motor control route in power assay or tare pipeline.</summary>
public enum PowerMotorRoute
{
    Modbus,
    UartFallback,
}

/// <summary>
/// Coordinates the motor control route for all power assays and tare sweeps.
/// Prioritizes direct Modbus control (which supports up to 1000 RPM) and falls back
/// automatically to UART/CN1 (capped at 965 RPM) if Modbus is unavailable or fails.
/// </summary>
public sealed class PowerMotorRouteCoordinator
{
    public const double MinRpm = 15.0;
    public const double ModbusMaxRpm = 1000.0;
    public const double UartFallbackMaxRpm = 965.0;

    private readonly ICommandArbiter _arbiter;
    private readonly IDeviceService _device;
    private readonly CommandOwner _owner;

    public PowerMotorRoute ActiveRoute { get; private set; } = PowerMotorRoute.Modbus;
    public bool IsModbusActive => ActiveRoute == PowerMotorRoute.Modbus;
    public bool IsUartFallback => ActiveRoute == PowerMotorRoute.UartFallback;
    public double EffectiveMaxRpm => IsModbusActive ? ModbusMaxRpm : UartFallbackMaxRpm;
    public string? FallbackReason { get; private set; }

    public event Action<PowerMotorRoute, string?>? RouteChanged;

    public PowerMotorRouteCoordinator(
        ICommandArbiter arbiter,
        IDeviceService device,
        CommandOwner owner = CommandOwner.PowerAssay)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(device);
        _arbiter = arbiter;
        _device = device;
        _owner = owner;
    }

    /// <summary>
    /// Evaluates whether the latest telemetry indicates Modbus route is available and healthy.
    /// </summary>
    public static bool IsModbusCandidate(SensorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return true;
        }

        if (snapshot.HasServoTelemetry && !snapshot.ServoOnline)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts to configure Modbus route as primary. If Modbus is candidate, sends
    /// motorControlMode: 1. If refused or if the servo is known offline, falls back to UART.
    /// </summary>
    public bool EnsurePrimaryRoute(out string statusMessage)
    {
        var latest = _device.Latest;
        if (!IsModbusCandidate(latest))
        {
            return ActivateUartFallback(
                "Servo drive offline; comunicação Modbus indisponível.",
                out statusMessage);
        }

        var cmd = CommandBuilders.MotorControlMode(viaModbus: true);
        var result = _arbiter.Dispatch(_owner, cmd);
        if (result.Accepted)
        {
            ActiveRoute = PowerMotorRoute.Modbus;
            FallbackReason = null;
            statusMessage = "Rota Modbus direto ativada prioritariamente (faixa de 15 a 1000 rpm).";
            RouteChanged?.Invoke(ActiveRoute, null);
            return true;
        }

        return ActivateUartFallback(
            $"Comando de rota Modbus recusado ({string.Join(", ", result.Refused)}).",
            out statusMessage);
    }

    /// <summary>
    /// Switches to UART/CN1 fallback mode (capped at 965 RPM).
    /// </summary>
    public bool ActivateUartFallback(string reason, out string statusMessage)
    {
        ActiveRoute = PowerMotorRoute.UartFallback;
        FallbackReason = reason;

        var cmd = CommandBuilders.MotorControlMode(viaModbus: false);
        _arbiter.Dispatch(_owner, cmd);

        statusMessage = $"Modo de fallback UART ativo ({reason}). A rotação máxima fica limitada a {UartFallbackMaxRpm:F0} rpm.";
        RouteChanged?.Invoke(ActiveRoute, reason);
        return false;
    }

    /// <summary>
    /// Checks if a requested setpoint RPM is valid under the currently active route.
    /// If in UART fallback and RPM exceeds 965, returns false with an explanatory message.
    /// </summary>
    public bool ValidateRpm(double rpm, out string? errorMessage)
    {
        if (!double.IsFinite(rpm) || rpm < MinRpm || rpm > ModbusMaxRpm)
        {
            errorMessage = $"A rotação deve estar entre {MinRpm:F0} e {ModbusMaxRpm:F0} rpm.";
            return false;
        }

        if (IsUartFallback && rpm > UartFallbackMaxRpm)
        {
            errorMessage = $"Em modo de fallback UART, a rotação máxima é de {UartFallbackMaxRpm:F0} rpm " +
                           $"(solicitado: {rpm:F0} rpm). Restabeleça a comunicação Modbus com o servo drive para alcançar até {ModbusMaxRpm:F0} rpm.";
            return false;
        }

        errorMessage = null;
        return true;
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

    /// <summary>
    /// Adjusts tare targets for the active route: under UART fallback, caps targets at 965 RPM.
    /// </summary>
    public List<double> AdjustTareTargets(IEnumerable<double> targets)
    {
        var result = new List<double>();
        foreach (var t in targets)
        {
            var clamped = IsUartFallback ? Math.Min(UartFallbackMaxRpm, t) : t;
            if (result.Count == 0 || Math.Abs(result[^1] - clamped) >= 1.0)
            {
                result.Add(clamped);
            }
        }
        return result;
    }
}
