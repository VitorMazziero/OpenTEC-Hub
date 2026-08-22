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
    }

    public SubsystemViewModel Subsystem { get; }

    public string IconKey { get; }

    public IReadOnlyList<CommandOwnerOption> ModeOptions { get; } =
    [
        new(CommandOwner.Manual, "Manual", null),
        new(CommandOwner.Automatic, "Automático", "A cascata chega na Fase 2."),
        new(CommandOwner.Recipe, "Receita", "O motor de receitas chega na Fase 3."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerText))]
    public partial CommandOwnerOption SelectedMode { get; set; }

    public string OwnerText => SelectedMode.Owner switch
    {
        CommandOwner.Automatic => "Cascata",
        CommandOwner.Recipe => "Receita",
        _ => "Operador",
    };
}

/// <summary>
/// The Phase 1 Controle page: verify, stage and send the whole core loop from one screen.
/// </summary>
public sealed partial class ControlViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly SubsystemViewModel _flowSubsystem;

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
        IDeviceService device,
        ISettingsService settings,
        IDialogService dialogs,
        ICascadeService cascade)
    {
        if (subsystems.Count != 5)
        {
            throw new ArgumentException("The Phase 1 control page requires five core subsystems.", nameof(subsystems));
        }

        _device = device;
        _settings = settings;
        _dialogs = dialogs;
        FlowControl = flowControl;
        PHControl = phControl;
        NutrientControl = nutrientControl;
        AntifoamControl = antifoamControl;
        FoamControl = foamControl;
        FlaskAgitator = flaskAgitator;
        BiomassControl = biomassControl;
        PumpControl = pumpControl;
        Tuning = new CascadeTuningViewModel(cascade, settings);

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
        }

        FlowControl.PropertyChanged += OnFlowStateChanged;
        PHControl.PropertyChanged += OnPHStateChanged;
        NutrientControl.PropertyChanged += OnDosingStateChanged;
        AntifoamControl.PropertyChanged += OnDosingStateChanged;
        FoamControl.PropertyChanged += OnDosingStateChanged;
        FlaskAgitator.PropertyChanged += OnDosingStateChanged;
        BiomassControl.PropertyChanged += OnDosingStateChanged;
        PumpControl.PropertyChanged += OnDosingStateChanged;

        foreach (var preset in settings.Current.SetpointPresets
                     .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Presets.Add(preset);
        }

        SelectedPreset = Presets.FirstOrDefault();
        RefreshState();
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

    /// <summary>Biomass sensor card (WP1). Self-contained apply; excluded from the safe-stop.</summary>
    public BiomassControlViewModel BiomassControl { get; }

    /// <summary>External-pump card (WP2). Self-contained apply; disabled by the safe-stop.</summary>
    public PumpControlViewModel PumpControl { get; }

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
        PumpControl.MarkStopped();
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
        => RefreshState();

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
        }

        FlowControl.PropertyChanged -= OnFlowStateChanged;
        PHControl.PropertyChanged -= OnPHStateChanged;
        NutrientControl.PropertyChanged -= OnDosingStateChanged;
        AntifoamControl.PropertyChanged -= OnDosingStateChanged;
        FoamControl.PropertyChanged -= OnDosingStateChanged;
        FlaskAgitator.PropertyChanged -= OnDosingStateChanged;
        BiomassControl.PropertyChanged -= OnDosingStateChanged;
        PumpControl.PropertyChanged -= OnDosingStateChanged;
        Tuning.Dispose();
    }
}
