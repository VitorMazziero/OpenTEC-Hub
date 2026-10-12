using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Renders the screens this change touched into <c>docs/evidence/ui-documentation/</c>.
/// </summary>
/// <remarks>
/// The evidence folder is produced, not collected by hand: a screenshot pasted into a chat
/// cannot be re-made when the layout changes again, and a stale picture of a fixed screen is
/// worse than none. Running the suite refreshes every file here from the real visual tree.
/// </remarks>
[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class DocumentationEvidenceTests
{
    private static readonly string EvidenceRoot = Path.Combine(
        TestPaths.EvidenceRoot, "docs", "evidence", "ui-documentation");

    [Fact]
    public void Capture_power_assembly_tab_with_the_reorganised_assay_card()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new PowerView { DataContext = shell.PowerTest };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "potencia-montagem.png");
        });
    }

    [Fact]
    public void Capture_power_validation_tab_without_its_paragraphs()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new PowerView { DataContext = shell.PowerTest };

            // The tab is selected on the built tree, not through a view-model flag: which tab
            // is open is view state, and the capture has to show what the operator sees.
            view.Loaded += (_, _) =>
            {
                var tabs = FindDescendant<TabControl>(view);
                if (tabs is not null)
                {
                    tabs.SelectedIndex = 2;
                    tabs.UpdateLayout();
                }
            };

            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "potencia-validacao.png");
        });
    }

    [Fact]
    public void Capture_the_kla_determination_page_with_its_uncropped_cards()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new KlaDeterminationView { DataContext = shell.KlaDetermination };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 860);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "kla-determinacao.png");
        });
    }

    [Fact]
    public void Capture_the_kla_mapping_page_with_its_deliberate_button_grid()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new KlaMappingView { DataContext = shell.KlaMapping };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 860);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "kla-mapeamento.png");
        });
    }

    [Theory]
    [InlineData(DocumentationCatalog.DashboardTopicId, "documentacao-painel.png")]
    [InlineData(DocumentationCatalog.ControlTopicId, "documentacao-controle.png")]
    [InlineData(DocumentationCatalog.RecipesTopicId, "documentacao-receitas.png")]
    [InlineData(DocumentationCatalog.RecipeBlocksTopicId, "documentacao-receitas-blocos.png")]
    [InlineData(DocumentationCatalog.RecipeOxygenTopicId, "documentacao-receitas-controle-o2.png")]
    [InlineData(DocumentationCatalog.KlaDeterminationTopicId, "documentacao-kla-determinacao.png")]
    [InlineData(DocumentationCatalog.KlaMappingTopicId, "documentacao-kla-mapeamento.png")]
    [InlineData(DocumentationCatalog.PowerTopicId, "documentacao-potencia.png")]
    [InlineData(DocumentationCatalog.IntegrationTopicId, "documentacao-integracao.png")]
    [InlineData(DocumentationCatalog.PowerTareTopicId, "documentacao-potencia-tara.png")]
    public void Capture_the_documentation_section_for_a_topic(string topicId, string fileName)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            shell.Settings.SelectDocumentation(topicId);

            var view = new SettingsView { DataContext = shell.Settings };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 900);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, fileName);
        });
    }

    [Fact]
    public void Capture_a_cascade_block_whose_note_no_longer_covers_its_ports()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var recipes = shell.Receitas;
            recipes.NewRecipeCommand.Execute(null);
            recipes.SelectedTab!.AddBlock(OpenTECHub.Services.Recipes.NodeType.CascadeControl, 320, 240);

            var view = new ReceitasView { DataContext = recipes };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "receitas-bloco-cascata.png");
        });
    }

    [Theory]
    [InlineData("pump-curve", "calibracao-bomba-curva.png")]
    [InlineData("pump-dose", "calibracao-bomba-ponto-de-dosagem.png")]
    public void Capture_each_pump_calibration_section(string target, string fileName)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new CalibrationView { DataContext = shell.Calibration };
            WpfRenderingHost.RenderElement(view, 1400, 1990);

            // The card's shortcut: it must land on the pump tab and on the right section.
            shell.Calibration.Select(target);
            var tabs = FindDescendant<TabControl>(view);
            Assert.NotNull(tabs);
            Assert.Equal("Bomba Externa", (tabs!.SelectedItem as TabItem)?.Header);
            Assert.Equal(target == "pump-dose", shell.Calibration.Pump.IsDoseSection);

            // Tall enough for the whole right column. Not 2000: the validator samples every
            // (w·h/2000)-th pixel, which at h = 2000 is always column 0.
            var bitmap = WpfRenderingHost.RenderElement(view, 1400, 1990);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, fileName);
        });
    }

    [Fact]
    public void Capture_the_external_pump_card_with_status_strip_and_stacked_charts()
    {
        WpfRenderingHost.Run(() =>
        {
            var device = new RecordingDeviceService();
            var settings = new MemorySettingsService();
            settings.Update(x => x with
            {
                PumpControl = x.PumpControl with
                {
                    Dose = new OpenTECHub.Services.Persistence.PumpDoseSettings
                    {
                        Point = new OpenTECHub.Services.Persistence.PumpDosePointSettings
                        {
                            SpeedUnits = 120, RateMlMin = 5.4, ProfileName = "Silicone 2mm", CapturedAtUtc = "2026-10-11T15:00:00Z",
                        },
                    },
                },
            });
            using var pump = new PumpControlViewModel(device, settings);

            // A 3.13 pump behind a 10.9 Hub, idle, with no curve on the node.
            device.PushTelemetry(new OpenTECHub.Protocol.SensorSnapshot
            {
                HasPumpTelemetry = true, PumpOnline = true, PumpCommEnabled = true,
                PumpVolume = 3.5, PumpFlow = 0.0, PumpMode = 0, PumpTargetVolume = 0.0,
                PumpTransitionSpeed = 500.0, PumpCalCrc = null, PumpCycleVolume = 0.0,
                PumpDrive = 0, PumpLiquidSensor = false, PumpLiquidOk = true,
                PumpUptimeS = 3 * 3600 + 25 * 60, PumpBootCount = 12, PumpResetReason = 1,
                PumpRejCmdId = 0, PumpRejErr = "", PumpDoseActive = false,
            });

            var panel = new OpenTECHub.Controls.ExternalPumpPanel { DataContext = pump, Padding = new Thickness(16) };
            panel.SetResourceReference(Control.BackgroundProperty, "SurfaceHoverBrush");
            // Twice: ScottPlot draws on the first layout pass, after the first capture.
            WpfRenderingHost.RenderElement(panel, 1400, 1003);
            var bitmap = WpfRenderingHost.RenderElement(panel, 1400, 1003); // not a multiple that pins the validator to fixed columns
            Assert.True(pump.NoCurveOnNode, $"online={pump.Status.IsOnline} curve={pump.CurveStatusText} summary={pump.SummaryText}");
            Save(bitmap, "bomba-externa-cartao.png");
            var chart = FindDescendant<OpenTECHub.Controls.PumpPlanChart>(panel);
            Assert.True(chart is not null && chart.ActualWidth > 100 && chart.ActualHeight > 100,
                $"chart={(chart is null ? "null" : $"{chart.ActualWidth}x{chart.ActualHeight} vis={chart.Visibility} preview={(chart.Preview is null ? "null" : "ok")}")}");
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial, $"ratio={metrics.NonZeroPixelRatio} colors={metrics.SampledUniqueColors}");
        });
    }

    [Fact]
    public void External_pump_pulse_panel_is_captured_while_dosing()
    {
        WpfRenderingHost.Run(() =>
        {
            var device = new RecordingDeviceService();
            var settings = new MemorySettingsService();
            settings.Update(x => x with
            {
                PumpControl = x.PumpControl with
                {
                    Dose = x.PumpControl.Dose with
                    {
                        Point = new OpenTECHub.Services.Persistence.PumpDosePointSettings
                        {
                            SpeedUnits = 120, RateMlMin = 5.4, ProfileName = "Silicone 2mm", CapturedAtUtc = "2026-10-11T15:00:00Z",
                        },
                        PeriodS = 300.0,
                    },
                },
            });
            using var pump = new PumpControlViewModel(device, settings);

            // 50 mL in 12 h, pulse 11 of 144 between pulses: the panel opens by itself.
            device.PushTelemetry(new OpenTECHub.Protocol.SensorSnapshot
            {
                HasPumpTelemetry = true, PumpOnline = true, PumpCommEnabled = true,
                PumpVolume = 3.5, PumpFlow = 0.0, PumpMode = 0, PumpTargetVolume = 0.0,
                PumpTransitionSpeed = 500.0, PumpCalCrc = null, PumpCycleVolume = 0.0,
                PumpDrive = 5, PumpLiquidSensor = false, PumpLiquidOk = true,
                PumpUptimeS = 3 * 3600, PumpBootCount = 12, PumpResetReason = 1,
                PumpRejCmdId = 0, PumpRejErr = "",
                PumpDoseActive = true, PumpDosePulse = false, PumpDoseMl = 0.3472, PumpDosePeriodS = 300.0,
                PumpDoseSpeed = 120, PumpDoseRate = 5.4, PumpDoseTotal = 50.0, PumpDoseDeadMs = 0,
                PumpDoseDelivered = 3.472, PumpDosePulses = 10, PumpDoseNextS = 133,
            });
            Assert.True(pump.IsPulseMode);
            Assert.NotNull(pump.Dosing.Plan);

            var panel = new OpenTECHub.Controls.ExternalPumpPanel { DataContext = pump, Padding = new Thickness(16) };
            panel.SetResourceReference(Control.BackgroundProperty, "SurfaceHoverBrush");
            WpfRenderingHost.RenderElement(panel, 1400, 1003);
            var bitmap = WpfRenderingHost.RenderElement(panel, 1400, 1003);
            Save(bitmap, "bomba-externa-pulsos.png");
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial, $"ratio={metrics.NonZeroPixelRatio} colors={metrics.SampledUniqueColors}");
        });
    }

    [Theory]
    [InlineData("flow", "Vazão de ar")]
    [InlineData("biomass", "Sensor de Biomassa")]
    [InlineData("pump", "Bomba Externa")]
    public void Calibration_shortcuts_open_the_named_tab(string target, string header)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new CalibrationView { DataContext = shell.Calibration };
            WpfRenderingHost.RenderElement(view, 1200, 800);
            shell.Calibration.Select(target);
            var tabs = FindDescendant<TabControl>(view);
            Assert.Equal(header, (tabs!.SelectedItem as TabItem)?.Header);
        });
    }

    private static void Save(System.Windows.Media.Imaging.RenderTargetBitmap bitmap, string fileName)
    {
        var path = Path.Combine(EvidenceRoot, fileName);
        WpfRenderingHost.SavePng(bitmap, path);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 1024);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
