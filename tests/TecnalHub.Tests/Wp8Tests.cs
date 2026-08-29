using System.IO;
using System.Text.Json;
using System.Windows;
using System.Xml.Linq;
using TecnalHub.Protocol;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>WP8's persistence, keyboard-access and reconnect contracts.</summary>
public sealed class Wp8Tests
{
    [Fact]
    public void Ui_defaults_to_dashboard_without_inventing_window_bounds()
    {
        var ui = new AppSettings().Ui;

        Assert.Equal("dashboard", ui.LastPage);
        Assert.False(ui.Window.HasBounds);
        Assert.False(ui.Window.IsMaximized);
        Assert.DoesNotContain("HasBounds", JsonSerializer.Serialize(new AppSettings()));
    }

    [Fact]
    public void Valid_saved_window_bounds_are_preserved()
    {
        var saved = new WindowPlacementSettings
        {
            Left = 120,
            Top = 80,
            Width = 1280,
            Height = 800,
        };

        var result = WindowPlacementBounds.Resolve(
            saved,
            new Rect(0, 0, 1920, 1040),
            new Size(1280, 800),
            new Size(1024, 640));

        Assert.Equal(new Rect(120, 80, 1280, 800), result);
    }

    [Fact]
    public void Offscreen_saved_window_is_recovered_to_the_visible_area()
    {
        var saved = new WindowPlacementSettings
        {
            Left = 5000,
            Top = 4000,
            Width = 1000,
            Height = 700,
        };

        var result = WindowPlacementBounds.Resolve(
            saved,
            new Rect(-1920, 0, 3840, 1040),
            new Size(1280, 800),
            new Size(1024, 640));

        Assert.Equal(-512, result.Left, precision: 8);
        Assert.Equal(170, result.Top, precision: 8);
        Assert.Equal(1024, result.Width, precision: 8);
        Assert.Equal(700, result.Height, precision: 8);
    }

    [Fact]
    public void Oversized_saved_window_is_clamped_to_current_screen_space()
    {
        var saved = new WindowPlacementSettings
        {
            Left = 0,
            Top = 0,
            Width = 5000,
            Height = 2000,
        };

        var result = WindowPlacementBounds.Resolve(
            saved,
            new Rect(0, 0, 1920, 1040),
            new Size(1280, 800),
            new Size(1024, 640));

        Assert.Equal(new Rect(0, 0, 1920, 1040), result);
    }

    [Fact]
    public void Reconnect_disconnects_an_active_link_then_uses_selected_transport()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings());
        using var vm = new ConnectionViewModel(device, settings)
        {
            State = ConnectionState.Connected,
            UseWiFi = true,
            IpAddress = "192.168.4.7",
        };

        vm.ReconnectCommand.Execute(null);

        Assert.Equal(1, device.DisconnectCalls);
        Assert.Equal("192.168.4.7", Assert.Single(device.WiFiConnections));
        Assert.Equal(0, device.ConnectCalls);
        Assert.Empty(device.UsbConnections);
    }

    [Fact]
    public void Keyboard_focus_contract_uses_a_two_pixel_accent_ring()
    {
        var controlsPath = Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Themes", "Controls.xaml");
        var document = XDocument.Load(controlsPath);
        var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var focusStyle = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute(xNamespace + "Key") == "KeyboardFocusVisualStyle");
        var ring = Assert.Single(focusStyle.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            (string?)element.Attribute("BorderThickness") == "2");

        Assert.Equal("{DynamicResource AccentBrush}", (string?)ring.Attribute("BorderBrush"));

        var references = Directory
            .EnumerateFiles(
                Path.Combine(TestPaths.RepositoryRoot, "src", "TecnalHub"),
                "*.xaml",
                SearchOption.AllDirectories)
            .Sum(path => File.ReadAllText(path)
                .Split("KeyboardFocusVisualStyle", StringSplitOptions.None).Length - 1);
        Assert.True(references >= 12, $"Only {references} keyboard-focus references remain.");
    }

    [Fact]
    public void Command_palette_exposes_search_results_and_keyboard_close_hooks()
    {
        var root = TestPaths.RepositoryRoot;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TecnalHub", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TecnalHub", "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.Name=\"Pesquisar páginas e comandos\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Resultados de comandos\"", xaml);
        Assert.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", xaml);
        Assert.Contains("Closed=\"CommandPalettePopup_Closed\"", xaml);
        Assert.Contains("e.Key == Key.K", code);
        Assert.Contains("e.Key == Key.Escape", code);
        Assert.Contains("e.Key == Key.F5", code);
        Assert.Contains("e.Key == Key.Space", code);
    }

    [Fact]
    public void Navigation_does_not_collapse_to_icon_rail()
    {
        var root = TestPaths.RepositoryRoot;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TecnalHub", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TecnalHub", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"NavigationRail\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"52\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("IsNavigationCompact", xaml, StringComparison.Ordinal);
        Assert.Contains("shell.IsNavigationCompact = false;", code, StringComparison.Ordinal);
    }

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
}
