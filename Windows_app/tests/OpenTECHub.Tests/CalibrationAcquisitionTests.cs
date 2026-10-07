using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CalibrationAcquisitionTests
{
    private static SensorSnapshot Reading(double raw = 1000) => new()
    {
        SensorCommOk = true, PHRaw = raw, PHCalibrated = 7,
        OxygenRaw = raw, OxygenCalibrated = 50,
    };

    [Fact]
    public void Both_procedures_show_cached_data_and_refresh_start_button_state()
    {
        var device = new RecordingDeviceService();
        device.PushTelemetry(Reading());
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        Assert.Equal(1000.ToString("F1", CultureInfo.CurrentCulture), vm.PH.CurrentRawText);
        Assert.Equal(vm.PH.CurrentRawText, vm.Oxygen.CurrentRawText);
        var phNotifications = 0;
        var oxygenNotifications = 0;
        vm.PH.StartProcedureCommand.CanExecuteChanged += (_, _) => phNotifications++;
        vm.Oxygen.StartProcedureCommand.CanExecuteChanged += (_, _) => oxygenNotifications++;
        vm.PH.StartProcedureCommand.Execute(null);
        vm.Oxygen.StartProcedureCommand.Execute(null);
        Assert.Equal(PHCalibrationStage.AwaitingFirstBuffer, vm.PH.Stage);
        Assert.Equal(OxygenCalibrationStage.AwaitingFirstStandard, vm.Oxygen.Stage);
        Assert.False(vm.PH.StartProcedureCommand.CanExecute(null));
        Assert.False(vm.Oxygen.StartProcedureCommand.CanExecute(null));
        Assert.True(phNotifications > 0 && oxygenNotifications > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_offline_sensor_data_restarts_stability_instead_of_completing_an_average(bool offline)
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        vm.PH.StabilityWindowText = vm.Oxygen.StabilityWindowText = "2";
        vm.PH.AverageSamplesText = vm.Oxygen.AverageSamplesText = "2";
        device.PushTelemetry(Reading());
        vm.PH.StartOnePointCommand.Execute(null);
        vm.Oxygen.StartOnePointCommand.Execute(null);
        vm.PH.ConfirmPointCommand.Execute(null);
        vm.Oxygen.ConfirmPointCommand.Execute(null);
        device.PushTelemetry(Reading());
        device.PushTelemetry(Reading());
        device.PushTelemetry(Reading()); // one sample into final averaging
        device.PushTelemetry(Reading() with
        {
            SensorCommOk = !offline, PHUpdated = offline, OxygenUpdated = offline,
        });
        Assert.Equal(PHCalibrationStage.StabilizingFirst, vm.PH.Stage);
        Assert.Equal(OxygenCalibrationStage.StabilizingFirst, vm.Oxygen.Stage);
        Assert.Equal("—", vm.PH.CurrentCalibratedText);
        Assert.Equal("—", vm.Oxygen.CurrentCalibratedText);
        device.PushTelemetry(Reading());
        device.PushTelemetry(Reading());
        device.PushTelemetry(Reading());
        Assert.False(vm.PH.CanApplyProposal);
        Assert.False(vm.Oxygen.CanApplyProposal);
        device.PushTelemetry(Reading());
        Assert.True(vm.PH.CanApplyProposal);
        Assert.True(vm.Oxygen.CanApplyProposal);
    }

    [Fact]
    public void Reconnection_requires_new_data_before_starting_either_procedure()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        device.PushTelemetry(Reading());
        device.PushState(ConnectionState.Disconnected);
        Assert.Equal("—", vm.PH.CurrentRawText);
        Assert.Equal("—", vm.Oxygen.CurrentRawText);
        device.PushState(ConnectionState.Connected);
        vm.PH.StartProcedureCommand.Execute(null);
        vm.Oxygen.StartProcedureCommand.Execute(null);
        Assert.Equal(PHCalibrationStage.Failed, vm.PH.Stage);
        Assert.Equal(OxygenCalibrationStage.Failed, vm.Oxygen.Stage);
    }

    [Fact]
    public void Unrelated_frames_do_not_clear_live_readings_or_restart_probe_averaging()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        vm.PH.StabilityWindowText = vm.Oxygen.StabilityWindowText = "2";
        vm.PH.AverageSamplesText = vm.Oxygen.AverageSamplesText = "2";
        device.PushTelemetry(Reading());
        vm.PH.StartOnePointCommand.Execute(null); vm.Oxygen.StartOnePointCommand.Execute(null);
        vm.PH.ConfirmPointCommand.Execute(null); vm.Oxygen.ConfirmPointCommand.Execute(null);
        for (var i = 0; i < 4; i++)
        {
            device.PushTelemetry(Reading() with { PHUpdated = false, OxygenUpdated = false, PHFrameReceived = false, OxygenFrameReceived = false });
            Assert.NotEqual("—", vm.PH.CurrentRawText);
            Assert.NotEqual("—", vm.Oxygen.CurrentRawText);
            device.PushTelemetry(Reading());
        }
        Assert.True(vm.PH.CanApplyProposal);
        Assert.True(vm.Oxygen.CanApplyProposal);
    }

    [Fact]
    public void Parser_marks_missing_sentinel_and_filtered_probe_values_as_not_updated()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"pHval":1000,"Oxyval":1000,"SensorCommOK":true}""");
        Assert.True(parser.Readings.Snapshot().PHUpdated);
        Assert.True(parser.Readings.Snapshot().OxygenUpdated);
        foreach (var json in new[]
        {
            """{"Time":1}""", """{"pHval":0,"Oxyval":0}""",
            """{"pHval":3000,"Oxyval":3000}""",
        })
        {
            parser.Parse(json);
            Assert.False(parser.Readings.Snapshot().PHUpdated);
            Assert.False(parser.Readings.Snapshot().OxygenUpdated);
            Assert.Equal(1000, parser.Readings.PHRaw);
            Assert.Equal(1000, parser.Readings.OxygenRaw);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Calibration_tabs_render_live_values_and_operator_instructions(int tab)
    {
        WpfRenderingHost.Run(() =>
        {
            var device = new RecordingDeviceService();
            device.PushTelemetry(Reading());
            var settings = new MemorySettingsService();
            using var control = new PHControlViewModel(device, settings);
            using var vm = new CalibrationViewModel(device, settings, control) { SelectedTabIndex = tab };
            if (tab == 0) vm.PH.StartProcedureCommand.Execute(null);
            else vm.Oxygen.StartProcedureCommand.Execute(null);
            var view = new CalibrationView { DataContext = vm };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 860);
            var texts = Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(1000.ToString("F1", CultureInfo.CurrentCulture), texts);
            Assert.Contains(tab == 0 ? vm.PH.InstructionText : vm.Oxygen.InstructionText, texts);
            Assert.Contains(tab == 0 ? vm.PH.StageText : vm.Oxygen.StageText, texts);
            var path = System.IO.Path.Combine(TestPaths.RepositoryRoot, "docs", "evidence", "ui-calibration");
            System.IO.Directory.CreateDirectory(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(path, tab == 0 ? "ph.png" : "oxygen.png"));
            encoder.Save(stream);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
