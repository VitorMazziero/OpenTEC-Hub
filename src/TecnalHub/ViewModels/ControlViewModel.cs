using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>One row in Controle's all-setpoints table.</summary>
public sealed partial class ControlParameterRowViewModel : ObservableObject
{
    public ControlParameterRowViewModel(SubsystemViewModel subsystem, string iconKey)
    {
        Subsystem = subsystem;
        IconKey = iconKey;
        SelectedMode = ModeOptions[0];
        SelectedOxygenMode = OxygenModeOptions[0];
    }

    public SubsystemViewModel Subsystem { get; }

    public string IconKey { get; }

    public bool IsOxygenRow => IconKey == "Oxygen";

    public IReadOnlyList<CommandOwnerOption> ModeOptions { get; } =
    [
        new(CommandOwner.Manual, "Manual", null),
        new(CommandOwner.Automatic, "Automático", "A cascata chega na Fase 2."),
        new(CommandOwner.Recipe, "Receita", "O motor de receitas chega na Fase 3."),
    ];

    public IReadOnlyList<string> OxygenModeOptions { get; } =
    [
        "Agitação",
        "Aeração",
        "Cascata",
        "Mapa"
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerText))]
    public partial CommandOwnerOption SelectedMode { get; set; }

    [ObservableProperty]
    public partial string SelectedOxygenMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveActive))]
    public partial bool IsOverriddenByCascade { get; set; }

    public string OwnerText => SelectedMode.Owner switch
    {
        CommandOwner.Automatic => "Cascata",
        CommandOwner.Recipe => "Receita",
        _ => "Operador",
    };

    // ── Oxygen-cascade engagement (wired by ControlViewModel for the oxygen row) ──

    /// <summary>Reads live cascade engagement; supplied by the owner for the oxygen row.</summary>
    internal Func<bool>? CascadeEngagedGetter { get; set; }

    /// <summary>Requests engage (true) or disengage (false); supplied by the owner for the oxygen row.</summary>
    internal Action<bool>? CascadeEngageRequested { get; set; }

    /// <summary>The selected oxygen mode as a <see cref="CascadeMode"/>.</summary>
    public CascadeMode SelectedOxygenCascadeMode => SelectedOxygenMode switch
    {
        "Agitação" => CascadeMode.AgitationOnly,
        "Aeração" => CascadeMode.AerationOnly,
        "Mapa" => CascadeMode.KlaPath,
        _ => CascadeMode.DualCascade,
    };

    /// <summary>
    /// The oxygen row's "Ativo" state: engaging the cascade. Toggling requests engage/disengage
    /// through the owner, which reverts this on a refusal (offline, or map mode with no map).
    /// </summary>
    public bool IsCascadeEngaged
    {
        get => CascadeEngagedGetter?.Invoke() ?? false;
        set => CascadeEngageRequested?.Invoke(value);
    }

    /// <summary>The mode may only change while the cascade is not engaged.</summary>
    public bool CanEditOxygenMode => !IsCascadeEngaged;

    /// <summary>
    /// A non-oxygen row's "Ativo" state: its subsystem enable, forced on (and locked) while the
    /// oxygen cascade drives this actuator, so the operator sees it running under the cascade.
    /// </summary>
    public bool EffectiveActive
    {
        get => IsOverriddenByCascade || Subsystem.IsEnabled;
        set => Subsystem.IsEnabled = value;
    }

    /// <summary>Re-reads the engagement-derived states after a cascade or enable change.</summary>
    public void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(EffectiveActive));
        OnPropertyChanged(nameof(IsCascadeEngaged));
        OnPropertyChanged(nameof(CanEditOxygenMode));
    }
}

/// <summary>
/// The Phase 1 Controle page: verify, stage and send the whole core loop from one screen.
/// </summary>
public sealed partial class ControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ICascadeService _cascade;
    private readonly SubsystemViewModel _flowSubsystem;

    public ControlViewModel(
        IReadOnlyList<SubsystemViewModel> subsystems,
        FlowControlViewModel flowControl,
        PHControlViewModel phControl,
        NutrientControlViewModel nutrientControl,
        AntifoamControlViewModel antifoamControl,
        FoamControlViewModel foamControl,
        FlaskAgitatorViewModel flaskAgitator,
        IDeviceService device,
        ISettingsService settings,
        IDialogService dialogs,
        ICascadeService cascade,
        ReceitasViewModel? receitas = null)
    {
        if (subsystems.Count != 5)
        {
            throw new ArgumentException("The Phase 1 control page requires five core subsystems.", nameof(subsystems));
        }

        _device = device;
        _settings = settings;
        _dialogs = dialogs;
        _cascade = cascade;
        FlowControl = flowControl;
        PHControl = phControl;
        NutrientControl = nutrientControl;
        AntifoamControl = antifoamControl;
        FoamControl = foamControl;
        FlaskAgitator = flaskAgitator;
        Tuning = new CascadeTuningViewModel(cascade, settings, receitas);

        Rows =
        [
            new(subsystems[0], "Temperature"),
            new(subsystems[1], "Impeller"),
            new(subsystems[2], "Oxygen"),
            new(subsystems[3], "Airflow"),
            new(subsystems[4], "Pressure"),
        ];
        _flowSubsystem = subsystems[3];

        foreach (var row in Rows)
        {
            row.Subsystem.PropertyChanged += OnStagedStateChanged;
            if (row.IsOxygenRow)
            {
                row.PropertyChanged += OnOxygenRowPropertyChanged;
            }
        }

        // The oxygen row's "Ativo" toggle is the single activation point for the cascade:
        // turning it on selects the mode and engages live actuation.
        var oxygenRow = Rows[2];
        oxygenRow.CascadeEngagedGetter = () => _cascade.IsEngaged;
        oxygenRow.CascadeEngageRequested = OnOxygenEngageRequested;
        _cascade.Updated += OnCascadeUpdated;

        FlowControl.PropertyChanged += OnFlowStateChanged;
        PHControl.PropertyChanged += OnPHStateChanged;
        NutrientControl.PropertyChanged += OnDosingStateChanged;
        AntifoamControl.PropertyChanged += OnDosingStateChanged;
        FoamControl.PropertyChanged += OnDosingStateChanged;
        FlaskAgitator.PropertyChanged += OnDosingStateChanged;

        foreach (var preset in settings.Current.SetpointPresets
                     .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Presets.Add(preset);
        }

        SelectedPreset = Presets.FirstOrDefault();
        RefreshState();
        OnCascadeUpdated();
    }

    public IReadOnlyList<ControlParameterRowViewModel> Rows { get; }

    public FlowControlViewModel FlowControl { get; }

    /// <summary>Full five-field pH state; separate from probe calibration.</summary>
    public PHControlViewModel PHControl { get; }

    /// <summary>Nutrient dosing card (WP7).</summary>
    public NutrientControlViewModel NutrientControl { get; }

    /// <summary>Antifoam dosing card (WP7).</summary>
    public AntifoamControlViewModel AntifoamControl { get; }

    /// <summary>Level/foam sensor configuration card (WP7). Applied on its own, not in bulk.</summary>
    public FoamControlViewModel FoamControl { get; }

    /// <summary>Separate flask-agitator card (WP7).</summary>
    public FlaskAgitatorViewModel FlaskAgitator { get; }

    public SubsystemViewModel FlowSubsystem => _flowSubsystem;

    /// <summary>Tab 2: the oxygen-cascade tuning workspace.</summary>
    public CascadeTuningViewModel Tuning { get; }

    public ObservableCollection<SetpointPreset> Presets { get; } = [];

    [ObservableProperty]
    public partial SetpointPreset? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial string PresetName { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public int DirtyCount
        => Rows.Count(row => row.Subsystem.HasPendingChange) +
           (FlowControl.HasPendingChange ? 1 : 0) +
           (PHControl.HasPendingChange ? 1 : 0) +
           (NutrientControl.HasPendingChange ? 1 : 0) +
           (AntifoamControl.HasPendingChange ? 1 : 0) +
           (FlaskAgitator.HasPendingChange ? 1 : 0);

    public string ApplyAllLabel => $"Aplicar alterações ({DirtyCount})";

    public string? FlowRequestError
    {
        get
        {
            if (!FlowControl.IsValid)
            {
                return FlowControl.ValidationError;
            }

            if (!_flowSubsystem.IsEnabled)
            {
                return null;
            }

            if (!_flowSubsystem.TryGetStagedValue(out var setpoint))
            {
                return _flowSubsystem.ValidationError;
            }

            if (FlowControl.TryGetStagedMaxFlow(out var maximum) && setpoint > maximum)
            {
                return $"O novo SP de vazão ({setpoint:G}) excede maxFlow ({maximum:G}).";
            }

            return null;
        }
    }

    public bool CanApplyAll
        => DirtyCount > 0 && DirtyRowsAreValid() &&
           (!PHControl.HasPendingChange || PHControl.CanApply) &&
           (!NutrientControl.HasPendingChange || NutrientControl.CanApply) &&
           (!AntifoamControl.HasPendingChange || AntifoamControl.CanApply) &&
           (!FlaskAgitator.HasPendingChange || FlaskAgitator.CanApply) &&
           FlowRequestError is null;

    public bool CanApplyFlowState
        => (_flowSubsystem.HasPendingChange || FlowControl.HasPendingChange) &&
           (!_flowSubsystem.IsEnabled || _flowSubsystem.IsValid) &&
           FlowRequestError is null;

    [RelayCommand(CanExecute = nameof(CanApplyAll))]
    private void ApplyAll()
    {
        var dirtyRows = Rows.Where(row => row.Subsystem.HasPendingChange).ToArray();
        var flowWasDirty = _flowSubsystem.HasPendingChange || FlowControl.HasPendingChange;
        var phWasDirty = PHControl.HasPendingChange;
        var nutrientWasDirty = NutrientControl.HasPendingChange;
        var antifoamWasDirty = AntifoamControl.HasPendingChange;
        var agitatorWasDirty = FlaskAgitator.HasPendingChange;

        if (!TryBuildCombinedCommand(out var command))
        {
            StatusText = "Revise os campos destacados antes de aplicar.";
            return;
        }

        _device.Send(command);

        foreach (var row in dirtyRows)
        {
            row.Subsystem.CommitPendingCommand();
        }

        if (flowWasDirty)
        {
            FlowControl.CommitRequested(_flowSubsystem.IsEnabled);
        }

        if (phWasDirty)
        {
            PHControl.CommitPendingCommand();
        }

        if (nutrientWasDirty)
        {
            NutrientControl.CommitPendingCommand();
        }

        if (antifoamWasDirty)
        {
            AntifoamControl.CommitPendingCommand();
        }

        if (agitatorWasDirty)
        {
            FlaskAgitator.CommitPendingCommand();
        }

        PersistAppliedSetpoints();
        StatusText = command.Count == 1
            ? "1 campo enviado em um único comando."
            : $"{command.Count} campos enviados em um único comando.";
        RefreshState();
    }

    [RelayCommand(CanExecute = nameof(CanApplyFlowState))]
    private void ApplyFlowState()
    {
        var flowRowWasDirty = _flowSubsystem.HasPendingChange;
        var setpoint = 0.0;
        if (_flowSubsystem.IsEnabled && !_flowSubsystem.TryGetStagedValue(out setpoint))
        {
            return;
        }

        if (!FlowControl.TryBuildRequested(setpoint, _flowSubsystem.IsEnabled, out var command))
        {
            StatusText = "Revise o SP e o limite de vazão antes de aplicar.";
            return;
        }

        _device.Send(command);
        if (flowRowWasDirty)
        {
            _flowSubsystem.CommitPendingCommand();
        }

        FlowControl.CommitRequested(_flowSubsystem.IsEnabled);
        PersistAppliedSetpoints();
        StatusText = "Estado de vazão e válvulas enviado.";
        RefreshState();
    }

    [RelayCommand]
    private void RevertAll()
    {
        foreach (var row in Rows)
        {
            row.Subsystem.RevertCommand.Execute(null);
        }

        FlowControl.Revert();
        PHControl.RevertCommand.Execute(null);
        NutrientControl.RevertCommand.Execute(null);
        AntifoamControl.RevertCommand.Execute(null);
        FoamControl.RevertCommand.Execute(null);
        FlaskAgitator.RevertCommand.Execute(null);
        StatusText = "Alterações não enviadas revertidas.";
        RefreshState();
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Informe um nome para a predefinição.";
            return;
        }

        if (!TryReadAllStagedValues(out var values) ||
            !PHControl.TryGetStagedSettings(out var phSettings) ||
            FlowRequestError is not null)
        {
            StatusText = "Corrija os campos antes de salvar a predefinição.";
            return;
        }

        var preset = new SetpointPreset
        {
            Name = name,
            TemperatureCelsius = values[0],
            TemperatureEnabled = Rows[0].Subsystem.IsEnabled,
            MotorRpm = (int)values[1],
            MotorEnabled = Rows[1].Subsystem.IsEnabled,
            OxygenPercent = values[2],
            OxygenEnabled = Rows[2].Subsystem.IsEnabled,
            PHControl = phSettings,
            PHControlEnabled = PHControl.IsEnabled,
            FlowLitresPerMinute = values[3],
            MaxFlowLitresPerMinute = FlowControl.MaximumForCommand,
            FlowEnabled = Rows[3].Subsystem.IsEnabled,
            Valve1Open = FlowControl.RequestedValve1,
            Valve2Open = FlowControl.RequestedValve2,
            PressureKilopascal = values[4],
            PressureEnabled = Rows[4].Subsystem.IsEnabled,
        };

        var existing = Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null)
        {
            Presets.Add(preset);
        }
        else
        {
            Presets[Presets.IndexOf(existing)] = preset;
        }

        SortPresets();
        SelectedPreset = Presets.First(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        PresetName = "";
        PersistPresets();
        StatusText = $"Predefinição “{name}” salva. Nenhum comando foi enviado.";
    }

    [RelayCommand]
    private void LoadPreset()
    {
        if (SelectedPreset is not { } preset)
        {
            StatusText = "Selecione uma predefinição para carregar.";
            return;
        }

        // maxFlow first: it defines the validation range of the flow row staged below.
        FlowControl.Stage(preset.MaxFlowLitresPerMinute, preset.Valve1Open, preset.Valve2Open);
        Rows[0].Subsystem.Stage(preset.TemperatureCelsius, preset.TemperatureEnabled);
        Rows[1].Subsystem.Stage(preset.MotorRpm, preset.MotorEnabled);
        Rows[2].Subsystem.Stage(preset.OxygenPercent, preset.OxygenEnabled);
        PHControl.Stage(preset.PHControl, preset.PHControlEnabled);
        Rows[3].Subsystem.Stage(preset.FlowLitresPerMinute, preset.FlowEnabled);
        Rows[4].Subsystem.Stage(preset.PressureKilopascal, preset.PressureEnabled);

        StatusText = $"Predefinição “{preset.Name}” carregada nos campos; nada foi enviado.";
        RefreshState();
    }

    [RelayCommand]
    private void SafeStop()
    {
        // Every actuator with an app-side control surface goes to its safe state in one
        // frame: the Phase 1 core loop, pH, and the WP7 nutrient, antifoam and flask
        // agitator. The level/foam sensor is deliberately left running — a stop must not
        // blind foam monitoring.
        var command = CommandBuilders.CoreSafeStop(FlowControl.MaximumForCommand)
            .Merge(PHControl.BuildSafeStop())
            .Merge(NutrientControl.BuildSafeStop())
            .Merge(AntifoamControl.BuildSafeStop())
            .Merge(FlaskAgitator.BuildSafeStop());
        var confirmed = _dialogs.ConfirmDestructive(
            "Parada segura",
            "Desativa temperatura, agitação, monitor de oxigênio, vazão, pressão, dosagem de pH, " +
            "nutriente, antiespumante e o agitador de frasco. As válvulas auxiliar e de nitrogênio " +
            "serão fechadas e a válvula de respiro será aberta. O sensor de nível/espuma segue ativo.",
            command.ToJson());

        if (!confirmed)
        {
            StatusText = "Parada segura cancelada; nenhum comando foi enviado.";
            return;
        }

        // Release the cascade first so the safe frame lands as a manual command rather than
        // being rejected for actuators the cascade still owns.
        _cascade.Disengage("parada segura");
        _device.Send(command);
        foreach (var row in Rows)
        {
            row.Subsystem.IsEnabled = false;
            row.Subsystem.CommitPendingCommand();
        }

        FlowControl.CommitRequested(flowEnabled: false);
        PHControl.IsEnabled = false;
        PHControl.CommitPendingCommand();
        NutrientControl.IsEnabled = false;
        NutrientControl.CommitPendingCommand();
        AntifoamControl.IsEnabled = false;
        AntifoamControl.CommitPendingCommand();
        FlaskAgitator.IsEnabled = false;
        FlaskAgitator.CommitPendingCommand();
        PersistAppliedSetpoints();
        StatusText = "Parada segura enviada; todos os subsistemas foram comandados para o estado seguro.";
        RefreshState();
    }

    private bool TryBuildCombinedCommand(out TecnalCommand combined)
    {
        combined = TecnalCommand.Create();
        var flowNeedsCommand = _flowSubsystem.HasPendingChange || FlowControl.HasPendingChange;

        foreach (var row in Rows)
        {
            var subsystem = row.Subsystem;
            if (ReferenceEquals(subsystem, _flowSubsystem))
            {
                if (!flowNeedsCommand)
                {
                    continue;
                }

                var setpoint = 0.0;
                if (subsystem.IsEnabled && !subsystem.TryGetStagedValue(out setpoint))
                {
                    return false;
                }

                if (!FlowControl.TryBuildRequested(setpoint, subsystem.IsEnabled, out var flowCommand))
                {
                    return false;
                }

                combined.Merge(flowCommand);
                continue;
            }

            if (!subsystem.HasPendingChange)
            {
                continue;
            }

            if (!subsystem.TryBuildPendingCommand(out var command))
            {
                return false;
            }

            combined.Merge(command);
        }

        if (PHControl.HasPendingChange)
        {
            if (!PHControl.TryBuildPendingCommand(out var phCommand))
            {
                return false;
            }

            combined.Merge(phCommand);
        }

        if (NutrientControl.HasPendingChange)
        {
            if (!NutrientControl.TryBuildPendingCommand(out var nutrientCommand))
            {
                return false;
            }

            combined.Merge(nutrientCommand);
        }

        if (AntifoamControl.HasPendingChange)
        {
            if (!AntifoamControl.TryBuildPendingCommand(out var antifoamCommand))
            {
                return false;
            }

            combined.Merge(antifoamCommand);
        }

        if (FlaskAgitator.HasPendingChange)
        {
            if (!FlaskAgitator.TryBuildPendingCommand(out var agitatorCommand))
            {
                return false;
            }

            combined.Merge(agitatorCommand);
        }

        return !combined.IsEmpty;
    }

    private bool DirtyRowsAreValid()
        => Rows.Where(row => row.Subsystem.HasPendingChange)
               .All(row => !row.Subsystem.IsEnabled || row.Subsystem.IsValid);

    private bool TryReadAllStagedValues(out double[] values)
    {
        values = new double[Rows.Count];
        for (var i = 0; i < Rows.Count; i++)
        {
            if (!Rows[i].Subsystem.TryGetStagedValue(out values[i]))
            {
                return false;
            }
        }

        return FlowControl.IsValid;
    }

    private void OnStagedStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubsystemViewModel.IsEnabled))
        {
            foreach (var row in Rows)
            {
                row.NotifyActiveChanged();
            }

            RefreshCascadeOverrides();
        }

        RefreshState();
    }

    private void OnOxygenRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlParameterRowViewModel.SelectedOxygenMode))
        {
            RefreshCascadeOverrides();
        }
    }

    private void RefreshCascadeOverrides()
    {
        var oxygenRow = Rows[2]; // Oxygen
        var agitationRow = Rows[1]; // Impeller
        var aerationRow = Rows[3]; // Airflow

        var engaged = _cascade.IsEngaged;
        var mode = oxygenRow.SelectedOxygenCascadeMode;

        var drivesAgitation = mode is CascadeMode.AgitationOnly or CascadeMode.DualCascade or CascadeMode.KlaPath;
        var drivesAeration = mode is CascadeMode.AerationOnly or CascadeMode.DualCascade or CascadeMode.KlaPath;

        agitationRow.IsOverriddenByCascade = engaged && drivesAgitation;
        aerationRow.IsOverriddenByCascade = engaged && drivesAeration;
    }

    /// <summary>
    /// Handles the oxygen row's "Ativo" toggle: engaging selects the mode and claims the oxygen
    /// actuators; a refusal (offline, or a map mode with no published map) reverts the toggle and
    /// reports why.
    /// </summary>
    private void OnOxygenEngageRequested(bool engage)
    {
        var oxygenRow = Rows[2];
        if (engage)
        {
            _cascade.SelectMode(oxygenRow.SelectedOxygenCascadeMode);
            if (!_cascade.CanEngage(out var reason))
            {
                StatusText = reason ?? "Não é possível ativar o controle de oxigênio agora.";
                oxygenRow.NotifyActiveChanged(); // snap the toggle back to off
                return;
            }

            var setpoints = _settings.Current.Setpoints;
            _cascade.Engage(setpoints.MotorRpm, setpoints.FlowLitresPerMinute);
            StatusText = "Controle de oxigênio ativado; a cascata assumiu agitação e aeração.";
        }
        else
        {
            _cascade.Disengage("operador desativou o controle de oxigênio");
            StatusText = "Controle de oxigênio desativado; o comando voltou ao operador.";
        }
        // The cascade's Updated event refreshes the overrides and the toggles.
    }

    /// <summary>Keeps the oxygen row and the actuator locks in step with the running cascade.</summary>
    private void OnCascadeUpdated()
    {
        var oxygenRow = Rows[2];
        var modeLabel = _cascade.Mode switch
        {
            CascadeMode.AgitationOnly => "Agitação",
            CascadeMode.AerationOnly => "Aeração",
            CascadeMode.DualCascade => "Cascata",
            CascadeMode.KlaPath => "Mapa",
            _ => oxygenRow.SelectedOxygenMode,
        };
        if (oxygenRow.SelectedOxygenMode != modeLabel)
        {
            oxygenRow.SelectedOxygenMode = modeLabel;
        }

        RefreshCascadeOverrides();
        foreach (var row in Rows)
        {
            row.NotifyActiveChanged();
        }

        RefreshState();
    }

    private void OnFlowStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlowControlViewModel.MaxFlowText) &&
            FlowControl.TryGetStagedMaxFlow(out var maximum))
        {
            _flowSubsystem.UpdateMaximum(maximum);
        }

        RefreshState();
    }

    private void OnPHStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RefreshState();

    private void OnDosingStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RefreshState();

    private void RefreshState()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(ApplyAllLabel));
        OnPropertyChanged(nameof(FlowRequestError));
        OnPropertyChanged(nameof(CanApplyAll));
        OnPropertyChanged(nameof(CanApplyFlowState));
        ApplyAllCommand.NotifyCanExecuteChanged();
        ApplyFlowStateCommand.NotifyCanExecuteChanged();
    }

    private void PersistAppliedSetpoints()
    {
        double Applied(int index, double fallback)
            => Rows[index].Subsystem.AppliedSetpoint ?? fallback;

        _settings.Update(s => s with
        {
            Setpoints = s.Setpoints with
            {
                TemperatureCelsius = Applied(0, s.Setpoints.TemperatureCelsius),
                MotorRpm = (int)Applied(1, s.Setpoints.MotorRpm),
                OxygenPercent = Applied(2, s.Setpoints.OxygenPercent),
                FlowLitresPerMinute = Applied(3, s.Setpoints.FlowLitresPerMinute),
                MaxFlowLitresPerMinute = FlowControl.AppliedMaxFlow,
                PressureKilopascal = Applied(4, s.Setpoints.PressureKilopascal),
            },
        });
    }

    private void PersistPresets()
        => _settings.Update(s => s with { SetpointPresets = [.. Presets] });

    private void SortPresets()
    {
        var ordered = Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Presets.Clear();
        foreach (var preset in ordered)
        {
            Presets.Add(preset);
        }
    }

    public void Dispose()
    {
        foreach (var row in Rows)
        {
            row.Subsystem.PropertyChanged -= OnStagedStateChanged;
            if (row.IsOxygenRow)
            {
                row.PropertyChanged -= OnOxygenRowPropertyChanged;
            }
        }

        FlowControl.PropertyChanged -= OnFlowStateChanged;
        PHControl.PropertyChanged -= OnPHStateChanged;
        NutrientControl.PropertyChanged -= OnDosingStateChanged;
        AntifoamControl.PropertyChanged -= OnDosingStateChanged;
        FoamControl.PropertyChanged -= OnDosingStateChanged;
        FlaskAgitator.PropertyChanged -= OnDosingStateChanged;
        _cascade.Updated -= OnCascadeUpdated;
        Tuning.Dispose();
    }
}
