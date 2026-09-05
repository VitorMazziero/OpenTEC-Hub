using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>
/// A manual command send whose outcome the caller can see.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDeviceService.Send"/> returns <c>void</c>. In the application composition
/// root that method is a Manual dispatch through <see cref="CommandArbiter"/>, which
/// refuses the whole frame when a recipe or the cascade owns any actuator it touches — and
/// the <c>void</c> signature throws that refusal away. A view-model that calls it can
/// persist a setpoint and tell the operator it was sent when nothing left the PC.
/// </para>
/// <para>
/// This is the narrow fix: the same Manual dispatch, with the result handed back, so a
/// card can hold its staged value and say who refused it. See <c>docs/CURRENT_STATUS.md</c>
/// AUD-003.
/// </para>
/// </remarks>
public interface IManualDispatcher
{
    /// <summary>Sends <paramref name="command"/> as the operator, reporting whether it left.</summary>
    CommandDispatchResult Dispatch(OpenTECCommand command);

    /// <summary>
    /// The same, but on its own frame after anything already buffered.
    /// </summary>
    /// <remarks>
    /// For the second half of a two-frame sequence, where merging the two would let the
    /// firmware drop the first. See <c>docs/PLANO_DISPOSITIVOS_EXTERNOS.md</c> section 3.5.
    /// </remarks>
    CommandDispatchResult DispatchSeparateFrame(OpenTECCommand command);

    /// <summary>
    /// Current actuator ownership snapshot, if the underlying device service supports arbitration.
    /// </summary>
    IReadOnlyDictionary<ActuatorId, CommandOwner>? Ownership => null;
}

/// <inheritdoc cref="IManualDispatcher"/>
public sealed class ManualDispatcher : IManualDispatcher
{
    private readonly IDeviceService _device;

    public ManualDispatcher(IDeviceService device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>The underlying device service or arbiter.</summary>
    public IDeviceService Device => _device;

    /// <inheritdoc />
    public IReadOnlyDictionary<ActuatorId, CommandOwner>? Ownership => (_device as ICommandArbiter)?.Ownership;

    public CommandDispatchResult Dispatch(OpenTECCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The application's IDeviceService *is* the arbiter, so this is the ordinary path
        // and it carries real ownership feedback. The fallback covers a bare device service
        // — the simulator harness and most view-model tests — where nothing contends for an
        // actuator and every write genuinely does leave.
        if (_device is ICommandArbiter arbiter)
        {
            return arbiter.Dispatch(CommandOwner.Manual, command);
        }

        _device.Send(command);
        return new CommandDispatchResult(true, [], CommandOwner.Manual);
    }

    public CommandDispatchResult DispatchSeparateFrame(OpenTECCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_device is ICommandArbiter arbiter)
        {
            return arbiter.DispatchSeparateFrame(CommandOwner.Manual, command);
        }

        _device.SendAfterCurrentFrame(command);
        return new CommandDispatchResult(true, [], CommandOwner.Manual);
    }
}

/// <summary>pt-BR wording for a refused manual dispatch.</summary>
public static class DispatchRefusal
{
    /// <summary>
    /// One sentence naming what was refused and who holds it, for a card's status line.
    /// </summary>
    public static string Describe(CommandDispatchResult result, IReadOnlyDictionary<ActuatorId, CommandOwner>? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Accepted)
        {
            return "";
        }

        if (result.Refused.Count == 0)
        {
            return "Comando não enviado: nada a aplicar.";
        }

        var refused = string.Join(", ", result.Refused.Select(CommandActuators.Label));

        if (ownership is null)
        {
            return $"Comando recusado: {refused} pertence a outro controlador.";
        }

        var owners = result.Refused
            .Select(a => ownership.TryGetValue(a, out var owner) ? owner : CommandOwner.Manual)
            .Distinct()
            .Select(OwnerLabel)
            .ToArray();

        return $"Comando recusado: {refused} sob controle de {string.Join(" / ", owners)}.";
    }

    /// <summary>
    /// Formats a refusal using the active dispatcher's ownership snapshot.
    /// </summary>
    public static string Describe(CommandDispatchResult result, IManualDispatcher? dispatcher) =>
        Describe(result, dispatcher?.Ownership);

    /// <summary>
    /// Formats a refusal using the device or arbiter's ownership snapshot.
    /// </summary>
    public static string Describe(CommandDispatchResult result, IDeviceService? device) =>
        Describe(result, (device as ICommandArbiter)?.Ownership);

    private static string OwnerLabel(CommandOwner owner) => owner switch
    {
        CommandOwner.Automatic => "Controle O₂",
        CommandOwner.Recipe => "Receita",
        CommandOwner.KlaAssay => "Ensaio de kLa",
        CommandOwner.PowerAssay => "Ensaio de Potência",
        _ => "Operador",
    };
}
