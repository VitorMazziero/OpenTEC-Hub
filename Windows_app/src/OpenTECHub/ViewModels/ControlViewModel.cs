using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Alarms;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

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
        CommandOwner.Automatic => "Controle O₂",
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
    private readonly IKlaProfileStore? _klaProfileStore;

    /// <summary>
    /// Optional: the alarm kernel needs the operator's routing switches to detect a
    /// disagreement with the Hub. Null in the view-model tests, which do not raise alarms.
    /// </summary>
    private readonly IAlarmService? _alarms;
    private readonly SubsystemViewModel _flowSubsystem;
    private bool _switchingSharedPump;
    private bool _flowCommitPending;
    private bool _flowRowCommitPending;

    public ControlViewModel(
        IReadOnlyList<SubsystemViewModel> subsystems,
        FlowControlViewModel flowControl,
        PHControlViewModel phControl,
        NutrientControlViewModel nutrientControl,
        AntifoamControlViewModel antifoamControl,
        FoamControlViewModel foamControl,
        FlaskAgitatorViewModel flaskAgitator,
        BiomassControlViewModel biomassControl,
        PumpControlViewModel pumpControl,
        ServoDriveViewModel servoDrive,
        IDeviceService device,
        ISettingsService settings,
        IDialogService dialogs,
        ICascadeService cascade,
        ReceitasViewModel? receitas = null,
        IKlaProfileStore? klaProfileStore = null,
        IAlarmService? alarms = null,
        ProcessVariableViewModel? phVariable = null,
        ProcessVariableViewModel? distanceVariable = null,
        ProcessVariableViewModel? biomassVariable = null)
    {
        if (subsystems.Count != 5)
        {
            throw new ArgumentException("The Phase 1 control page requires five core subsystems.", nameof(subsystems));
        }

        _device = device;
        _settings = settings;
        _dialogs = dialogs;
        _cascade = cascade;
        _klaProfileStore = klaProfileStore;
        _alarms = alarms;
        PHVariable = phVariable;
        DistanceVariable = distanceVariable;
        BiomassVariable = biomassVariable;
        FlowControl = flowControl;
        PHControl = phControl;
        NutrientControl = nutrientControl;
        AntifoamControl = antifoamControl;
        FoamControl = foamControl;
        FlaskAgitator = flaskAgitator;
        BiomassControl = biomassControl;
        PumpControl = pumpControl;
        ServoDrive = servoDrive;

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
        _device.StateChanged += OnDeviceStateChanged;
        _device.TelemetryReceived += OnTelemetryReceived;

        FlowControl.PropertyChanged += OnFlowStateChanged;
        PHControl.PropertyChanged += OnPHStateChanged;
        NutrientControl.PropertyChanged += OnDosingStateChanged;
        AntifoamControl.PropertyChanged += OnDosingStateChanged;
        FoamControl.PropertyChanged += OnDosingStateChanged;
        FlaskAgitator.PropertyChanged += OnDosingStateChanged;
        BiomassControl.PropertyChanged += OnDosingStateChanged;
        PumpControl.PropertyChanged += OnDosingStateChanged;

        FlowControl.IsLoopRequested = _flowSubsystem.IsEnabled;
        PublishRoutingIntent();

        var presetsSource = settings.Current.SetpointPresets.Length > 0
            ? settings.Current.SetpointPresets
            : [SetpointPreset.DefaultPreset];

        foreach (var preset in presetsSource
                     .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Presets.Add(preset);
        }

        Receitas = receitas;
        if (receitas is not null)
        {
            receitas.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ReceitasViewModel.IsRunning) or nameof(ReceitasViewModel.RunState))
                {
                    OnPropertyChanged(nameof(IsRecipeRunning));
                    OnPropertyChanged(nameof(IsManualOperationEnabled));
                    OnPropertyChanged(nameof(RecipeRunningNotice));
                }
            };
        }

        SelectedPreset = Presets.FirstOrDefault();
        RefreshState();
        OnCascadeUpdated();
    }

    public ReceitasViewModel? Receitas { get; }
    public bool IsRecipeRunning => Receitas?.IsRunning ?? false;
    public bool IsManualOperationEnabled => !IsRecipeRunning;
    public string RecipeRunningNotice => $"Receita em execução ({Receitas?.StatusText}) — Todas as entradas manuais que enviam comandos ao biorreator estão bloqueadas pela automação.";

    public IReadOnlyList<ControlParameterRowViewModel> Rows { get; }

    /// <summary>Operator-facing order; command indexes remain stable in <see cref="Rows"/>.</summary>
    public IReadOnlyList<ControlParameterRowViewModel> DisplayRows
        => [Rows[1], Rows[0], Rows[4], Rows[2], Rows[3]];

    public ControlParameterRowViewModel AgitationRow => Rows[1];
    public ControlParameterRowViewModel TemperatureRow => Rows[0];
    public ControlParameterRowViewModel PressureRow => Rows[4];
    public ControlParameterRowViewModel OxygenRow => Rows[2];
    public ControlParameterRowViewModel FlowRow => Rows[3];
    public bool IsCascadeActive => _cascade.IsEngaged;

    [ObservableProperty]
    public partial bool IsExpandedTemperature { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedOxygen { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedPH { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedNutrient { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedAntifoam { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedFlow { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedDistance { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedExternalPump { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedBiomass { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedFlaskAgitator { get; set; }

    [ObservableProperty]
    public partial bool IsExpandedServoDrive { get; set; }

    public ProcessVariableViewModel TemperatureVariable => TemperatureRow.Subsystem.Variable;
    public ProcessVariableViewModel OxygenVariable => OxygenRow.Subsystem.Variable;
    public ProcessVariableViewModel FlowVariable => FlowRow.Subsystem.Variable;
    public ProcessVariableViewModel? PHVariable { get; }
    public ProcessVariableViewModel? DistanceVariable { get; }
    public ProcessVariableViewModel? BiomassVariable { get; }

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

    /// <summary>Biomass sensor card (WP1). Self-contained apply; excluded from the safe-stop.</summary>
    public BiomassControlViewModel BiomassControl { get; }

    /// <summary>External-pump card (WP2). Self-contained apply; disabled by the safe-stop.</summary>
    public PumpControlViewModel PumpControl { get; }

    /// <summary>The ASDA-B2 servo drive: measured shaft telemetry, and no motor control.</summary>
    public ServoDriveViewModel ServoDrive { get; }

    public bool CanActuate => _device.State == ConnectionState.Connected;

    public SubsystemViewModel FlowSubsystem => _flowSubsystem;

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

    private bool FlowStateDirty => _flowSubsystem.HasPendingChange || FlowControl.HasPendingChange;

    public string? FlowRequestError
    {
        get
        {
            if (!FlowStateDirty)
            {
                return null;
            }

            if (!FlowControl.HasFlowmeterTelemetry)
            {
                return FlowControl.FlowmeterStatusText;
            }

            if (FlowControl.IsFlowmeterOffline)
            {
                // The row already carries the concise "desconectado" chip. Avoid
                // repeating a second, long-form outage message in the drawer.
                return null;
            }

            if (FlowControl.IsAwaitingAck)
            {
                return FlowControl.PendingStatusText;
            }

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
           (!FlowStateDirty || FlowControl.CanSendFlowCommands) &&
           FlowRequestError is null;

    public bool CanApplyFlowState
        => FlowStateDirty && FlowControl.CanSendFlowCommands &&
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

        foreach (var row in dirtyRows.Where(row => row.Subsystem != _flowSubsystem))
        {
            row.Subsystem.CommitPendingCommand();
        }

        if (flowWasDirty)
        {
            _flowCommitPending = true;
            _flowRowCommitPending = _flowSubsystem.HasPendingChange;
            FlowControl.MarkCommandDispatched();
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
        StatusText = flowWasDirty
            ? FlowControl.PendingStatusText
            : command.Count == 1
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
        _flowCommitPending = true;
        _flowRowCommitPending = flowRowWasDirty;
        FlowControl.MarkCommandDispatched();
        StatusText = FlowControl.PendingStatusText;
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
    private void OpenOxygenConfig()
    {
        using var vm = new OxygenConfigViewModel(_cascade, _settings, _klaProfileStore);
        var dialog = new Views.Dialogs.OxygenConfigDialog(vm)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() == true)
        {
            var oxygenRow = Rows.FirstOrDefault(r => r.IsOxygenRow);
            if (oxygenRow != null)
            {
                oxygenRow.SelectedOxygenMode = vm.SelectedMode.Label;
            }
            StatusText = "Parâmetros de oxigênio aplicados.";
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (!TryReadAllStagedValues(out var values) ||
            !PHControl.TryGetStagedSettings(out var phSettings) ||
            FlowRequestError is not null)
        {
            StatusText = "Corrija os campos antes de salvar a predefinição.";
            return;
        }

        if (!_dialogs.PromptInput("Salvar Predefinição", "Digite um nome para a predefinição:", out var name, PresetName) ||
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();

        var preset = new SetpointPreset
        {
            Name = name,
            TemperatureCelsius = values[0],
            TemperatureEnabled = Rows[0].Subsystem.IsEnabled,
            MotorRpm = (int)values[1],
            MotorEnabled = Rows[1].Subsystem.IsEnabled,
            OxygenPercent = values[2],
            OxygenEnabled = Rows[2].Subsystem.IsEnabled,
            OxygenMode = Rows[2].SelectedOxygenMode,
            Cascade = _settings.Current.Cascade,
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

        if (!string.IsNullOrWhiteSpace(preset.OxygenMode))
        {
            Rows[2].SelectedOxygenMode = preset.OxygenMode;
        }

        if (preset.Cascade is not null)
        {
            _settings.Update(s => s with { Cascade = preset.Cascade });
            _cascade.Configure(preset.Cascade);
        }

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
            .Merge(FlaskAgitator.BuildSafeStop())
            .Merge(PumpControl.BuildSafeStop());
        var confirmed = _dialogs.ConfirmDestructive(
            "Parada segura",
            "Desativa temperatura, agitação, monitor de oxigênio, vazão, pressão, dosagem de pH, " +
            "nutriente, antiespumante, o agitador de frasco e a bomba externa. As válvulas auxiliar " +
            "e de nitrogênio serão fechadas e a válvula de respiro será aberta. Os sensores de " +
            "nível/espuma e de biomassa seguem ativos.",
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

        // The pump's routing switch travels on its own frame, after the one above. Merged in,
        // the Hub would clear routing while parsing and then discard the mode:0 beside it,
        // leaving the node dosing through a stop that reported success.
        _device.SendAfterCurrentFrame(PumpControl.BuildRoutingDisable());

        foreach (var row in Rows)
        {
            row.Subsystem.IsEnabled = false;
            row.Subsystem.CommitPendingCommand();
        }

        FlowControl.CommitRequested(false);
        PHControl.IsEnabled = false;
        PHControl.CommitPendingCommand();
        NutrientControl.IsEnabled = false;
        NutrientControl.CommitPendingCommand();
        AntifoamControl.IsEnabled = false;
        AntifoamControl.CommitPendingCommand();
        FlaskAgitator.IsEnabled = false;
        FlaskAgitator.CommitPendingCommand();
        PumpControl.MarkStopped();
        PersistAppliedSetpoints();
        StatusText = "Parada segura executada. Atuadores e dosagens desativados.";
        RefreshState();
    }

    private bool TryBuildCombinedCommand(out OpenTECCommand combined)
    {
        combined = OpenTECCommand.Create();

        foreach (var row in Rows.Where(r => r.Subsystem.HasPendingChange))
        {
            if (!row.Subsystem.TryBuildPendingCommand(out var fragment))
            {
                return false;
            }

            combined.Merge(fragment);
        }

        if (_flowSubsystem.HasPendingChange || FlowControl.HasPendingChange)
        {
            var targetFlow = _flowSubsystem.AppliedSetpoint ?? 0.0;
            if (_flowSubsystem.IsEnabled && !_flowSubsystem.TryGetStagedValue(out targetFlow))
            {
                return false;
            }

            if (!FlowControl.TryBuildRequested(targetFlow, _flowSubsystem.IsEnabled, out var flowCommand))
            {
                return false;
            }

            combined.Merge(flowCommand);
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

    private void OnStagedStateChanged(object? sender, PropertyChangedEventArgs e)
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

    private void OnOxygenRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlParameterRowViewModel.SelectedOxygenMode))
        {
            var oxygenRow = Rows[2];
            _cascade.SelectMode(oxygenRow.SelectedOxygenCascadeMode);
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
            StatusText = "Controle de oxigênio ativado; a malha de O₂ assumiu agitação e aeração.";
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

    private void OnFlowStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlowControlViewModel.MaxFlowText) &&
            FlowControl.TryGetStagedMaxFlow(out var maximum))
        {
            _flowSubsystem.UpdateMaximum(maximum);
        }

        _flowSubsystem.RefreshAvailability();
        RefreshState();
    }

    private void OnPHStateChanged(object? sender, PropertyChangedEventArgs e)
        => RefreshState();

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected && _flowCommitPending)
        {
            StatusText = "Conexão com a central perdida; o comando de vazão permanece sem confirmação.";
        }

        if (change.State != ConnectionState.Connected)
        {
            FlowControl.MarkHubUnavailable();
        }

        RefreshState();
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        var wasOnline = FlowControl.IsFlowmeterOnline;
        var wasAwaiting = FlowControl.IsAwaitingAck;
        FlowControl.UpdateTelemetry(snapshot);

        if (FlowControl.IsAwaitingAck)
        {
            StatusText = FlowControl.PendingStatusText;
        }
        else if (_flowCommitPending)
        {
            CompletePendingFlowCommit();
        }
        else if (wasAwaiting || !wasOnline)
        {
            StatusText = "Confirmação recebida; controles do fluxômetro liberados.";
        }

        RefreshState();
    }

    private void CompletePendingFlowCommit()
    {
        if (_flowRowCommitPending)
        {
            _flowSubsystem.CommitPendingCommand();
        }

        FlowControl.CommitRequested(_flowSubsystem.IsEnabled);
        _flowCommitPending = false;
        _flowRowCommitPending = false;
        PersistAppliedSetpoints();
        StatusText = "";
    }

    /// <summary>
    /// Tells the alarm kernel what the operator's external-device switches say.
    /// </summary>
    /// <remarks>
    /// Half of the routing-mismatch condition. The other half is the Hub's own echo, and
    /// the alarm only fires when the two disagree — so the Hub silently dropping every
    /// command for a device stops being invisible.
    /// </remarks>
    private void PublishRoutingIntent()
    {
        if (_alarms is null)
        {
            return;
        }

        _alarms.SetRoutingRequested(DeviceNames.Routing.Airflow, _flowSubsystem.IsEnabled);
        _alarms.SetRoutingRequested(DeviceNames.Routing.Absorbance, BiomassControl.IsEnabled);
        _alarms.SetRoutingRequested(DeviceNames.Routing.ExternalPump, PumpControl.IsEnabled);
        _alarms.SetRoutingRequested(DeviceNames.Routing.Distance, FoamControl.SensorEnabled);
        // The flask agitator has no Hub routing echo, so its offline alarm leans entirely on
        // this: it stays silent unless the operator has actually turned the agitator on.
        _alarms.SetRoutingRequested(DeviceNames.Routing.FlaskAgitator, FlaskAgitator.AppliedIsEnabled);
    }

    private void OnDosingStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The flow row's own enable lives on the subsystem, not on FlowControl, so it is
        // mirrored across wherever that changes.
        FlowControl.IsLoopRequested = _flowSubsystem.IsEnabled;
        PublishRoutingIntent();

        // The distance/foam firmware routine actuates the physical nutrient pump.
        // Never allow it to contend with the operator's nutrient dosing schedule.
        if (!_switchingSharedPump)
        {
            _switchingSharedPump = true;
            try
            {
                if (sender == NutrientControl && e.PropertyName == nameof(NutrientControlViewModel.IsEnabled) &&
                    NutrientControl.IsEnabled && FoamControl.SensorEnabled)
                {
                    FoamControl.SensorEnabled = false;
                    FoamControl.ApplyCommand.Execute(null);
                    StatusText = "Sensor de distância desativado: a bomba de nutrientes foi selecionada para dosagem.";
                }
                else if (sender == FoamControl && e.PropertyName == nameof(FoamControlViewModel.SensorEnabled) &&
                         FoamControl.SensorEnabled && NutrientControl.IsEnabled)
                {
                    NutrientControl.IsEnabled = false;
                    NutrientControl.ApplyCommand.Execute(null);
                    StatusText = "Dosagem de nutrientes desativada: o sensor de distância usa a mesma bomba.";
                }
            }
            finally
            {
                _switchingSharedPump = false;
            }
        }

        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(ApplyAllLabel));
        OnPropertyChanged(nameof(FlowRequestError));
        OnPropertyChanged(nameof(CanApplyAll));
        OnPropertyChanged(nameof(CanApplyFlowState));
        OnPropertyChanged(nameof(CanActuate));
        ApplyAllCommand.NotifyCanExecuteChanged();
        ApplyFlowStateCommand.NotifyCanExecuteChanged();
    }

    private void PersistAppliedSetpoints()
    {
        double Applied(int index, double fallback)
            => Rows[index].Subsystem.AppliedSetpoint ?? fallback;

        bool IsEnabled(int index, bool fallback)
            => Rows[index].Subsystem.AppliedIsEnabled;

        _settings.Update(s => s with
        {
            Setpoints = s.Setpoints with
            {
                TemperatureCelsius = Applied(0, s.Setpoints.TemperatureCelsius),
                TemperatureEnabled = IsEnabled(0, s.Setpoints.TemperatureEnabled),
                MotorRpm = (int)Applied(1, s.Setpoints.MotorRpm),
                MotorEnabled = IsEnabled(1, s.Setpoints.MotorEnabled),
                OxygenPercent = Applied(2, s.Setpoints.OxygenPercent),
                OxygenEnabled = IsEnabled(2, s.Setpoints.OxygenEnabled),
                FlowLitresPerMinute = Applied(3, s.Setpoints.FlowLitresPerMinute),
                FlowEnabled = IsEnabled(3, s.Setpoints.FlowEnabled),
                MaxFlowLitresPerMinute = FlowControl.AppliedMaxFlow,
                PressureKilopascal = Applied(4, s.Setpoints.PressureKilopascal),
                PressureEnabled = IsEnabled(4, s.Setpoints.PressureEnabled),
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
        _device.StateChanged -= OnDeviceStateChanged;
        _device.TelemetryReceived -= OnTelemetryReceived;
        BiomassControl.PropertyChanged -= OnDosingStateChanged;
        PumpControl.PropertyChanged -= OnDosingStateChanged;
        ServoDrive.Dispose();
    }
}
