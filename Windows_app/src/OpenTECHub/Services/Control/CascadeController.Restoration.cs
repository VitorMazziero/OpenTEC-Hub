using System.Text.Json;
using OpenTECHub.Services.KlaMapping;

namespace OpenTECHub.Services.Control;

public sealed partial class CascadeController
{
    /// <summary>Restore a complete captured controller while its update/actuation gate is closed.</summary>
    public void RestoreStateJson(string json)
        => PrepareStateRestoration(json)();

    /// <summary>Validate without mutation so a coordinated return can accept its device frame first.
    /// Invoke the returned operation while the controller computation gate remains closed.</summary>
    internal Action PrepareStateRestoration(string json)
    {
        using var document = JsonDocument.Parse(json);
        var options = new JsonSerializerOptions { RespectRequiredConstructorParameters = true };
        var pid = document.RootElement.GetProperty("Pid").Deserialize<CascadePidState>(options)
            ?? throw new ArgumentException("Snapshot sem PID.");
        var configuration = document.RootElement.GetProperty("Allocation");
        var allocation = ParseAllocation(configuration, options);
        var validated = new CascadeTwoLoopPidController(pid.Tuning, pid.Setpoint);
        validated.RestoreState(pid);
        var frozen = validated.CaptureState();
        return () =>
        {
            _pid.RestoreState(frozen);
            _allocation = allocation;
        };
    }

    private static CascadeAllocation ParseAllocation(JsonElement configuration, JsonSerializerOptions options)
    {
        ActuatorWindow Window(JsonElement element, string expectedName)
        {
            var window = element.Deserialize<ActuatorWindow>(options) ?? throw new ArgumentException("Janela ausente.");
            if (window.Name != expectedName || window.Validate().Count != 0 || window.Min < 0)
                throw new ArgumentException("Janela do atuador inválida.");
            return window;
        }
        switch (configuration.GetProperty("Kind").GetString())
        {
            case nameof(WindowAllocation):
                var windows = configuration.GetProperty("Windows");
                if (windows.GetArrayLength() != 2) throw new ArgumentException("Alocação requer duas janelas.");
                return new WindowAllocation(Window(windows[0], AgitationActuator), Window(windows[1], AerationActuator));
            case nameof(SingleActuatorAllocation):
                var agitation = configuration.GetProperty("DriveAgitation").GetBoolean();
                var driven = Window(configuration.GetProperty("Driven"), agitation ? AgitationActuator : AerationActuator);
                var heldMotor = configuration.GetProperty("HeldAgitationRpm").GetDouble();
                var heldFlow = configuration.GetProperty("HeldAerationLpm").GetDouble();
                if (!double.IsFinite(heldMotor) || !double.IsFinite(heldFlow) || heldMotor < 0 || heldFlow < 0 ||
                    driven.EffortStart != 0 || driven.EffortEnd != 100 ||
                    agitation && heldMotor != 0 || !agitation && heldFlow != 0)
                    throw new ArgumentException("Alocação simples inválida.");
                return agitation ? SingleActuatorAllocation.Agitation(driven.Min, driven.Max, heldFlow)
                    : SingleActuatorAllocation.Aeration(driven.Min, driven.Max, heldMotor);
            case nameof(KlaPathAllocation):
                var table = configuration.GetProperty("Table").Deserialize<KlaAllocationSample[]>(options)
                    ?? throw new ArgumentException("Trajetória de kLa ausente.");
                if (table.Length < 2 || table.Any(sample => sample is null || !double.IsFinite(sample.KlaPerHour) ||
                    !double.IsFinite(sample.AirflowLpm) || !double.IsFinite(sample.AgitationRpm) ||
                    sample.KlaPerHour < 0 || sample.AirflowLpm < 0 || sample.AgitationRpm < 0) ||
                    table.Zip(table.Skip(1)).Any(pair => pair.First.KlaPerHour >= pair.Second.KlaPerHour))
                    throw new ArgumentException("Trajetória de kLa inválida.");
                return new KlaPathAllocation(table);
            default: throw new ArgumentException("Tipo de alocação desconhecido.");
        }
    }
}
