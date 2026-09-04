using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Routed shell for the impeller-power acquisition workflow.
/// </summary>
/// <remarks>
/// Step 5 deliberately wires only durable dependencies and live observations. The runner is
/// registered headlessly in the composition root; editing and execution commands arrive in
/// steps 6-7 so this routed shell cannot create a second path around <see cref="ICommandArbiter"/>.
/// </remarks>
public sealed partial class PowerTestViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private bool _disposed;

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);

        TestRootDirectory = store.RootDirectory;
        _device = device;
        _arbiter = arbiter;
        _device.TelemetryReceived += OnTelemetryReceived;
        _arbiter.OwnershipChanged += OnOwnershipChanged;

        RefreshOwnership();
        if (_device.Latest is { } latest)
        {
            OnTelemetryReceived(latest);
        }
    }

    public string TestRootDirectory { get; }

    public string PageStage => "Esqueleto roteado · aquisição na etapa 6";

    [ObservableProperty]
    public partial bool HasServoSample { get; private set; }

    [ObservableProperty]
    public partial double? CurrentRpm { get; private set; }

    [ObservableProperty]
    public partial double? CurrentTorqueNm { get; private set; }

    [ObservableProperty]
    public partial double? CurrentPowerW { get; private set; }

    [ObservableProperty]
    public partial string AgitationOwnerLabel { get; private set; } = "Manual";

    public string LiveSummary => HasServoSample
        ? $"{CurrentRpm:F1} rpm · {CurrentTorqueNm:F4} N·m · {CurrentPowerW:F2} W"
        : "Aguardando telemetria válida do servo";

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        HasServoSample = snapshot.HasServoSample;
        CurrentRpm = snapshot.HasServoSample ? snapshot.ServoRpm : null;
        CurrentTorqueNm = snapshot.HasServoSample ? snapshot.ServoTorqueNm : null;
        CurrentPowerW = snapshot.HasServoSample ? snapshot.ServoPowerW : null;
        OnPropertyChanged(nameof(LiveSummary));
    }

    private void OnOwnershipChanged(OwnershipTransfer _) => RefreshOwnership();

    private void RefreshOwnership()
        => AgitationOwnerLabel = _arbiter.OwnerOf(ActuatorId.Agitation) switch
        {
            CommandOwner.Manual => "Operador",
            CommandOwner.Automatic => "Cascata",
            CommandOwner.Recipe => "Receita",
            CommandOwner.KlaAssay => "Ensaio kLa",
            CommandOwner.PowerAssay => "Ensaio de potência",
            var owner => owner.ToString(),
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.TelemetryReceived -= OnTelemetryReceived;
        _arbiter.OwnershipChanged -= OnOwnershipChanged;
    }
}
