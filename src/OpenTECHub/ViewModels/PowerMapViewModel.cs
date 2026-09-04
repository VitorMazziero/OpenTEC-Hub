using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

/// <summary>Routed phase-3 destination for power-map synthesis and comparison.</summary>
public sealed class PowerMapViewModel : ObservableObject
{
    public PowerMapViewModel(IPowerTestStore testStore, IPowerMapStore? mapStore = null)
    {
        ArgumentNullException.ThrowIfNull(testStore);
        TestRootDirectory = testStore.RootDirectory;
        MapRootDirectory = mapStore?.RootDirectory ?? "";
    }

    public string TestRootDirectory { get; }

    public string MapRootDirectory { get; }

    public string PhaseLabel => "FASE 3";

    public string StatusMessage =>
        "A rota está pronta. Superfície (N, Qg), flooding e comparação de impelidores entram na Fase 3.";
}
