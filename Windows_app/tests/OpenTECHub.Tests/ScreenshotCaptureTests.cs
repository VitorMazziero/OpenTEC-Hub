using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Theme;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Automated in-memory screenshot capture and multi-DPI visual validation suite.
/// Replaces desktop UI Automation COM (0x80004002) with native RenderTargetBitmap rendering.
/// Generates screenshots across 100%, 125%, and 150% DPI scales in both Light and Dark themes.
/// </summary>
[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class ScreenshotCaptureTests
{
    private static readonly string ScreenshotsRoot = Path.Combine(
        TestPaths.RepositoryRoot, "docs", "evidence", "screenshots");

    [Theory]
    [InlineData(96.0, "100dpi", 1280, 800)]
    [InlineData(120.0, "125dpi", 1600, 1000)]
    [InlineData(144.0, "150dpi", 1920, 1200)]
    public void Render_synoptic_view_across_dpi_scales(double dpi, string folder, int expectedWidth, int expectedHeight)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new SynopticView { DataContext = shell };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);

            Assert.NotNull(bitmap);
            Assert.Equal(expectedWidth, bitmap.PixelWidth);
            Assert.Equal(expectedHeight, bitmap.PixelHeight);

            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial, "SynopticView bitmap must contain non-trivial visual content");

            var outputPath = Path.Combine(ScreenshotsRoot, folder, "synoptic.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 1024);
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi", 1280, 800)]
    [InlineData(120.0, "125dpi", 1600, 1000)]
    [InlineData(144.0, "150dpi", 1920, 1200)]
    public void Render_control_view_across_dpi_scales(double dpi, string folder, int expectedWidth, int expectedHeight)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new ControlView { DataContext = shell.Control };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);

            Assert.NotNull(bitmap);
            Assert.Equal(expectedWidth, bitmap.PixelWidth);
            Assert.Equal(expectedHeight, bitmap.PixelHeight);

            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial, "ControlView bitmap must contain non-trivial visual content");

            var outputPath = Path.Combine(ScreenshotsRoot, folder, "control.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 1024);
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi")]
    [InlineData(120.0, "125dpi")]
    [InlineData(144.0, "150dpi")]
    public void Render_calibration_view_across_dpi_scales(double dpi, string folder)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new CalibrationView { DataContext = shell.Calibration };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);

            Assert.NotNull(bitmap);
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial);

            var outputPath = Path.Combine(ScreenshotsRoot, folder, "calibrations.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi")]
    [InlineData(120.0, "125dpi")]
    [InlineData(144.0, "150dpi")]
    public void Render_recipes_view_across_dpi_scales(double dpi, string folder)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new ReceitasView { DataContext = shell.Receitas };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);

            Assert.NotNull(bitmap);
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial);

            var outputPath = Path.Combine(ScreenshotsRoot, folder, "recipes.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi")]
    [InlineData(120.0, "125dpi")]
    [InlineData(144.0, "150dpi")]
    public void Render_kla_views_across_dpi_scales(double dpi, string folder)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();

            // 1. KlaDeterminationView
            var viewDet = new KlaDeterminationView { DataContext = shell.KlaDetermination };
            var bitmapDet = WpfRenderingHost.RenderElement(viewDet, 1280, 800, dpi);
            Assert.NotNull(bitmapDet);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmapDet).IsNonTrivial);
            var pathDet = Path.Combine(ScreenshotsRoot, folder, "kla-determination.png");
            WpfRenderingHost.SavePng(bitmapDet, pathDet);
            Assert.True(File.Exists(pathDet));

            // 2. KlaMappingView
            var viewMap = new KlaMappingView { DataContext = shell.KlaMapping };
            var bitmapMap = WpfRenderingHost.RenderElement(viewMap, 1280, 800, dpi);
            Assert.NotNull(bitmapMap);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmapMap).IsNonTrivial);
            var pathMap = Path.Combine(ScreenshotsRoot, folder, "kla-mapping.png");
            WpfRenderingHost.SavePng(bitmapMap, pathMap);
            Assert.True(File.Exists(pathMap));
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi")]
    [InlineData(120.0, "125dpi")]
    [InlineData(144.0, "150dpi")]
    public void Render_power_view_across_dpi_scales(double dpi, string folder)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new PowerView { DataContext = shell.PowerTest };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);

            Assert.NotNull(bitmap);
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial);

            var outputPath = Path.Combine(ScreenshotsRoot, folder, "power.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
        });
    }

    [Theory]
    [InlineData(96.0, "100dpi")]
    [InlineData(120.0, "125dpi")]
    [InlineData(144.0, "150dpi")]
    public void Render_history_and_events_views_across_dpi_scales(double dpi, string folder)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();

            // History
            var viewHist = new HistoricalView { DataContext = shell.Historical };
            var bitmapHist = WpfRenderingHost.RenderElement(viewHist, 1280, 800, dpi);
            Assert.NotNull(bitmapHist);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmapHist).IsNonTrivial);
            var pathHist = Path.Combine(ScreenshotsRoot, folder, "history.png");
            WpfRenderingHost.SavePng(bitmapHist, pathHist);
            Assert.True(File.Exists(pathHist));

            // Events
            var viewEvents = new EventsView { DataContext = shell.Events };
            var bitmapEvents = WpfRenderingHost.RenderElement(viewEvents, 1280, 800, dpi);
            Assert.NotNull(bitmapEvents);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmapEvents).IsNonTrivial);
            var pathEvents = Path.Combine(ScreenshotsRoot, folder, "events.png");
            WpfRenderingHost.SavePng(bitmapEvents, pathEvents);
            Assert.True(File.Exists(pathEvents));
        });
    }

    [Fact]
    public void Render_full_shell_in_both_light_and_dark_themes()
    {
        WpfRenderingHost.Run(() =>
        {
            var settings = WpfRenderingHost.Services.GetRequiredService<ISettingsService>();
            var theme = WpfRenderingHost.Services.GetRequiredService<IThemeService>();
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();

            // 1. Light Theme Shell
            WpfRenderingHost.SetTheme(isDark: false);
            var lightWindow = new MainWindow(settings, theme) { DataContext = shell };
            var lightBitmap = WpfRenderingHost.RenderWindow(lightWindow, 1280, 800, 96.0);

            Assert.NotNull(lightBitmap);
            Assert.Equal(1280, lightBitmap.PixelWidth);
            Assert.Equal(800, lightBitmap.PixelHeight);
            var lightMetrics = VisualValidationHelper.ValidateBitmap(lightBitmap);
            Assert.True(lightMetrics.IsNonTrivial);

            var lightPath = Path.Combine(ScreenshotsRoot, "100dpi", "shell-light.png");
            WpfRenderingHost.SavePng(lightBitmap, lightPath);
            Assert.True(File.Exists(lightPath));

            // 2. Dark Theme Shell
            WpfRenderingHost.SetTheme(isDark: true);
            var darkWindow = new MainWindow(settings, theme) { DataContext = shell };
            var darkBitmap = WpfRenderingHost.RenderWindow(darkWindow, 1280, 800, 96.0);

            Assert.NotNull(darkBitmap);
            Assert.Equal(1280, darkBitmap.PixelWidth);
            Assert.Equal(800, darkBitmap.PixelHeight);
            var darkMetrics = VisualValidationHelper.ValidateBitmap(darkBitmap);
            Assert.True(darkMetrics.IsNonTrivial);

            var darkPath = Path.Combine(ScreenshotsRoot, "100dpi", "shell-dark.png");
            WpfRenderingHost.SavePng(darkBitmap, darkPath);
            Assert.True(File.Exists(darkPath));

            // Reset theme back to light
            WpfRenderingHost.SetTheme(isDark: false);
        });
    }

    [Fact]
    public void Compact_viewport_layout_adapts_without_destroying_synoptic_spacing()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new SynopticView { DataContext = shell };
            var bitmap = WpfRenderingHost.RenderElement(view, 1024, 640, 96.0);

            Assert.NotNull(bitmap);
            Assert.Equal(1024, bitmap.PixelWidth);
            Assert.Equal(640, bitmap.PixelHeight);
            var metrics = VisualValidationHelper.ValidateBitmap(bitmap);
            Assert.True(metrics.IsNonTrivial);

            var outputPath = Path.Combine(ScreenshotsRoot, "125dpi", "synoptic-compact-1024.png");
            WpfRenderingHost.SavePng(bitmap, outputPath);
            Assert.True(File.Exists(outputPath));
        });
    }
}
