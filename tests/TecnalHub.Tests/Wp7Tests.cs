using System.Globalization;
using System.IO;
using System.Text;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Telemetry;
using TecnalHub.Services.Theme;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>WP7's persisted-history, audit and display-unit contracts.</summary>
public sealed class Wp7Tests
{
    [Theory]
    [InlineData(100.0, PressureUnitPreference.KPa, 100.0)]
    [InlineData(100.0, PressureUnitPreference.Bar, 1.0)]
    [InlineData(100.0, PressureUnitPreference.MmHg, 750.0616827)]
    public void Pressure_display_conversion_round_trips(
        double canonical,
        PressureUnitPreference unit,
        double expectedDisplay)
    {
        var display = UnitConversions.PressureToDisplay(canonical, unit);

        Assert.Equal(expectedDisplay, display, precision: 7);
        Assert.Equal(canonical, UnitConversions.PressureToCanonical(display, unit), precision: 8);
    }

    [Fact]
    public void Historical_session_preserves_header_rows_duration_and_series()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tecnalhub-wp7-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session_test.txt");
        var csvPath = Path.Combine(directory, "session_test.csv");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                sessionPath,
                string.Join('\n',
                    SessionLogFormat.Header,
                    Row(0.00, 30.00, 300, 100, 40, 1, "USB"),
                    Row(2.50, 31.25, 325, 101, 42, 1.2, "USB")) + '\n',
                new UTF8Encoding(false));

            var settings = new AppSettings
            {
                Logging = new LoggingSettings { SessionLogPath = sessionPath },
            };
            var service = new SessionFileService();

            var summary = Assert.Single(
                service.Discover(settings),
                session => string.Equals(session.Path, sessionPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(summary.HeaderValid);
            Assert.Equal(2, summary.RowCount);
            Assert.Equal(2.5, summary.DurationMinutes, precision: 8);
            Assert.Equal("USB", summary.ConnectionMedia);

            var loaded = service.Load(summary);
            var temperature = loaded.GetSeries(TelemetryChannel.Temperature, window: null, maxPoints: 100);
            Assert.Equal([0.0, 2.5], temperature.Minutes);
            Assert.Equal([30.0, 31.25], temperature.Values);

            service.ExportCsv(summary, csvPath);
            var csv = File.ReadAllLines(csvPath, Encoding.UTF8);
            Assert.Equal(3, csv.Length);
            Assert.StartsWith("Time (min),Temperature (°C),Motor (rpm)", csv[0], StringComparison.Ordinal);
            Assert.StartsWith("2.50,31.25,325.000", csv[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Event_journal_keeps_exact_command_and_adds_setpoint_audit()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings());
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var journal = new EventJournal(arbiter, arbiter, settings);

        device.Send(CommandBuilders.MotorSetpoint(790));

        var entries = journal.Snapshot();
        var command = Assert.Single(entries, entry => entry.Source == AuditSource.Command);
        var setpoint = Assert.Single(entries, entry => entry.Source == AuditSource.Setpoint);
        Assert.Equal("""{"motorSetpoint":790}""", command.Detail);
        Assert.Equal(command.Detail, setpoint.Detail);
        Assert.Contains("agitação = 790", setpoint.Message, StringComparison.Ordinal);
        Assert.Equal(8, Enum.GetValues<AuditSource>().Length);
    }

    [Fact]
    public async Task Events_workspace_initializes_with_both_default_filters()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings());
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var journal = new EventJournal(arbiter, arbiter, settings);
        await using var logger = new RecordingSessionLogger();

        using var vm = new EventsViewModel(journal, logger, settings, new RecordingFiles());

        Assert.Null(vm.SelectedSource.Source);
        Assert.Null(vm.SelectedSeverity.Severity);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal(AuditSource.Application, vm.VisibleEvents[0].Entry.Source);
    }

    [Fact]
    public void Destructive_device_commands_default_to_no_send_and_show_exact_json()
    {
        var device = new RecordingDeviceService();
        var dialogs = new RecordingDialogService();
        using var vm = new SettingsViewModel(
            new MemorySettingsService(new AppSettings()),
            new RecordingThemeService(),
            device,
            dialogs);

        vm.ResetDeviceVariablesCommand.Execute(null);

        Assert.Empty(device.Sent);
        Assert.Equal("""{"resetVariables":1}""", dialogs.ExactCommand);
        Assert.Contains("cancelado", vm.StatusMessage, StringComparison.CurrentCultureIgnoreCase);

        dialogs.ConfirmResult = true;
        vm.RestartDeviceCommsCommand.Execute(null);

        Assert.Equal("""{"restart":1}""", Assert.Single(device.Sent));
        Assert.Equal("""{"restart":1}""", dialogs.ExactCommand);
    }

    [Fact]
    public void Guided_calibration_refreshes_only_changed_coefficients_in_open_settings()
    {
        var settings = new MemorySettingsService(new AppSettings());
        using var vm = new SettingsViewModel(
            settings,
            new RecordingThemeService(),
            new RecordingDeviceService(),
            new RecordingDialogService());

        vm.PHSlope = "0.123";
        vm.AutoConnect = false;

        settings.Update(current => current with
        {
            Calibration = current.Calibration with { OxygenA = 0.125, OxygenB = -2.5 },
        });

        Assert.Equal(0.125, double.Parse(vm.OxygenA, CultureInfo.CurrentCulture));
        Assert.Equal(-2.5, double.Parse(vm.OxygenB, CultureInfo.CurrentCulture));
        Assert.Equal("0.123", vm.PHSlope);
        Assert.False(vm.AutoConnect);
        Assert.True(vm.HasChanges);
        Assert.Contains("sincronizados", vm.StatusMessage, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Row(
        double minute,
        double temperature,
        double motor,
        double pressure,
        double oxygen,
        double flow,
        string connection)
        => string.Join('\t',
            minute.ToString("F2", CultureInfo.InvariantCulture),
            temperature.ToString("F2", CultureInfo.InvariantCulture),
            motor.ToString("F3", CultureInfo.InvariantCulture),
            "7.00",
            "-999.000",
            pressure.ToString(CultureInfo.InvariantCulture),
            oxygen.ToString("F3", CultureInfo.InvariantCulture),
            flow.ToString("F3", CultureInfo.InvariantCulture),
            "-999.00",
            "-999.00000",
            "-999.0000",
            "-999.000",
            "-999.000",
            connection);

    private sealed class MemorySettingsService(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial;

        public event Action<AppSettings>? Changed;

        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(Current);
        }

        public Task SaveNowAsync() => Task.CompletedTask;

        public void Reload() => Changed?.Invoke(Current);
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public bool IsDark { get; private set; }

        public event Action<bool>? ThemeChanged;

        public void Apply(ThemePreference preference)
        {
            IsDark = preference == ThemePreference.Dark;
            ThemeChanged?.Invoke(IsDark);
        }
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public string ExactCommand { get; private set; } = "";

        public bool ConfirmDestructive(string title, string consequence, string exactCommand)
        {
            ExactCommand = exactCommand;
            return ConfirmResult;
        }

        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            response = "";
            return false;
        }
    }

    private sealed class RecordingSessionLogger : ISessionLogger
    {
        public event Action? StatusChanged;

        public bool IsLogging { get; private set; }

        public string? CurrentPath { get; private set; }

        public int RowsWritten { get; private set; }

        public void Start(string path)
        {
            IsLogging = true;
            CurrentPath = path;
            StatusChanged?.Invoke();
        }

        public void Stop()
        {
            IsLogging = false;
            CurrentPath = null;
            StatusChanged?.Invoke();
        }

        public void Write(SensorSnapshot snapshot, double commandedRpm, string connectionStatus)
        {
            RowsWritten++;
            StatusChanged?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingFiles : IFileInteractionService
    {
        public string? ChooseSavePath(string title, string suggestedName, string filter, string extension)
            => null;

        public string? ChooseOpenPath(string title, string filter, string extension)
            => null;

        public string? ChooseFolder(string title, string? initialDirectory = null)
            => null;

        public void OpenFolder(string path)
        {
        }

        public void CopyText(string text)
        {
        }
    }
}
