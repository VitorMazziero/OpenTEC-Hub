namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Reads the live controller reference, independently of the device's oxygen monitor.</summary>
    public bool TryReadCascadeOxygenReference(string nodeId, out double reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        lock (_lock)
        {
            if (_liveCascades.TryGetValue(nodeId, out var controller))
            {
                reference = controller.OxygenSetpoint;
                return true;
            }
            reference = default;
            return false;
        }
    }

    /// <summary>Changes only the active cascade reference, under its assay handoff barrier.</summary>
    public bool TrySetCascadeOxygenReference(string nodeId, double reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!double.IsFinite(reference) || !DeviceRanges.Accepts(SetpointVariable.Oxygen, reference))
            throw new ArgumentOutOfRangeException(nameof(reference));
        lock (_lock)
        {
            if (State != RecipeRunState.Running) return false;
            using var step = _cascadeGates.TryGetValue(nodeId, out var gate) ? gate.TryEnterStep() : null;
            if (step is null || !_liveCascades.TryGetValue(nodeId, out var controller)) return false;
            controller.OxygenSetpoint = reference;
            return true;
        }
    }
}
