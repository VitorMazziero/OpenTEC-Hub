namespace OpenTECHub.Protocol;

/// <summary>The six parameters of the pump's pulse dosing (pump 3.13).</summary>
/// <param name="VolumeMl">Volume per pulse, mL.</param>
/// <param name="PeriodS">Time between pulse starts, s.</param>
/// <param name="Speed">Motor speed S during the pulse, 1..1000.</param>
/// <param name="RateMlMin">Flow measured at <paramref name="Speed"/>, mL/min (the dose point).</param>
/// <param name="TotalMl">Stop after this volume; 0 = no limit.</param>
/// <param name="DeadMs">Start-up dead time t₀ compensated on each pulse, ms (0..2000).</param>
public sealed record PumpDoseParameters(
    double VolumeMl,
    double PeriodS,
    double Speed,
    double RateMlMin,
    double TotalMl,
    double DeadMs);

/// <summary>
/// Exact mirror of the pump firmware's <c>control/DoseMath.h</c>: the same limits and the same
/// refusal codes the node publishes as <c>rej_err</c>.
/// </summary>
/// <remarks>
/// The app validates before sending so the operator sees the reason at once; the node
/// validates again and refuses the whole frame if anything disagrees.
/// </remarks>
public static class PumpDoseRules
{
    public const double SpeedMin = 1.0;
    public const double SpeedMax = 1000.0;
    public const double DeadMaxMs = 2000.0;

    /// <summary>Delivering part of a pulse (without t₀), below which the start-up error dominates.</summary>
    public const double MinOnMs = 200.0;

    /// <summary>Minimum gap between the end of a pulse and the start of the next.</summary>
    public const double GapMs = 500.0;

    public const double MaxVolumeMl = 100.0;
    public const double MinPeriodS = 2.0;
    public const double MaxPeriodS = 86400.0;
    public const double MaxRateMlMin = 1000.0;

    /// <summary>Motor time that delivers <paramref name="volumeMl"/>, without t₀.</summary>
    public static double DeliveryMs(double volumeMl, double rateMlMin) => volumeMl / rateMlMin * 60000.0;

    /// <summary>Whole pulse length, with t₀.</summary>
    public static double PulseMs(double volumeMl, double rateMlMin, double deadMs) => DeliveryMs(volumeMl, rateMlMin) + deadMs;

    /// <summary>Volume delivered by <paramref name="motorMs"/> of motor time.</summary>
    public static double CountedMl(double rateMlMin, double motorMs, double deadMs)
    {
        var useful = motorMs - deadMs;
        return useful > 0.0 ? rateMlMin * useful / 60000.0 : 0.0;
    }

    /// <summary>Null when valid; otherwise the node's <c>rej_err</c> code.</summary>
    public static string? Validate(PumpDoseParameters p)
    {
        ArgumentNullException.ThrowIfNull(p);

        // The firmware works in float; the borders are compared the same way here.
        float volume = (float)p.VolumeMl, period = (float)p.PeriodS, speed = (float)p.Speed;
        float rate = (float)p.RateMlMin, total = (float)p.TotalMl, dead = (float)p.DeadMs;

        if (!float.IsFinite(volume) || volume <= 0f || volume > (float)MaxVolumeMl) return "dose_ml";
        if (!float.IsFinite(period) || period < (float)MinPeriodS || period > (float)MaxPeriodS) return "dose_period";
        if (!float.IsFinite(speed) || speed < (float)SpeedMin || speed > (float)SpeedMax) return "dose_speed";
        if (!float.IsFinite(rate) || rate <= 0f || rate > (float)MaxRateMlMin) return "dose_rate";
        if (!float.IsFinite(total) || total < 0f) return "dose_total";
        if (!float.IsFinite(dead) || dead < 0f || dead > (float)DeadMaxMs) return "dose_dead";
        var deliveryMs = volume / rate * 60000.0f;
        if (deliveryMs < (float)MinOnMs) return "dose_short";
        if (deliveryMs + dead + (float)GapMs > period * 1000.0f) return "dose_fit";
        return null;
    }

    /// <summary>Operator text for a node refusal code (<c>PumpRejErr</c>).</summary>
    public static string DescribeRefusal(string? code) => code switch
    {
        null or "" => "",
        "mode_range" => "modo de perfil fora de 0..5",
        "seg_range" => "número de segmentos fora de 2..100",
        "cal_busy" => "a curva não pode mudar com um perfil em execução",
        "cal_incomplete" => "a curva exige os nove coeficientes no mesmo quadro",
        "cal_st_range" => "velocidade de transição fora de (0, 1000)",
        "cal_shape" => "a curva precisa ser contínua em St (valor e derivada), não negativa e crescente",
        "dose_ml" => "volume por pulso fora de (0, 100] mL",
        "dose_period" => "período fora de [2, 86 400] s",
        "dose_speed" => "velocidade do pulso fora de [1, 1000]",
        "dose_rate" => "falta a vazão medida no ponto de dosagem",
        "dose_total" => "volume total negativo",
        "dose_dead" => "atraso de partida fora de [0, 2000] ms",
        "dose_short" => "pulso menor que 0,2 s: aumente o volume por pulso ou use uma velocidade menor",
        "dose_fit" => "o pulso não cabe no período (precisa de 0,5 s de folga)",
        "dose_busy" => "há um perfil contínuo em execução; pare-o antes de dosar",
        "dose_range" => "comando de dosagem diferente de iniciar/parar",
        _ => code,
    };
}

/// <summary>Who drives the pump motor, as the node publishes it (<c>PumpDrive</c>, pump 3.13).</summary>
public enum PumpDriveState
{
    Stopped = 0,
    Potentiometers = 1,
    ManualSpeed = 2,
    ProfileWaiting = 3,
    ProfileRunning = 4,
    DoseIdle = 5,
    DosePulse = 6,
    Ota = 7,
}

/// <summary>Operator text for the pump's drive state and reset reason (pump 3.13).</summary>
public static class PumpNodeText
{
    public static string Drive(int? drive) => drive switch
    {
        (int)PumpDriveState.Stopped => "Parada",
        (int)PumpDriveState.Potentiometers => "Potenciômetros de bancada",
        (int)PumpDriveState.ManualSpeed => "Velocidade manual",
        (int)PumpDriveState.ProfileWaiting => "Aguardando janela",
        (int)PumpDriveState.ProfileRunning => "Perfil em execução",
        (int)PumpDriveState.DoseIdle => "Dosando (entre pulsos)",
        (int)PumpDriveState.DosePulse => "Dosando (pulso)",
        (int)PumpDriveState.Ota => "Gravando firmware",
        null => "—",
        _ => $"Estado {drive}",
    };

    /// <summary>ESP-IDF <c>esp_reset_reason_t</c>.</summary>
    public static string ResetReason(int? reason) => reason switch
    {
        1 => "ligada",
        3 => "reinício por software",
        4 => "exceção",
        5 => "watchdog de interrupção",
        6 => "watchdog de tarefa",
        7 => "watchdog",
        8 => "saída de sono profundo",
        9 => "queda de tensão",
        null => "motivo desconhecido",
        _ => $"motivo {reason}",
    };
}
