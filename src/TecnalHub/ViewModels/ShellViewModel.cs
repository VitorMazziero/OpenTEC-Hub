using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Theme;

namespace TecnalHub.ViewModels;

/// <summary>A destination in the navigation rail.</summary>
public sealed record NavigationItem(string Id, string Label, string Glyph);

/// <summary>
/// Shell state: navigation, the always-visible KPI strip, and the live variables.
/// </summary>
/// <remarks>
/// Owns the single set of <see cref="ProcessVariableViewModel"/> instances that the
/// KPI strip, synoptic and detail pane all bind to, so those three views can never
/// disagree about a value. See <c>docs/UI_DESIGN.md</c> section 2.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ILogger<ShellViewModel> _log;

    public ShellViewModel(
        IDeviceService device,
        ISettingsService settings,
        IThemeService theme,
        ConnectionViewModel connection,
        ILogger<ShellViewModel> log)
    {
        _device = device;
        _settings = settings;
        _theme = theme;
        _log = log;
        Connection = connection;

        Temperature = new ProcessVariableViewModel("temperature", "Temperatura", "°C", decimals: 1);
        // No RPM feedback exists on the wire, so this variable can only ever show
        // what was commanded.
        Motor = new ProcessVariableViewModel(
            "motor", "Agitação", "rpm", decimals: 0, isCommandedOnly: true);
        Oxygen = new ProcessVariableViewModel("oxygen", "Oxigênio", "%", decimals: 1);
        Flow = new ProcessVariableViewModel("flow", "Vazão", "L/min", decimals: 2);
        Pressure = new ProcessVariableViewModel("pressure", "Pressão", "kPa", decimals: 1, isControllable: false);

        Variables = [Temperature, Motor, Oxygen, Flow, Pressure];

        // Ranges come from docs/PROTOCOL.md section 3.1 and are paired with the
        // command builders, so validation and the wire cannot drift apart.
        var setpoints = settings.Current.Setpoints;
        var maxFlow = setpoints.MaxFlowLitresPerMinute;

        Subsystems =
        [
            new SubsystemViewModel(Temperature,
                new SubsystemSpec(15, 60, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value),
                    () => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 0.0)),
                device, setpoints.TemperatureCelsius),

            new SubsystemViewModel(Motor,
                new SubsystemSpec(50, 1000, IsInteger: true,
                    value => CommandBuilders.MotorSetpoint((int)value),
                    () => CommandBuilders.MotorSetpoint(0)),
                device, setpoints.MotorRpm),

            new SubsystemViewModel(Oxygen,
                new SubsystemSpec(0, 100, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, value),
                    () => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, 0.0)),
                device, setpoints.OxygenPercent),

            new SubsystemViewModel(Flow,
                new SubsystemSpec(0, maxFlow, IsInteger: false,
                    value => CommandBuilders.FlowSetpoint(value, maxFlow),
                    // Safe-stop, not merely zero flow: both valves are forced closed,
                    // because leaving nitrogen open through a stop is a hazard.
                    () => CommandBuilders.FlowSafeStop(maxFlow)),
                device, setpoints.FlowLitresPerMinute),

            new SubsystemViewModel(Pressure,
                new SubsystemSpec(1, 380, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.PressureReference, value),
                    () => TecnalCommand.Create().Set(CommandKeys.PressureReference, 0.0)),
                device, setpoints.PressureKilopascal),
        ];

        SelectedVariable = Temperature;
        SelectedSubsystem = Subsystems[0];

        NavigationItems =
        [
            new NavigationItem("dashboard", "Painel", ""),
            new NavigationItem("charts", "Gráficos", ""),
            new NavigationItem("log", "Registro", ""),
            new NavigationItem("settings", "Configurações", ""),
        ];
        SelectedNavigationId = "dashboard";

        _device.StateChanged += OnStateChanged;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.DeviceLogReceived += OnDeviceLogReceived;
    }

    public ConnectionViewModel Connection { get; }

    /// <summary>Every live variable, in KPI-strip order.</summary>
    public IReadOnlyList<ProcessVariableViewModel> Variables { get; }

    public ProcessVariableViewModel Temperature { get; }

    public ProcessVariableViewModel Motor { get; }

    public ProcessVariableViewModel Oxygen { get; }

    public ProcessVariableViewModel Flow { get; }

    public ProcessVariableViewModel Pressure { get; }

    /// <summary>Controllable subsystems, aligned with <see cref="Variables"/>.</summary>
    public IReadOnlyList<SubsystemViewModel> Subsystems { get; }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    /// <summary>Recent device log lines, newest last. Bounded so a long run cannot grow it without limit.</summary>
    public ObservableCollection<string> DeviceLog { get; } = [];

    [ObservableProperty]
    public partial string SelectedNavigationId { get; set; }

    /// <summary>The variable whose controls fill the detail pane.</summary>
    [ObservableProperty]
    public partial ProcessVariableViewModel? SelectedVariable { get; set; }

    /// <summary>
    /// The subsystem behind <see cref="SelectedVariable"/>, or null when the selected
    /// variable is read-only (pressure has no controllable half in Phase 1 scope
    /// beyond its reference, which is exposed here for completeness).
    /// </summary>
    [ObservableProperty]
    public partial SubsystemViewModel? SelectedSubsystem { get; set; }

    /// <summary>Elapsed run time reported by the device, formatted for the header.</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "—";

    /// <summary>
    /// True when the sensor module behind the ESP32 is not answering.
    /// </summary>
    /// <remarks>
    /// Independent of the PC link: the USB or Wi-Fi connection can be perfectly
    /// healthy while the module's internal UART is down. Conflating the two would tell
    /// the operator the wrong thing.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsSensorModuleOffline { get; set; }

    [RelayCommand]
    private void Navigate(string id) => SelectedNavigationId = id;

    [RelayCommand]
    private void SelectVariable(ProcessVariableViewModel variable)
    {
        SelectedVariable = variable;
        SelectedSubsystem = Subsystems.FirstOrDefault(s => s.Variable == variable);
    }

    /// <summary>Selects a subsystem by id, for clicks on the synoptic.</summary>
    [RelayCommand]
    private void SelectById(string id)
    {
        if (Variables.FirstOrDefault(v => v.Id == id) is { } variable)
        {
            SelectVariable(variable);
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _theme.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        _theme.Apply(next);
        _settings.Update(s => s with { Theme = next });
    }

    /// <summary>
    /// Starts the link, after the shell is on screen.
    /// </summary>
    /// <remarks>
    /// Called from <c>ContentRendered</c>, never from the constructor: the cold-start
    /// budget is first frame under 2 s, and nothing may sit between process start and
    /// a visible window.
    /// </remarks>
    public void StartAutoConnect()
    {
        if (!_settings.Current.Connection.AutoConnect)
        {
            _log.LogInformation("Auto-connect disabled by settings");
            return;
        }

        _log.LogInformation("Auto-connect starting");
        _device.Connect();
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        if (change.State is ConnectionState.Connected)
        {
            return;
        }

        // Nothing is being measured any more; showing the last values as though they
        // were live is the failure this project is trying to avoid.
        foreach (var variable in Variables)
        {
            variable.Clear();
        }

        ElapsedText = "—";
        IsSensorModuleOffline = false;
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Temperature.Push(snapshot.Temperature);
        Oxygen.Push(snapshot.OxygenCalibrated);
        Flow.Push(snapshot.FlowRate);
        Pressure.Push(snapshot.Pressure);

        // Motor is deliberately not pushed here: the device reports no RPM feedback,
        // so there is nothing to measure. Its "value" is whatever was last commanded,
        // which the UI must present as a command rather than a reading - see
        // MotorIsCommandedOnly. v.6 logs the same commanded figure.
        Flow.Setpoint = snapshot.FlowSetpoint >= 0 ? snapshot.FlowSetpoint : null;

        IsSensorModuleOffline = !snapshot.SensorCommOk;

        var minutes = snapshot.TimeMinutes;
        ElapsedText = minutes < 0
            ? "—"
            : TimeSpan.FromMinutes(minutes).ToString(@"hh\:mm\:ss");
    }

    private void OnDeviceLogReceived(string line)
    {
        DeviceLog.Add(line);

        // A multi-hour run must not accumulate an unbounded list.
        while (DeviceLog.Count > 500)
        {
            DeviceLog.RemoveAt(0);
        }
    }

    /// <summary>Captures the applied setpoints so they are restored next launch.</summary>
    public void PersistSetpoints()
    {
        double Applied(int index, double fallback)
            => Subsystems[index].AppliedSetpoint ?? fallback;

        _settings.Update(s => s with
        {
            Setpoints = s.Setpoints with
            {
                TemperatureCelsius = Applied(0, s.Setpoints.TemperatureCelsius),
                MotorRpm = (int)Applied(1, s.Setpoints.MotorRpm),
                OxygenPercent = Applied(2, s.Setpoints.OxygenPercent),
                FlowLitresPerMinute = Applied(3, s.Setpoints.FlowLitresPerMinute),
                PressureKilopascal = Applied(4, s.Setpoints.PressureKilopascal),
            },
        });
    }

    public void Dispose()
    {
        _device.StateChanged -= OnStateChanged;
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.DeviceLogReceived -= OnDeviceLogReceived;
        Connection.Dispose();
    }
}
