using System;
using System.Windows;
using System.Windows.Media;

namespace OpenTECHub.Views.Dialogs;

/// <summary>Destructive confirmation that defaults to cancellation.</summary>
public partial class DestructiveConfirmationDialog : Window
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public DestructiveConfirmationDialog(
        string title,
        string consequence,
        string? exactCommand = null,
        string confirmText = "Confirmar",
        string cancelText = "Cancelar",
        bool isDanger = true)
    {
        InitializeComponent();
        var hasExactCommand = !string.IsNullOrWhiteSpace(exactCommand);
        var resolvedConfirmText = confirmText != "Confirmar"
            ? confirmText
            : (hasExactCommand ? "Confirmar parada" : "Confirmar");

        var hasCancel = !string.IsNullOrWhiteSpace(cancelText);

        DataContext = new DialogContent(
            title,
            consequence,
            exactCommand ?? "",
            hasExactCommand,
            resolvedConfirmText,
            cancelText,
            hasCancel,
            isDanger);

        if (isDanger)
        {
            if (TryFindResource("DangerButtonStyle") is Style dangerStyle)
            {
                ConfirmBtn.Style = dangerStyle;
            }
        }
        else
        {
            if (TryFindResource("AccentButtonStyle") is Style accentStyle)
            {
                ConfirmBtn.Style = accentStyle;
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var isDark = Application.Current?.Resources["SurfaceBaseBrush"] is SolidColorBrush brush
                         && (brush.Color.R + brush.Color.G + brush.Color.B) / 3 < 128;
            var dark = isDark ? 1 : 0;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Best effort
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private sealed record DialogContent(
        string DialogTitle,
        string Consequence,
        string ExactCommand,
        bool HasExactCommand,
        string ConfirmText,
        string CancelText,
        bool HasCancelButton,
        bool IsDanger);
}
