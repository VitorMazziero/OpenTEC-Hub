using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The separate flask agitator (WP7): on/off, foam-automatic mode, a 0-100 magnitude, a
/// direction and the potentiometer re-enable.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>bench device, not the reactor impeller</b>, so it never appears on the
/// reactor synoptic. The operator picks a magnitude and a direction; the two combine into
/// the signed percent the command builder splits back into the wire's separate magnitude
/// (<c>agitatorPercent</c>) and direction (<c>agitatorDir</c>) keys. The signed form never
/// reaches the wire (<c>docs/PROTOCOL.md</c> §3.3).
/// </para>
/// <para>
/// Firmware v10 pushes magnitude, direction, potentiometer authority, source and command ACK
/// through the Hub. Before that first push the card stays usable with
/// <see cref="ExternalDeviceStatus.HasTelemetry"/> false and says <i>awaiting telemetry</i>
/// rather than pretending either success or failure.
/// </para>
/// <para>
/// The potentiometer is the safety subtlety. The Hub turns an off command into
/// <c>ActivePot = agitatorReEnablePot</c>, and the node re-reads the bench knob on its next
/// loop whenever that is set — so an ordinary "Desligar" with the knob at 60 % restarts the
/// motor at 60 %. The ordinary stop keeps that behaviour, because it is the documented
/// meaning of that switch; the <b>safe stop locks the pot out</b>, because a stop that a
/// knob can undo is not a stop.
/// </para>
/// </remarks>
public sealed partial class FlaskAgitatorViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private bool _syncing;
    private FlaskAgitatorSettings _committed;

    public FlaskAgitatorViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher ?? new ManualDispatcher(device);
        _committed = settings.Current.FlaskAgitator;
        Status = new ExternalDeviceStatus("Agitador de frasco", "do agitador de frasco", timeProvider) { NodeKind = NodeFirmwareCatalog.Agitator };
        Status.PropertyChanged += OnStatusChanged;

        Load(_committed);
        AppliedIsEnabled = false;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    /// <summary>Presence and pending state of the node behind the Hub.</summary>
    public ExternalDeviceStatus Status { get; }

    /// <summary>Magnitude the node reports actually driving, or an em dash.</summary>
    [ObservableProperty]
    public partial string ActualPercentText { get; set; } = "—";

    /// <summary>Direction the node reports actually driving.</summary>
    [ObservableProperty]
    public partial string ActualDirectionText { get; set; } = "—";

    /// <summary>
    /// What last moved the motor. When this reads <c>Potenciômetro</c>, the bench knob is in
    /// charge and the staged setpoint above is not what the motor is doing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPotentiometerInControl))]
    public partial string ActualSourceText { get; set; } = "—";

    /// <summary>The node's potentiometer is live and outranks the app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPotentiometerInControl))]
    public partial bool IsPotentiometerActive { get; set; }

    /// <summary>Agitator running — <c>agitatorOn</c>.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// Hub-side automatic foam response — <c>agitatorAuto</c>. The physical potentiometer is
    /// the separate manual-local source represented by <c>agitatorReEnablePot</c>/<c>ActivePot</c>.
    /// </summary>
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

    /// <summary>Last magnitude actually queued for the device.</summary>
    [ObservableProperty]
    public partial double? AppliedMagnitudePercent { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanActuate))]
    [NotifyPropertyChangedFor(nameof(IsOwnedByOther))]
    [NotifyPropertyChangedFor(nameof(HasOwnerBadge))]
    [NotifyPropertyChangedFor(nameof(OwnerBadgeText))]
    [NotifyPropertyChangedFor(nameof(OwnerLockReason))]
    public partial CommandOwner CurrentOwner { get; set; } = CommandOwner.Manual;

    public bool IsOwnedByOther => CurrentOwner != CommandOwner.Manual;
    public bool HasOwnerBadge => IsOwnedByOther;
    public string? OwnerBadgeText => OwnershipUi.GetBadgeText(CurrentOwner);
    public string? OwnerLockReason => OwnershipUi.GetLockReason(CurrentOwner);

    public bool IsValid => ValidationError is null;

    public bool CanApply => (!IsEnabled || IsValid) && Status.IsOnline && Status.CanSend && !IsOwnedByOther;

    public bool CanActuate => Status.IsOnline && Status.CanSend && !IsOwnedByOther;

    public string StateText => IsEnabled ? "Ativo" : "Desligado";

    /// <summary>The bench knob currently holds the motor, whatever the card was told.</summary>
    public bool IsPotentiometerInControl =>
        IsPotentiometerActive || string.Equals(ActualSourceText, "Potenciômetro", StringComparison.Ordinal);

    /// <summary>
    /// Warns that an ordinary stop can be undone by the knob, when the node says it is live.
    /// </summary>
    public string? PotentiometerWarning => IsPotentiometerInControl
        ? "O potenciômetro da bancada está ativo: desligar devolve o controle a ele e o motor " +
          "volta a girar se o botão não estiver em zero. A parada segura bloqueia o potenciômetro."
        : null;

    /// <summary>Direction picked as counter-clockwise, for the second radio.</summary>
    public bool CounterClockwise
    {
        get => !Clockwise;
        set
        {
            if (value != !Clockwise)
            {
                Clockwise = !value;
            }
        }
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

    partial void OnIsEnabledChanged(bool value)
    {
        if (_initialised && IsOwnedByOther && value != AppliedIsEnabled)
        {
            IsEnabled = AppliedIsEnabled;
            return;
        }

        ValidateAndRefresh();
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnIsAutomaticChanged(bool value) => ValidateAndRefresh();

    partial void OnAppliedIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Operação bloqueada pelo controlador atual.";
            return;
        }

        if (!TryBuildPendingCommand(out var command))
        {
            StatusText = ValidationError ?? "Revise os parâmetros do agitador.";
            return;
        }

        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        CommitPendingCommand();
        Status.MarkCommandDispatched();
        StatusText = IsEnabled
            ? "Estado completo do agitador de frasco enviado."
            : IsPotentiometerInControl
                ? "Agitador de frasco desligado; o potenciômetro da bancada reassume o controle."
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
    [RelayCommand(CanExecute = nameof(CanActuate))]
    private void ReEnablePot()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.FlaskAgitatorReEnablePot());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        StatusText = "Reativação do potenciômetro enviada ao agitador.";
    }

    /// <summary>Builds the flask-agitator frame without sending it, for the bulk apply.</summary>
    public bool TryBuildPendingCommand(out OpenTECCommand command)
    {
        if (!IsEnabled)
        {
            // An ordinary stop, which leaves the Hub's potentiometer flag alone. Only the
            // operator safe-stop locks the knob out - see BuildSafeStop.
            command = CommandBuilders.FlaskAgitatorOff(
                SignedPercent(TryGetStagedSettings(out var stagedOff) ? stagedOff : _committed));
            return true;
        }

        if (!TryGetStagedSettings(out var staged))
        {
            command = OpenTECCommand.Create();
            return false;
        }

        command = CommandBuilders.FlaskAgitator(on: true, staged.Automatic, SignedPercent(staged));
        return true;
    }

    /// <summary>
    /// Stops the agitator and locks the potentiometer out, keeping the staged magnitude and
    /// direction so re-enabling resumes where the operator left it.
    /// </summary>
    /// <remarks>
    /// The pot lockout persists on the Hub until the operator re-arms it with
    /// <see cref="ReEnablePotCommand"/>. That is deliberate: after an emergency stop the
    /// bench knob does not get to restart the motor on its own.
    /// </remarks>
    public OpenTECCommand BuildSafeStop()
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
        AppliedMagnitudePercent = IsEnabled ? _committed.MagnitudePercent : 0.0;
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

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExternalDeviceStatus.IsOnline) or nameof(ExternalDeviceStatus.CanSend) or null)
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanActuate));
            ApplyCommand.NotifyCanExecuteChanged();
            ReEnablePotCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
        }
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Status.Update(
            snapshot.HasAgitatorTelemetry,
            snapshot.AgitatorOnline,
            snapshot.AgitatorCommandPending,
            commEnabled: null,
            snapshot.AgitatorNode);

        ActualPercentText = snapshot.AgitatorPercent > SensorReadings.NotReceived
            ? snapshot.AgitatorPercent.ToString("F0", CultureInfo.CurrentCulture)
            : "—";

        ActualDirectionText = snapshot.AgitatorDirection switch
        {
            1 => "Horário",
            0 => "Anti-horário",
            _ => "—",
        };

        IsPotentiometerActive = snapshot.AgitatorPotActive;
        ActualSourceText = snapshot.AgitatorSource switch
        {
            "Pot" => "Potenciômetro",
            "Hub" => "Hub",
            "USB" => "USB",
            "Wi-Fi" => "Wi-Fi",
            _ => "—",
        };

        OnPropertyChanged(nameof(PotentiometerWarning));
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= OnStatusChanged;
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
