using System.Collections.ObjectModel;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.ViewModels;

public sealed record RecipeOperationalProfileChoice(string ProfileId, string Version, KlaAssayProtocol Protocol)
{
    public string Label => $"{ProfileId} · {Version}";
}

public sealed partial class RecipeNodeViewModel
{
    public bool IsKlaAssay => Type == NodeType.KlaAssay;
    public ObservableCollection<RecipeOperationalProfileChoice> AvailableOperationalProfiles { get; } = [];
    private RecipeOperationalProfileChoice? _selectedOperationalProfile;
    private bool _refreshingOperationalProfiles;

    public RecipeOperationalProfileChoice? SelectedOperationalProfile
    {
        get => _selectedOperationalProfile;
        set
        {
            if (_refreshingOperationalProfiles || !IsKlaAssay) return;
            if (value is null) return; // Rebuilding a selector must preserve the stored recipe identity.
            var profile = FindCurrentProfile(value);
            if (profile is null || value.Protocol.ToString() != Model.Text("protocol"))
            {
                RefreshOperationalProfiles();
                return;
            }
            Model.Set("profileId", value.ProfileId); Model.Set("profileVersion", value.Version);
            SetProperty(ref _selectedOperationalProfile, value);
            OnFieldChanged();
        }
    }

    public string OperationalProfileStatus
    {
        get
        {
            var profile = _selectedOperationalProfile is { } choice ? FindCurrentProfile(choice) : null;
            if (profile is null)
                return string.IsNullOrWhiteSpace(Model.Text("profileId"))
                    ? "Selecione um perfil qualificado. Nenhum perfil é escolhido automaticamente."
                    : $"Perfil {Model.Text("profileId")} · {Model.Text("profileVersion")} indisponível ou vencido para este protocolo.";
            if (profile.Capabilities.ProfileId == KlaRecipeOperatorProfile.Id)
                return "Perfil do operador: remoção, alvo de OD, tempos máximos e agitação vêm das configurações atuais da " +
                    "página Determinar kLa e são gravados na sessão no início do ensaio.";
            var limits = profile.MaximumRetry;
            return $"Perfil de simulação · válido até {profile.ValidUntilUtc.LocalDateTime:g}. " +
                $"Limites: {limits.MaximumAttemptsPerReplicate} tentativas por réplica; {limits.MaximumAttemptsPerCultivation} no cultivo; " +
                $"{limits.MaximumBlockSeconds:0.##} s por bloco; exposição {limits.MaximumGasOffSecondsPerAttempt:0.##} s por tentativa e " +
                $"{limits.MaximumCumulativeGasOffSecondsPerCultivation:0.##} s no cultivo; intervalo mínimo {limits.MinimumInterAssaySeconds:0.##} s. " +
                "Configure os limites da receita dentro desses valores.";
        }
    }

    private KlaRecipeOperationalProfile? FindCurrentProfile(RecipeOperationalProfileChoice choice)
        => _operationalProfiles?.AvailableProfiles.FirstOrDefault(p => p.Capabilities.ProfileId == choice.ProfileId &&
            p.Capabilities.ProfileVersion == choice.Version && p.Template.Protocol == choice.Protocol);

    public void RefreshOperationalProfiles()
    {
        if (!IsKlaAssay) return;
        _refreshingOperationalProfiles = true;
        try
        {
            AvailableOperationalProfiles.Clear();
            foreach (var profile in _operationalProfiles?.AvailableProfiles ?? [])
                if (profile.Template.Protocol.ToString() == Model.Text("protocol"))
                    AvailableOperationalProfiles.Add(new(profile.Capabilities.ProfileId, profile.Capabilities.ProfileVersion, profile.Template.Protocol));
            _selectedOperationalProfile = AvailableOperationalProfiles.FirstOrDefault(p =>
                p.ProfileId == Model.Text("profileId") && p.Version == Model.Text("profileVersion"));
            OnPropertyChanged(nameof(SelectedOperationalProfile));
            OnPropertyChanged(nameof(OperationalProfileStatus));
        }
        finally { _refreshingOperationalProfiles = false; }
    }
}
