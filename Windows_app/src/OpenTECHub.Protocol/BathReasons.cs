namespace OpenTECHub.Protocol;

/// <summary>Operator text for the bath/cascade reason codes published by Hub 10.6.</summary>
public static class BathReasons
{
    /// <summary>pt-BR text for a Hub reason code; unknown codes are shown literally.</summary>
    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "sem motivo informado";
        if (code.StartsWith("node_rejected:", StringComparison.Ordinal))
            return $"o banho recusou o comando ({code["node_rejected:".Length..]})";
        if (code.StartsWith("bath_error:", StringComparison.Ordinal))
            return $"erro na sequência do C404 ({code["bath_error:".Length..]})";
        return code switch
        {
            "no_reference" => "sem referência do reator",
            "reactor_pv_invalid" or "reactor_pv_stale" => "temperatura do reator (Tempval) inválida",
            "node_offline" => "banho offline",
            "bath_comm_off" => "comunicação do banho desligada",
            "sp_source_shadow" => "nó sem leitura do display (sp_source=0)",
            "bath_manual" => "guarda do C404 em manual",
            "guard_suspended" => "guarda do C404 suspensa",
            "display_sp_invalid" => "SP do display ilegível",
            "bath_aborted" => "sequência do C404 abortada",
            "bath_completion_timeout" => "comando não concluído em 300 s",
            "target_override" => "alvo do C404 alterado por fora",
            "actuator_busy" => "C404 executando comando",
            "same_frame" => "sintonia no mesmo quadro de via/setpoint",
            "out_of_range" => "valores fora da faixa",
            _ => code,
        };
    }
}
