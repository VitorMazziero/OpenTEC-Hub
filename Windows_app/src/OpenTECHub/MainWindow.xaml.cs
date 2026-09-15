using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;

namespace OpenTECHub;

/// <summary>
/// Shell window. Hosts the KPI strip, the navigation rail and the active page.
/// </summary>
/// <remarks>
/// Code-behind holds no logic: state and commands live in
/// <see cref="ViewModels.ShellViewModel"/>. The only thing here is the responsive
/// switch, which is a visual-tree concern XAML cannot express - WPF has no media
/// queries, and a VisualStateManager would need the same width threshold written
/// twice.
/// </remarks>
public partial class MainWindow : Window
{
    private static readonly Size DefaultWindowSize = new(1280, 800);

    /// <summary>
    /// Below this width the detail pane moves under the synoptic.
    /// </summary>
    /// <remarks>
    /// Side by side, the pane needs ~380 px and the diagram stops being readable
    /// under ~700 px. See <c>docs/UI_DESIGN.md</c>, "Responsive behaviour".
    /// </remarks>
    private const double SidePaneMinimumWidth = 1200;

    /// <summary>
    /// Below this width the variable rail folds away.
    /// </summary>
    /// <remarks>
    /// Nav rail, variable rail, synoptic and detail pane all want horizontal space; at
    /// four columns the diagram is the one that suffers, and the diagram is what makes
    /// this a bioreactor application. The operator's preference is untouched - the rail
    /// returns on its own once there is room. See <c>docs/UI_DESIGN.md</c> section 4.7.
    /// </remarks>
    private const double VariableRailMinimumWidth = 1400;

    private readonly ISettingsService? _settings;
    private readonly IThemeService? _themeService;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private IInputElement? _focusBeforePalette;

    public MainWindow()
        : this(settings: null, themeService: null)
    {
    }

    public MainWindow(ISettingsService? settings, IThemeService? themeService = null)
    {
        InitializeComponent();
        _settings = settings;
        _themeService = themeService;

        if (_themeService is not null)
        {
            _themeService.ThemeChanged += OnThemeChanged;
        }

        // Custom chrome: the OS caption is gone, so a maximized window must be told to
        // stop at the work area instead of overhanging its edges and the taskbar.
        WindowChromeMaximizeFix.Enable(this);

        ApplySavedPlacement();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void OnThemeChanged(bool isDark)
    {
        // Standard Window chrome reacts poorly to dark mode without explicit overrides
        // when using the default OS chrome (we use custom chrome later, but this
        // ensures native dialogs spawned by this window don't blind the user).
        var dark = isDark ? 1 : 0;
        DwmSetWindowAttribute(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            20, // DWMWA_USE_IMMERSIVE_DARK_MODE
            ref dark,
            sizeof(int));
    }

    private void ApplySavedPlacement()
    {
        if (_settings?.Current.Ui.Window is not { HasBounds: true } saved)
        {
            // No remembered placement (first run). On a traditional laptop panel the
            // 1280×800 default barely fits — or overhangs a 768 px-tall screen — so open
            // maximized there and leave larger desktop monitors on the windowed default.
            // Once a placement is saved, that operator choice is what gets restored.
            if (ShouldStartMaximizedForSmallScreen())
            {
                _lastNonMinimizedState = WindowState.Maximized;
                WindowState = WindowState.Maximized;
            }

            return;
        }

        var available = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var bounds = WindowPlacementBounds.Resolve(
            saved,
            available,
            DefaultWindowSize,
            new Size(MinWidth, MinHeight));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        if (saved.IsMaximized)
        {
            _lastNonMinimizedState = WindowState.Maximized;
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// True on a traditional-laptop-sized panel, where the app should open maximized.
    /// </summary>
    /// <remarks>
    /// Measured against the primary work area in device-independent units, so display
    /// scaling counts: a 1920×1080 panel at 150 % has the effective room of a small
    /// screen and is treated as one. The 1600×900 bound catches 1366×768, 1440×900 and
    /// 1536×864 laptops while leaving 1080p-and-larger desktop monitors windowed.
    /// </remarks>
    private static bool ShouldStartMaximizedForSmallScreen()
    {
        var work = SystemParameters.WorkArea;
        return work.Width <= 1600 || work.Height <= 900;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedState = WindowState;
        }
    }

    // ── Custom caption buttons ───────────────────────────────────────────────
    // The window has no OS caption, so the header draws its own controls.

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreWindow(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnDrawerBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.IsNavDrawerOpen = false;
        }
    }

    /// <summary>
    /// Criteria typed into the capture-settings dialog and Montagem fields only reach the assay on
    /// disk through <c>Salvar Preferências</c>; closing the application with them unsaved used to lose them
    /// silently (bench of 2026-09-11). Returns false when the operator cancels the exit.
    /// </summary>
    private bool ConfirmUnsavedAssaySetup()
    {
        if (DataContext is not ShellViewModel { PowerTest: { } power } || !power.HasUnsavedSetup)
        {
            return true;
        }

        var answer = MessageBox.Show(this,
            "O setup do ensaio de potência tem alterações não salvas.\n\nSalvar o setup do ensaio antes de sair?",
            "Ensaio de potência", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (!power.TrySaveSetupForExit(out var error))
                {
                    MessageBox.Show(this, $"Não foi possível salvar o setup: {error}", "Ensaio de potência",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case MessageBoxResult.No:
                return true;
            default:
                return false;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmUnsavedAssaySetup())
        {
            e.Cancel = true;
            return;
        }

        if (_settings is null)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized ||
                        (WindowState == WindowState.Minimized &&
                         _lastNonMinimizedState == WindowState.Maximized);
        _settings.Update(settings => settings with
        {
            Ui = settings.Ui with
            {
                Window = new WindowPlacementSettings
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    IsMaximized = maximized,
                },
            },
        });
    }

    private void ApplyResponsiveLayout()
    {
        if (DataContext is ViewModels.ShellViewModel shell)
        {
            shell.IsRailAffordable = ActualWidth >= VariableRailMinimumWidth;
            shell.IsNavigationCompact = ActualWidth < 1440;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var control = modifiers.HasFlag(ModifierKeys.Control);
        if (control && TryNavigationIndex(e.Key, out var index))
        {
            if (index < shell.NavigationItems.Count)
            {
                shell.NavigateCommand.Execute(shell.NavigationItems[index].Id);
                e.Handled = true;
            }

            return;
        }

        if (control && e.Key == Key.K)
        {
            OpenCommandPalette(shell);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.R)
        {
            shell.ToggleVariableRailCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
        {
            // The shortcut is reserved now so Phase 3 can add the recipe editor without
            // changing the operator map. The palette explains why it is unavailable.
            OpenCommandPalette(shell, "Salvar receita");
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.None && e.Key == Key.F5)
        {
            shell.Connection.ReconnectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            if (shell.IsNavDrawerOpen)
            {
                shell.IsNavDrawerOpen = false;
                e.Handled = true;
                return;
            }
            if (shell.IsCommandPaletteOpen)
            {
                CloseCommandPalette(shell);
                e.Handled = true;
            }
            else if (ConnectionChip.IsChecked == true)
            {
                ConnectionChip.IsChecked = false;
                e.Handled = true;
            }
            else if (shell.SelectedNavigationId == "dashboard" && shell.SelectedVariable is not null)
            {
                shell.ClearSelectionCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (modifiers == ModifierKeys.None &&
            e.Key == Key.Space &&
            shell.SelectedNavigationId == "charts" &&
            Keyboard.FocusedElement is not (
                TextBoxBase or ButtonBase or Selector or ListBoxItem or TabItem or DataGridCell))
        {
            shell.Charts.TogglePauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool TryNavigationIndex(Key key, out int zeroBasedIndex)
    {
        zeroBasedIndex = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1,
        };
        return zeroBasedIndex >= 0;
    }

    private void OpenCommandPalette_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            OpenCommandPalette(shell);
        }
    }

    private void OpenCommandPalette(ShellViewModel shell, string? query = null)
    {
        _focusBeforePalette = Keyboard.FocusedElement;
        shell.OpenCommandPaletteCommand.Execute(null);
        if (query is not null)
        {
            shell.CommandSearchText = query;
        }

        Dispatcher.BeginInvoke(
            () => CommandPaletteSearchBox.Focus(),
            DispatcherPriority.Input);
    }

    private void CommandPalettePopup_Opened(object sender, EventArgs e)
    {
        CommandPaletteSearchBox.Focus();
        CommandPaletteSearchBox.SelectAll();
    }

    private void CommandPalettePopup_Closed(object sender, EventArgs e) => RestorePaletteFocus();

    private void CommandPaletteSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ExecuteSelectedPaletteEntry(shell);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && shell.CommandPaletteResults.Count > 0)
        {
            CommandPaletteResults.Focus();
            if (shell.SelectedCommandPaletteEntry is { } selected)
            {
                CommandPaletteResults.ScrollIntoView(selected);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseCommandPalette(shell);
            e.Handled = true;
        }
    }

    private void CommandPaletteResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ExecuteSelectedPaletteEntry(shell);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseCommandPalette(shell);
            e.Handled = true;
        }
    }

    private void CommandPaletteResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            ExecuteSelectedPaletteEntry(shell);
        }
    }

    private void ExecuteSelectedPaletteEntry(ShellViewModel shell)
    {
        var entry = shell.SelectedCommandPaletteEntry;
        shell.ExecuteCommandPaletteEntryCommand.Execute(entry);
        if (!shell.IsCommandPaletteOpen)
        {
            RestorePaletteFocus();
        }
    }

    private void CloseCommandPalette(ShellViewModel shell)
    {
        shell.CloseCommandPaletteCommand.Execute(null);
        RestorePaletteFocus();
    }

    private void RestorePaletteFocus()
    {
        if (_focusBeforePalette is not { } target)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => Keyboard.Focus(target), DispatcherPriority.Input);
        _focusBeforePalette = null;
    }
}
