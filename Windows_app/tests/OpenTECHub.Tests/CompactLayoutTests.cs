using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Controls;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Layout acceptance for the supported minimum window. Every destination is arranged in
/// the content area a 1024 x 640 DIP window actually leaves - the rail, title bar and
/// status bar are already subtracted - and nothing operational may be pushed past the
/// right edge. Sideways scrolling is not an escape hatch here: no page in this shell
/// scrolls horizontally, so content past the edge is content the operator cannot read.
/// </summary>
public sealed class CompactLayoutTests
{
    /// <summary>
    /// Content width left by the smallest supported window: 1024 DIP less the 56 DIP
    /// compact navigation rail and the page margins the shell applies around a page.
    /// </summary>
    private const double CompactContentWidth = 936;

    /// <summary>
    /// Content height left by the smallest supported window: 640 DIP less the 48 DIP
    /// title bar, the 34 DIP status bar and the page margins.
    /// </summary>
    private const double CompactContentHeight = 534;

    public static TheoryData<string> Destinations() => new(
    [
        "synoptic",
        "control",
        "recipes",
        "kla-determination",
        "kla-mapping",
        "power",
        "power-map",
        "power-impeller-comparison",
        "history",
        "events",
        "calibration",
        "settings",
    ]);

    [Theory]
    [MemberData(nameof(Destinations))]
    public void Destination_fits_the_minimum_window_without_losing_content(string destination)
    {
        var issues = WpfRenderingHost.Run(() =>
        {
            var view = CreateView(destination);
            return ArrangeAndInspect(view, CompactContentWidth, CompactContentHeight);
        });

        Assert.True(
            issues.Count == 0,
            $"{destination} at {CompactContentWidth} x {CompactContentHeight} DIP:{Environment.NewLine}"
            + string.Join(Environment.NewLine, issues.Take(25)));
    }

    [Theory]
    [MemberData(nameof(Destinations))]
    public void Destination_fits_a_wide_window_without_losing_content(string destination)
    {
        var issues = WpfRenderingHost.Run(() =>
        {
            var view = CreateView(destination);
            return ArrangeAndInspect(view, 1680, 980);
        });

        Assert.True(
            issues.Count == 0,
            $"{destination} at 1680 x 980 DIP:{Environment.NewLine}"
            + string.Join(Environment.NewLine, issues.Take(25)));
    }

    [Theory]
    [InlineData(936.0, 534.0)]
    [InlineData(1680.0, 980.0)]
    public void External_pump_calibration_tab_fits_supported_windows(double width, double height)
    {
        var issues = WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            shell.Calibration.SelectedTabIndex = 4;
            try
            {
                var view = new CalibrationView { DataContext = shell.Calibration };
                return ArrangeAndInspect(view, width, height);
            }
            finally
            {
                shell.Calibration.SelectedTabIndex = 0;
            }
        });

        Assert.True(
            issues.Count == 0,
            $"external-pump calibration at {width} x {height} DIP:{Environment.NewLine}"
            + string.Join(Environment.NewLine, issues.Take(25)));
    }

    /// <summary>
    /// The three stacked kLa plots used to demand 620 DIP of height whatever the window
    /// offered. On a short page one plot is picked from a selector and gets the whole
    /// area, instead of three bands too short to carry an axis.
    /// </summary>
    [Fact]
    public void Kla_determination_shows_one_plot_at_a_time_when_the_page_is_short()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new KlaDeterminationView { DataContext = shell.KlaDetermination };

            var (shortHosts, shortHeight) = ArrangeAndMeasurePlots(view, CompactContentWidth, CompactContentHeight);
            Assert.Equal(1, shortHosts);
            Assert.True(
                shortHeight >= 190,
                $"The single visible plot got {shortHeight:F0} DIP, less than the 190 DIP floor.");

            var (tallHosts, _) = ArrangeAndMeasurePlots(view, 1680, 980);
            Assert.Equal(3, tallHosts);
        });
    }

    /// <summary>
    /// The compact kLa-map selector has to hand the chosen section the whole row. A local
    /// Width attribute silently outranks a trigger setter in WPF, so the section would
    /// still be pinned to its 300 DIP column with the rest of the page left blank -
    /// measuring the arranged width is the only way to see the difference.
    /// </summary>
    [Theory]
    [InlineData("MapTabData")]
    [InlineData("MapTabSurface")]
    [InlineData("MapTabDiagnostics")]
    public void Kla_mapping_compact_section_takes_the_whole_row(string tabName)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new KlaMappingView { DataContext = shell.KlaMapping };

            var host = new Border { Width = CompactContentWidth, Height = CompactContentHeight, Child = view };
            var window = new Window
            {
                Width = CompactContentWidth,
                Height = CompactContentHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Content = host,
            };

            try
            {
                window.Show();
                WpfRenderingHost.PumpDispatcher();
                host.UpdateLayout();
                WpfRenderingHost.PumpDispatcher();

                var tab = (RadioButton)view.FindName(tabName);
                tab.IsChecked = true;
                host.UpdateLayout();
                WpfRenderingHost.PumpDispatcher();

                var body = (FrameworkElement)view.FindName("MapBody");
                Assert.True(
                    Controls.Responsive.GetIsNarrow(body),
                    "The body should report narrow at the minimum window width.");

                var sections = VisibleSections(body).ToList();
                Assert.Single(sections);
                Assert.True(
                    sections[0].ActualWidth >= body.ActualWidth - 2,
                    $"{tabName} left the section at {sections[0].ActualWidth:F0} of {body.ActualWidth:F0} DIP.");
            }
            finally
            {
                window.Close();
                host.Child = null;
            }
        });
    }

    /// <summary>
    /// The three top-level section containers of the kLa map body, in the order they sit
    /// in the row: experiments card, surface charts, diagnostics card.
    /// </summary>
    private static IEnumerable<FrameworkElement> VisibleSections(FrameworkElement body)
    {
        var row = (Grid)VisualTreeHelper.GetChild(body, 1);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(row); i++)
        {
            if (VisualTreeHelper.GetChild(row, i) is FrameworkElement { IsVisible: true } child)
            {
                yield return child;
            }
        }
    }

    private static (int VisiblePlots, double TallestPlot) ArrangeAndMeasurePlots(
        FrameworkElement view,
        double width,
        double height)
    {
        var host = new Border { Width = width, Height = height, Child = view };
        var window = new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Content = host,
        };

        try
        {
            window.Show();
            WpfRenderingHost.PumpDispatcher();
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            WpfRenderingHost.PumpDispatcher();
            host.UpdateLayout();
            WpfRenderingHost.PumpDispatcher();

            var hosts = new[] { "ChartDoHost", "ChartLogLinearHost", "ChartInstantKlaHost" }
                .Select(name => view.FindName(name) as FrameworkElement)
                .Where(element => element is { IsVisible: true })
                .ToList();

            return (hosts.Count, hosts.Count == 0 ? 0 : hosts.Max(element => element!.ActualHeight));
        }
        finally
        {
            window.Close();
            host.Child = null;
        }
    }

    private static List<string> ArrangeAndInspect(FrameworkElement view, double width, double height)
    {
        // A Border host mirrors how the shell hands a page its slice of the window, and
        // gives CheckHorizontalOverflow a root whose width is the real usable area.
        var host = new Border
        {
            Width = width,
            Height = height,
            Child = view,
        };

        var window = new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Content = host,
        };

        try
        {
            window.Show();
            WpfRenderingHost.PumpDispatcher();

            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            WpfRenderingHost.PumpDispatcher();

            // A second pass matters: Responsive publishes its flags from SizeChanged, so
            // the triggers that collapse a panel only take effect on the layout after the
            // first arrange - exactly as they do when the operator drags the window.
            host.UpdateLayout();
            WpfRenderingHost.PumpDispatcher();

            var issues = VisualValidationHelper.CheckHorizontalOverflow(host);
            issues.AddRange(VisualValidationHelper.CheckTextTruncation(host));
            return issues;
        }
        finally
        {
            window.Close();
            host.Child = null;
        }
    }

    private static FrameworkElement CreateView(string destination)
    {
        var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();

        return destination switch
        {
            "synoptic" => new SynopticView { DataContext = shell },
            "control" => new ControlView { DataContext = shell.Control },
            "recipes" => new ReceitasView { DataContext = shell.Receitas },
            "kla-determination" => new KlaDeterminationView { DataContext = shell.KlaDetermination },
            "kla-mapping" => new KlaMappingView { DataContext = shell.KlaMapping },
            "power" => new PowerView { DataContext = shell.PowerTest },
            "power-map" => new PowerMapView { DataContext = shell.PowerMap },
            "power-impeller-comparison" => new PowerImpellerComparisonView { DataContext = shell.PowerMap.Comparison },
            "history" => new HistoricalView { DataContext = shell.Historical },
            "events" => new EventsView { DataContext = shell.Events },
            "calibration" => new CalibrationView { DataContext = shell.Calibration },
            "settings" => new SettingsView { DataContext = shell.Settings },
            _ => throw new ArgumentOutOfRangeException(nameof(destination), destination, "Unknown destination."),
        };
    }
}
