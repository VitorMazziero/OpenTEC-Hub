using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

/// <summary>Direct ramp destinations. Rejects route clipping; does not dispatch or claim confirmation.</summary>
public sealed class RecipeRampDirectCommands(double maximumFlow, bool uartFallback, GasRigConfiguration gasRig,
    double phInactiveBand)
{
    public double Quantize(SetpointVariable variable, double reference)
    {
        Validate(variable, reference);
        // The common motor wire uses integral rpm. Other builders transmit engineering-unit doubles.
        return variable == SetpointVariable.Agitation ? Math.Round(reference, MidpointRounding.AwayFromZero) : reference;
    }

    public OpenTECCommand Build(LinearRampSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var value = Quantize(sample.Variable, sample.Reference);
        if (sample.Variable == SetpointVariable.Oxygen)
            throw new ArgumentException("Referência da cascata requer destino do controlador, não comando de monitor.");
        if (sample.Variable != SetpointVariable.Oxygen && sample.OxygenTarget is not null)
            throw new ArgumentException("Destino de O₂ incompatível com o parâmetro.");
        return sample.Variable switch
        {
            SetpointVariable.Temperature => OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, value),
            SetpointVariable.Agitation => CommandBuilders.MotorSetpoint((int)value),
            SetpointVariable.Flow => CommandBuilders.FlowRoute(value, maximumFlow,
                value > 0 ? GasRoute.Reactor : GasRoute.Closed, gasRig),
            SetpointVariable.Pressure => OpenTECCommand.Create().Set(CommandKeys.PressureReference, value),
            SetpointVariable.Ph => OpenTECCommand.Create().Set(CommandKeys.PHSetpoint, value).Set(CommandKeys.PHError, phInactiveBand),
            _ => throw new ArgumentException("Destino desconhecido.")
        };
    }

    private void Validate(SetpointVariable variable, double reference)
    {
        if (!Enum.IsDefined(variable) || !double.IsFinite(reference) || !DeviceRanges.Accepts(variable, reference))
            throw new ArgumentException("Referência fora do envelope do dispositivo.");
        if (!double.IsFinite(maximumFlow) || maximumFlow <= 0 ||
            !double.IsFinite(phInactiveBand) || phInactiveBand < 0)
            throw new ArgumentException("Limites do destino inválidos.");
        if (variable == SetpointVariable.Flow && reference > maximumFlow ||
            variable == SetpointVariable.Agitation && uartFallback && reference > MotorRouteCoordinator.UartFallbackMaxRpm)
            throw new ArgumentException("Alvo não alcançável pela rota/configuração atual.");
    }
}
