using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// The separate flask agitator (WP7): on/off, automatic mode, a 0-100 magnitude, a
/// direction and the potentiometer re-enable.
/// </summary>
/// <remarks>
/// This is a <b>bench device, not the reactor impeller</b>, so it never appears on the
/// reactor synoptic. The operator picks a magnitude and a direction; the two combine into
/// the signed percent the command builder splits back into the wire's separate magnitude
/// (<c>agitatorPercent</c>) and direction (<c>agitatorDir</c>) keys. The signed form never
/// reaches the wire (<c>docs/PROTOCOL.md</c> §3.3).
/// </remarks>
public sealed partial class FlaskAgitatorViewModel : ObservableObject
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private bool _syncing;
    private FlaskAgitatorSettings _committed;

    public FlaskAgitatorViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.FlaskAgitator;

        Load(_committed);
        AppliedIsEnabled = false;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    /// <summary>Agitator running — <c>agitatorOn</c>.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>Automatic mode — <c>agitatorAuto</c>. The potentiometer drives speed when set.</summary>
    [ObservableProperty]
    public partial bool IsAutomatic { get; set; }

    /// <summary>Speed magnitude 0-100, bound to the slider.</summary>
    [ObservableProperty]
    public partial double MagnitudePercent { get; set; } = 50.0;

    /// <summary>Speed magnitude as text, bound to the entry beside the slider.</summary>
    [ObservableProperty]
    public partial string MagnitudePercentText { get; set; } = "50";

    /// <summary><c>true</c> clockwise (<c>agitatorDir:1</c>), <c>false</c> counter-clockwise.</summary>
    [ObservableProperty]
    public partial bool Clockwise { get; set; } = true;

    [ObservableProperty]
    public partial bool AppliedIsEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool IsValid => ValidationError is null;

    public bool CanApply => !IsEnabled || IsValid;

    public string AppliedStateText => AppliedIsEnabled ? "Ativo" : "Desligado";

    /// <summary>Direction picked as counter-clockwise, for the second radio.</summary>
    public bool CounterClockwise
    {
        get => !Clockwise;
        set => Clockwise = !value;
    }

    partial void OnMagnitudePercentChanged(double value)
    {
        if (!_syncing)
        {
            _syncing = true;
            MagnitudePercentText = DosingInput.Format(value, 0);
            _syncing = false;
        }

        ValidateAndRefresh();
    }

    partial void OnMagnitudePercentTextChanged(string value)
    {
        if (!_syncing && DosingInput.TryParseDouble(value, out var parsed) && parsed is >= 0.0 and <= 100.0)
        {
            _syncing = true;
            MagnitudePercent = parsed;
            _syncing = false;
        }

        ValidateAndRefresh();
    }

    partial void OnClockwiseChanged(bool value)
    {
        OnPropertyChanged(nameof(CounterClockwise));
        ValidateAndRefresh();
    }

    partial void OnIsEnabledChanged(bool value) => ValidateAndRefresh();

    partial void OnIsAutomaticChanged(bool value) => ValidateAndRefresh();

    partial void OnAppliedIsEnabledChanged(bool value) => OnPropertyChanged(nameof(AppliedStateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryBuildPendingCommand(out var command))
        {
            StatusText = ValidationError ?? "Revise os parâmetros do agitador.";
            return;
        }

        _device.Send(command);
        CommitPendingCommand();
        StatusText = IsEnabled
            ? "Estado completo do agitador de frasco enviado."
            : "Agitador de frasco desligado.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        IsEnabled = AppliedIsEnabled;
        HasPendingChange = false;
        StatusText = "Alterações não enviadas do agitador foram revertidas.";
    }

    /// <summary>Re-enables the physical potentiometer. A momentary action, sent at once.</summary>
    [RelayCommand]
    private void ReEnablePot()
    {
        _device.Send(CommandBuilders.FlaskAgitatorReEnablePot());
        StatusText = "Reativação do potenciômetro enviada ao agitador.";
    }

    /// <summary>Builds the flask-agitator frame without sending it, for the bulk apply.</summary>
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

        command = CommandBuilders.FlaskAgitator(on: true, staged.Automatic, SignedPercent(staged));
        return true;
    }

    /// <summary>Stops the agitator, keeping the staged magnitude and direction.</summary>
    public TecnalCommand BuildSafeStop()
    {
        var staged = TryGetStagedSettings(out var parsed) ? parsed : _committed;
        return CommandBuilders.FlaskAgitatorSafeStop(SignedPercent(staged));
    }

    public void CommitPendingCommand()
    {
        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged;
            _settings.Update(settings => settings with { FlaskAgitator = staged });
        }

        AppliedIsEnabled = IsEnabled;
        HasPendingChange = false;
    }

    public bool TryGetStagedSettings(out FlaskAgitatorSettings settings)
    {
        settings = _committed;
        if (!DosingInput.TryParseDouble(MagnitudePercentText, out var magnitude) || magnitude is < 0.0 or > 100.0)
        {
            return false;
        }

        settings = new FlaskAgitatorSettings
        {
            MagnitudePercent = magnitude,
            Clockwise = Clockwise,
            Automatic = IsAutomatic,
        };
        return true;
    }

    private static double SignedPercent(FlaskAgitatorSettings s)
        => s.Clockwise ? s.MagnitudePercent : -s.MagnitudePercent;

    private void Load(FlaskAgitatorSettings s)
    {
        _syncing = true;
        MagnitudePercent = s.MagnitudePercent;
        MagnitudePercentText = DosingInput.Format(s.MagnitudePercent, 0);
        _syncing = false;
        Clockwise = s.Clockwise;
        IsAutomatic = s.Automatic;
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
        => DosingInput.TryParseDouble(MagnitudePercentText, out var magnitude) && magnitude is >= 0.0 and <= 100.0
            ? null
            : "Intensidade: valor de 0 a 100%.";

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
