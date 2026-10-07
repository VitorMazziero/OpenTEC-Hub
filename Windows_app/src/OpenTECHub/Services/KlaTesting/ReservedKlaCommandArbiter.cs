using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Routes the common runner's commands through its recipe reservation, without spoofing ownership.</summary>
internal sealed class ReservedKlaCommandArbiter(ICommandArbiter arbiter, RecipeAssayResourceLease lease) : ICommandArbiter
{
    private readonly object _dispatchGate = new();
    private bool _sealed;
    public void Seal() { lock (_dispatchGate) _sealed = true; }
    public IReadOnlyDictionary<ActuatorId, CommandOwner> Ownership => arbiter.Ownership;
    public IReadOnlyList<CommandLifecycleEntry> Lifecycle => arbiter.Lifecycle;
    public CommandOwner OwnerOf(ActuatorId actuator) => arbiter.OwnerOf(actuator);
    public CommandDispatchResult Dispatch(CommandOwner requester, OpenTECCommand command) => Send(requester, command, false);
    public CommandDispatchResult DispatchSeparateFrame(CommandOwner requester, OpenTECCommand command) => Send(requester, command, true);
    private CommandDispatchResult Send(CommandOwner requester, OpenTECCommand command, bool separate)
    {
        lock (_dispatchGate)
        {
            if (_sealed || requester != CommandOwner.KlaAssay || command.Keys.Any(key => CommandActuators.ForKey(key) is null))
                return new(false, CommandActuators.ActuatorsIn(command).ToArray(), requester);
            return lease.DispatchAssay(command, separate);
        }
    }
    public OwnershipTransfer Claim(CommandOwner owner, IReadOnlyList<ActuatorId> actuators, string reason) => throw TransferRefused();
    public OwnershipTransfer Release(CommandOwner owner, string reason) => throw TransferRefused();
    public OwnershipTransfer ReturnToManual(string reason, bool isSafeAbort = false) => throw TransferRefused();
    private static InvalidOperationException TransferRefused() => new("Cessão de receita pertence ao coordenador de recursos.");
    public CommandDispatchResult DispatchSafety(OpenTECCommand command, string reason, bool returnToManual = true)
        => arbiter.DispatchSafety(command, reason, returnToManual);
    public CommandDispatchResult DispatchSeparateSafetyFrame(OpenTECCommand command, string reason, bool returnToManual = true)
        => arbiter.DispatchSeparateSafetyFrame(command, reason, returnToManual);
    public event Action<OwnershipTransfer>? OwnershipChanged { add => arbiter.OwnershipChanged += value; remove => arbiter.OwnershipChanged -= value; }
    public event Action<CommandRejection>? CommandRejected { add => arbiter.CommandRejected += value; remove => arbiter.CommandRejected -= value; }
    public event Action<CommandLifecycleEntry>? CommandTracked { add => arbiter.CommandTracked += value; remove => arbiter.CommandTracked -= value; }
    public event Action<OwnershipTransfer>? OwnershipRevoked { add => arbiter.OwnershipRevoked += value; remove => arbiter.OwnershipRevoked -= value; }
}
