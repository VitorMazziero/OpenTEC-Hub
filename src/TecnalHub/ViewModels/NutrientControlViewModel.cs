using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// Complete nutrient dosing state (WP7): operation/mix timing, the two cycle counts and
/// pump intensity, emitted atomically.
/// </summary>
/// <remarks>
/// Nutrient has no telemetry — it is a commanded-only pump, so the card shows what was
/// commanded and never a measured value. Restoring a preset stages fields only; nothing
/// reaches the wire until <see cref="ApplyCommand"/> runs.
/// </remarks>
public sealed partial class NutrientControlViewModel : ObservableObject
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private NutrientControlSettings _committed;

    public NutrientControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.NutrientControl;

        Load(_committed);
        AppliedIsEnabled = false;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    [ObservableProperty]
    public partial string OperationSecondsText { get; set; } = "1";

    [ObservableProperty]
    public partial string MixSecondsText { get; set; } = "60";

    [ObservableProperty]
    public partial string OperationCyclesText { get; set; } = "1";

    [ObservableProperty]
    public partial string MixCyclesText { get; set; } = "1";

    [ObservableProperty]
    public partial string PumpSpeedPercentText { get; set; } = "80";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool AppliedIsEnabled { get; set; }

    [ObservableProperty]
    public partial double? AppliedDutyCyclePercent { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool IsValid => ValidationError is null;

    /// <summary>A stop is never blocked by an invalid staged field.</summary>
    public bool CanApply => !IsEnabled || IsValid;

    public string AppliedStateText => AppliedIsEnabled && AppliedDutyCyclePercent is { } duty
        ? string.Create(CultureInfo.CurrentCulture, $"Ativo · ciclo útil {duty:F0}%")
        : "Desligado";

    partial void OnOperationSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnMixSecondsTextChanged(string value) => ValidateAndRefresh();

    partial void OnOperationCyclesTextChanged(string value) => ValidateAndRefresh();

    partial void OnMixCyclesTextChanged(string value) => ValidateAndRefresh();

    partial void OnPumpSpeedPercentTextChanged(string value) => ValidateAndRefresh();

    partial void OnIsEnabledChanged(bool value) => ValidateAndRefresh();

    partial void OnAppliedIsEnabledChanged(bool value) => OnPropertyChanged(nameof(AppliedStateText));

    partial void OnAppliedDutyCyclePercentChanged(double? value) => OnPropertyChanged(nameof(AppliedStateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryBuildPendingCommand(out var command))
        {
            StatusText = ValidationError ?? "Revise os parâmetros do nutriente.";
            return;
        }

        _device.Send(command);
        CommitPendingCommand();
        StatusText = IsEnabled
            ? "Estado completo da dosagem de nutriente enviado."
            : "Dosagem de nutriente desligada; intensidade zero enviada.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        IsEnabled = AppliedIsEnabled;
        HasPendingChange = false;
        StatusText = "Alterações não enviadas do nutriente foram revertidas.";
    }

    /// <summary>Builds one atomic nutrient frame without sending it, for the bulk apply.</summary>
    public bool TryBuildPendingCommand(out TecnalCommand command)
    {
        if (!IsEnabled)
        {
            command = BuildSafeStop();
            return true;
        }

        if (!TryGetStagedSettings(out var staged))
        {
            command = TecnalCommand.Create();
            return false;
        }

        command = CommandBuilders.NutrientControl(
            staged.OperationSeconds,
            staged.MixSeconds,
            staged.OperationCycles,
            staged.MixCycles,
            staged.PumpSpeedPercent);
        return true;
    }

    /// <summary>Safe-stop using valid staged auxiliaries, else the last committed set.</summary>
    public TecnalCommand BuildSafeStop()
    {
        var p = TryGetStagedSettings(out var staged) ? staged : _committed;
        return CommandBuilders.NutrientControlSafeStop(
            p.OperationSeconds, p.MixSeconds, p.OperationCycles, p.MixCycles);
    }

    /// <summary>Updates the acknowledged UI state after a caller queues the command.</summary>
    public void CommitPendingCommand()
    {
        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged;
            _settings.Update(settings => settings with { NutrientControl = staged });
        }
        else if (!IsEnabled)
        {
            Load(_committed);
        }

        AppliedIsEnabled = IsEnabled;
        AppliedDutyCyclePercent = IsEnabled ? DutyCycle(_committed) : 0.0;
        HasPendingChange = false;
    }

    public bool TryGetStagedSettings(out NutrientControlSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseInteger(OperationSecondsText, out var operation) || operation is < 1 or > 999 ||
            !DosingInput.TryParseInteger(MixSecondsText, out var mix) || mix is < 1 or > 999 ||
            !DosingInput.TryParseInteger(OperationCyclesText, out var opCycle) || opCycle is < 1 or > 999 ||
            !DosingInput.TryParseInteger(MixCyclesText, out var mixCycle) || mixCycle is < 1 or > 999 ||
            !DosingInput.TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return false;
        }

        settings = new NutrientControlSettings
        {
            OperationSeconds = operation,
            MixSeconds = mix,
            OperationCycles = opCycle,
            MixCycles = mixCycle,
            PumpSpeedPercent = speed,
        };
        return true;
    }

    /// <summary>Duty cycle shown as the commanded value on the synoptic and KPI strip.</summary>
    private static double DutyCycle(NutrientControlSettings s)
    {
        var total = s.OperationSeconds + s.MixSeconds;
        return total <= 0 ? 0.0 : Math.Round(100.0 * s.OperationSeconds / total, 0);
    }

    private void Load(NutrientControlSettings s)
    {
        OperationSecondsText = DosingInput.FormatInt(s.OperationSeconds);
        MixSecondsText = DosingInput.FormatInt(s.MixSeconds);
        OperationCyclesText = DosingInput.FormatInt(s.OperationCycles);
        MixCyclesText = DosingInput.FormatInt(s.MixCycles);
        PumpSpeedPercentText = DosingInput.Format(s.PumpSpeedPercent, 0);
    }

    private void ValidateAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        ValidationError = Validate();
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        RefreshPendingState();
    }

    private string? Validate()
    {
        if (!DosingInput.TryParseInteger(OperationSecondsText, out var operation) || operation is < 1 or > 999)
        {
            return "Operação: inteiro de 1 a 999 s.";
        }

        if (!DosingInput.TryParseInteger(MixSecondsText, out var mix) || mix is < 1 or > 999)
        {
            return "Mistura: inteiro de 1 a 999 s.";
        }

        if (!DosingInput.TryParseInteger(OperationCyclesText, out var opCycle) || opCycle is < 1 or > 999)
        {
            return "Ciclo de operação: inteiro de 1 a 999.";
        }

        if (!DosingInput.TryParseInteger(MixCyclesText, out var mixCycle) || mixCycle is < 1 or > 999)
        {
            return "Ciclo de mistura: inteiro de 1 a 999.";
        }

        if (!DosingInput.TryParseDouble(PumpSpeedPercentText, out var speed) || speed < 0.0 || speed > 99.0)
        {
            return "Intensidade da bomba: valor de 0 a 99%.";
        }

        return null;
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        if (IsEnabled != AppliedIsEnabled)
        {
            HasPendingChange = true;
            return;
        }

        HasPendingChange = !TryGetStagedSettings(out var staged) || staged != _committed;
    }
}
