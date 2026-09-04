using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

/// <summary>Routed phase-3 destination for power-map synthesis and comparison.</summary>
public sealed class PowerMapViewModel : ObservableObject
{
    public PowerMapViewModel(IPowerTestStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        TestRootDirectory = store.RootDirectory;
    }

    public string TestRootDirectory { get; }

    public string PhaseLabel => "FASE 3";

    public string StatusMessage =>
        "A rota está pronta. Superfície (N, Qg), flooding e comparação de impelidores entram na Fase 3.";
}
