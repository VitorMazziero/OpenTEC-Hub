using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenTECHub.ViewModels;

public sealed partial class ReceitasViewModel
{
    public bool HasKlaApplicationHost => _klaHost is not null;
    public bool CanConfigureKlaAutomation => !IsRunning && !IsKlaConfigurationBusy && _klaHost?.Context is not null;
    public string KlaInstallationId => _klaHost?.Context?.InstallationId ?? "Indisponível";
    public string KlaAutomationAvailability => _klaHost is null ? "Serviço não disponível" :
        _klaHost.AvailabilityError ?? (!_klaHost.Profiles.IsIsolatedEnvironment
            ? "Ensaios automáticos físicos aguardam qualificação da instalação."
            : $"Simulação isolada · {_klaHost.Profiles.AvailableProfiles.Count} perfil(is) disponível(is)");
    public string KlaActiveCultivation => $"Cultivo ativo: {_klaHost?.Context?.CultivationId ?? "não identificado"}";

    [ObservableProperty]
    public partial string KlaCultivationId { get; set; } = "";
    [ObservableProperty]
    public partial string KlaConfigurationMessage { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigureKlaAutomation))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveKlaCultivationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportKlaOperationalProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadKlaOperationalProfilesCommand))]
    public partial bool IsKlaConfigurationBusy { get; set; }

    [RelayCommand(CanExecute = nameof(CanConfigureKlaAutomation))]
    private async Task SaveKlaCultivation()
    {
        if (!CanConfigureKlaAutomation) return;
        IsKlaConfigurationBusy = true;
        try
        {
            await _klaHost!.Context!.SetCultivationAsync(KlaCultivationId);
            KlaCultivationId = _klaHost.Context.CultivationId ?? "";
            OnPropertyChanged(nameof(KlaActiveCultivation));
            KlaConfigurationMessage = "Identificação do cultivo gravada. Os limites cumulativos usam essa identificação.";
        }
        catch (Exception error) { KlaConfigurationMessage = $"Cultivo não alterado: {error.Message}"; }
        finally { IsKlaConfigurationBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureKlaAutomation))]
    private async Task ImportKlaOperationalProfile()
    {
        if (!CanConfigureKlaAutomation || _files is null) return;
        var path = _files.ChooseOpenPath("Importar perfil qualificado de ensaio", "Perfil (*.json)|*.json", ".json");
        if (path is null) return;
        IsKlaConfigurationBusy = true;
        try
        {
            await _klaHost!.ImportProfileAsync(path);
            RefreshKlaProfileSelectors();
            KlaConfigurationMessage = "Perfil importado. Selecione-o explicitamente no bloco de kLa.";
        }
        catch (Exception error) { KlaConfigurationMessage = $"Perfil não importado: {error.Message}"; }
        finally { IsKlaConfigurationBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureKlaAutomation))]
    private void ReloadKlaOperationalProfiles()
    {
        if (!CanConfigureKlaAutomation) return;
        _klaHost!.ReloadProfiles();
        RefreshKlaProfileSelectors();
        KlaConfigurationMessage = _klaHost.AvailabilityError ?? "Catálogo de perfis atualizado.";
    }

    private void RefreshKlaProfileSelectors()
    {
        foreach (var node in Tabs.SelectMany(tab => tab.Nodes)) node.RefreshOperationalProfiles();
        OnPropertyChanged(nameof(KlaAutomationAvailability));
    }
}
