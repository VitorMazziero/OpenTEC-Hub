using System;
using System.Collections.Generic;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>Active motor control route across the system.</summary>
public enum MotorRoute
{
    Modbus,
    UartFallback,
}

/// <summary>
/// Coordinates the motor control route for an automated actuator owner.
/// Prioritizes direct Modbus control (which supports 15 to 1000 RPM) and falls back
/// automatically to UART/CN1 (capped at 965 RPM) if Modbus is unavailable or fails.
/// </summary>
public class MotorRouteCoordinator
{
    public const double MinRpm = 15.0;
    public const double ModbusMaxRpm = 1000.0;
    public const double UartFallbackMaxRpm = 965.0;

    protected readonly ICommandArbiter _arbiter;
    protected readonly IDeviceService _device;
    protected readonly CommandOwner _owner;

    public MotorRoute ActiveRoute { get; protected set; } = MotorRoute.Modbus;
    public bool IsModbusActive => ActiveRoute == MotorRoute.Modbus;
    public bool IsUartFallback => ActiveRoute == MotorRoute.UartFallback;
    public double EffectiveMaxRpm => IsModbusActive ? ModbusMaxRpm : UartFallbackMaxRpm;
    public string? FallbackReason { get; protected set; }

    public event Action<MotorRoute, string?>? RouteChanged;

    public MotorRouteCoordinator(
        ICommandArbiter arbiter,
        IDeviceService device,
        CommandOwner owner)
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
            ActiveRoute = MotorRoute.Modbus;
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
        ActiveRoute = MotorRoute.UartFallback;
        FallbackReason = reason;

        var cmd = CommandBuilders.MotorControlMode(viaModbus: false);
        _arbiter.Dispatch(_owner, cmd);

        statusMessage = $"Modo de fallback UART ativo ({reason}). A rotação máxima fica limitada a {UartFallbackMaxRpm:F0} rpm.";
        RouteChanged?.Invoke(ActiveRoute, reason);
        return false;
    }

    /// <summary>
    /// Clamps an RPM setpoint to the effective route range (15 to 1000 on Modbus, 15 to 965 on UART).
    /// If rpm == 0, returns 0 (stop/disabled).
    /// </summary>
    public double ClampRpm(double rpm)
    {
        if (rpm <= 0)
        {
            return 0;
        }
        return Math.Clamp(rpm, MinRpm, EffectiveMaxRpm);
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
