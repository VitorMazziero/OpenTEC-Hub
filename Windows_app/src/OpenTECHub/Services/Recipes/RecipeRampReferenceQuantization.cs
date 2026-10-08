namespace OpenTECHub.Services.Recipes;

/// <summary>Canonical ramp references for the existing device command representations.</summary>
public static class RecipeRampReferenceQuantization
{
    public static double Quantize(SetpointVariable variable, double reference)
    {
        if (!Enum.IsDefined(variable) || !double.IsFinite(reference))
            throw new ArgumentException("Referência inválida para quantização.");
        var quantized = variable switch
        {
            SetpointVariable.Agitation => Math.Round(reference, MidpointRounding.AwayFromZero),
            // The Hub parses pressureReference with toInt(), then sends an integral UART reference.
            SetpointVariable.Pressure => Math.Truncate(reference),
            // The module receives a two-decimal pH reference (String(pHReference, 2)).
            SetpointVariable.Ph => Math.Round(reference, 2, MidpointRounding.AwayFromZero),
            _ => reference
        };
        if (reference > 0 && quantized == 0)
            throw new ArgumentException("Referência positiva não representável sem desligar o dispositivo.");
        return quantized;
    }
}
