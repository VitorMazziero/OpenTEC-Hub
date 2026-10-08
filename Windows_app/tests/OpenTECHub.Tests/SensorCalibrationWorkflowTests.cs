using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class SensorCalibrationWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Second_standard_requires_operator_confirmation_before_acquisition(bool ph)
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        BeginFirstPoint(vm, device, ph);
        var workflow = ph ? vm.PH.Workflow : vm.Oxygen.Workflow;
        Assert.Equal(SensorCalibrationPhase.AwaitingSecond, workflow.Phase);
        Assert.Contains("Troque", workflow.Title);
        Assert.Contains("2 pronto", workflow.ActionText);
        Assert.True(workflow.Steps[1].IsComplete);
        Assert.True(workflow.Steps[2].IsActive);
        var progress = ph ? vm.PH.ProgressPercent : vm.Oxygen.ProgressPercent;
        for (var i = 0; i < 10; i++)
        {
            Push(device, settings.Current, second: true);
        }
        Assert.Equal(SensorCalibrationPhase.AwaitingSecond, ph ? vm.PH.Workflow.Phase : vm.Oxygen.Workflow.Phase);
        Assert.Equal(progress, ph ? vm.PH.ProgressPercent : vm.Oxygen.ProgressPercent);
        Assert.False(ph ? vm.PH.CanApplyProposal : vm.Oxygen.CanApplyProposal);
        ConfirmAndAcquireSecond(vm, device, settings.Current, ph);
        Assert.Equal(SensorCalibrationPhase.Review, ph ? vm.PH.Workflow.Phase : vm.Oxygen.Workflow.Phase);
        Assert.False(ph ? vm.PH.Workflow.CanEditSetup : vm.Oxygen.Workflow.CanEditSetup);
        Assert.Equal(ph ? vm.PH.ApplyProposalCommand : vm.Oxygen.ApplyProposalCommand,
            ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Applying_a_curve_saves_it_and_a_reopened_workspace_uses_it(bool ph)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OpenTEC-Calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AppSettings()));
        await using var settings = new SettingsService(NullLogger<SettingsService>.Instance, path);
        var original = settings.Current;
        var device = new RecordingDeviceService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        BeginFirstPoint(vm, device, ph);
        ConfirmAndAcquireSecond(vm, device, settings.Current, ph);
        Assert.Equal(original.Calibration, settings.Current.Calibration); // proposed only
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        await settings.SaveNowAsync();
        await using var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
        Assert.Equal(settings.Current.Calibration.PHSlope, reloaded.Current.Calibration.PHSlope);
        Assert.Equal(settings.Current.Calibration.PHIntercept, reloaded.Current.Calibration.PHIntercept);
        Assert.Equal(settings.Current.Calibration.OxygenA, reloaded.Current.Calibration.OxygenA);
        Assert.Equal(settings.Current.Calibration.OxygenB, reloaded.Current.Calibration.OxygenB);
        var parser = new TelemetryParser(reloaded.Current.ToParserConfig());
        parser.Parse("""{"pHval":14609.5,"Oxyval":1000,"SensorCommOK":true}""");
        Assert.Equal(ph ? 7 : 0, ph ? parser.Readings.PHCalibrated : parser.Readings.OxygenCalibrated, precision: 4);
        using var reopenedControl = new PHControlViewModel(device, reloaded);
        using var reopened = new CalibrationViewModel(device, reloaded, reopenedControl);
        Assert.Equal(ph ? vm.PH.CurrentEquationText : vm.Oxygen.CurrentEquationText,
            ph ? reopened.PH.CurrentEquationText : reopened.Oxygen.CurrentEquationText);
    }

    [Theory]
    [InlineData(0, "change")]
    [InlineData(1, "change")]
    [InlineData(0, "review")]
    [InlineData(1, "review")]
    [InlineData(0, "saved")]
    [InlineData(1, "saved")]
    public void Both_sensors_render_the_shared_steps_and_primary_action(int tab, string phase)
    {
        WpfRenderingHost.Run(() =>
        {
            WpfRenderingHost.SetTheme(true);
            var ph = tab == 0;
            var device = new RecordingDeviceService();
            var settings = new MemorySettingsService();
            using var control = new PHControlViewModel(device, settings);
            using var vm = new CalibrationViewModel(device, settings, control) { SelectedTabIndex = tab };
            BeginFirstPoint(vm, device, ph);
            if (phase != "change")
            {
                ConfirmAndAcquireSecond(vm, device, settings.Current, ph);
            }
            if (phase == "saved")
            {
                (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
                Push(device, settings.Current, second: true);
            }
            var workflow = ph ? vm.PH.Workflow : vm.Oxygen.Workflow;
            var view = new CalibrationView { DataContext = vm };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);
            var children = Descendants(view).ToArray();
            Assert.Single(children.OfType<SensorCalibrationView>());
            Assert.Contains(children.OfType<TextBlock>(), text => text.Text == workflow.Title);
            Assert.Contains(children.OfType<Button>(), button => Equals(button.Content, workflow.ActionText) && button.IsEnabled);
            if (phase == "saved")
            {
                Assert.All(workflow.Steps, step => Assert.True(step.IsComplete));
                Assert.False(workflow.ShowProposal);
            }
            var directory = Path.Combine(TestPaths.RepositoryRoot, "docs", "evidence", "ui-calibration");
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"{(ph ? "ph" : "oxygen")}-{phase}.png"));
            encoder.Save(stream);
            WpfRenderingHost.SetTheme(false);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_point_shortcut_uses_the_selected_reference_and_skips_the_second_step(bool ph)
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var control = new PHControlViewModel(device, settings);
        using var vm = new CalibrationViewModel(device, settings, control);
        vm.PH.StabilityWindowText = vm.Oxygen.StabilityWindowText = "2";
        vm.PH.AverageSamplesText = vm.Oxygen.AverageSamplesText = "2";
        Push(device, settings.Current, second: false);
        (ph ? vm.PH.StartOnePointCommand : vm.Oxygen.StartOnePointCommand).Execute(null);
        Assert.False(ph ? vm.PH.IsTwoPoint : vm.Oxygen.IsTwoPoint);
        Assert.Equal("Não usado", (ph ? vm.PH.Workflow : vm.Oxygen.Workflow).Steps[2].State);
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        for (var i = 0; i < 4; i++)
        {
            Push(device, settings.Current, second: false);
        }
        Assert.Equal(SensorCalibrationPhase.Review, (ph ? vm.PH.Workflow : vm.Oxygen.Workflow).Phase);
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        Assert.Equal(ph ? 7 : 100, ph ? settings.Current.Calibration.DecodePH(14609.5)
            : settings.Current.Calibration.DecodeOxygen(1000), precision: 6);
    }
    private static void BeginFirstPoint(CalibrationViewModel vm, RecordingDeviceService device, bool ph)
    {
        vm.PH.StabilityWindowText = vm.Oxygen.StabilityWindowText = "2";
        vm.PH.AverageSamplesText = vm.Oxygen.AverageSamplesText = "2";
        var settings = new AppSettings();
        Push(device, settings, second: false);
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        for (var i = 0; i < 4; i++)
        {
            Push(device, settings, second: false);
        }
    }

    private static void ConfirmAndAcquireSecond(CalibrationViewModel vm, RecordingDeviceService device, AppSettings settings, bool ph)
    {
        (ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).Execute(null);
        Assert.False((ph ? vm.PH.PrimaryActionCommand : vm.Oxygen.PrimaryActionCommand).CanExecute(null));
        for (var i = 0; i < 4; i++)
        {
            Push(device, settings, second: true);
        }
    }

    private static void Push(RecordingDeviceService device, AppSettings settings, bool second)
    {
        var ph = second ? 9214.15 : 14609.5;
        var oxygen = second ? 4000 : 1000;
        device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true, PHRaw = ph, OxygenRaw = oxygen,
            PHCalibrated = settings.Calibration.DecodePH(ph),
            OxygenCalibrated = settings.Calibration.DecodeOxygen(oxygen),
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
