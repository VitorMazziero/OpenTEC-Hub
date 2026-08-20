using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.ViewModels;

namespace TecnalHub;

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
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private IInputElement? _focusBeforePalette;

    public MainWindow()
        : this(settings: null)
    {
    }

    public MainWindow(ISettingsService? settings)
    {
        InitializeComponent();
        _settings = settings;

        ApplySavedPlacement();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void ApplySavedPlacement()
    {
        if (_settings?.Current.Ui.Window is not { HasBounds: true } saved)
        {
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

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedState = WindowState;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
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
        var wide = ActualWidth >= SidePaneMinimumWidth;

        if (DataContext is ViewModels.ShellViewModel shell)
        {
            shell.IsRailAffordable = ActualWidth >= VariableRailMinimumWidth;
            shell.IsNavigationCompact = ActualWidth < VariableRailMinimumWidth;
        }

        // The HOSTS are toggled, never the panes inside them. Each host holds both a
        // controllable and a read-only pane, and which one shows is a data question
        // answered by a binding - assigning Visibility on those panes from here would
        // replace the binding with a local value and permanently break it.
        SideDetailHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = wide ? new GridLength(380) : new GridLength(0);

        DrawerDetailHost.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        DrawerRow.Height = wide ? new GridLength(0) : new GridLength(300);
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
