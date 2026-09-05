using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenTECHub.Services.Platform;

/// <summary>
/// Makes a custom-chrome (<see cref="System.Windows.Shell.WindowChrome"/>) window maximize to
/// the monitor's <b>work area</b> instead of overhanging its edges and covering the taskbar.
/// </summary>
/// <remarks>
/// <para>
/// Once the OS caption is removed, a maximized WPF window sizes itself to the full monitor plus
/// the resize border, so its edges — and with them the custom minimise/close buttons — spill a
/// few pixels off every side and the taskbar disappears underneath. Windows asks the window for
/// its maximized bounds through <c>WM_GETMINMAXINFO</c>; answering with the work area of the
/// nearest monitor fixes both, and does the right thing on a multi-monitor desk.
/// </para>
/// <para>
/// This is deliberately the only native interop in the shell. It touches no application state,
/// so it lives here rather than in the window code-behind.
/// </para>
/// </remarks>
public static class WindowChromeMaximizeFix
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>Hooks <paramref name="window"/> so its maximized state respects the work area.</summary>
    public static void Enable(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        };
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo && TryConstrainToWorkArea(hwnd, lParam))
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool TryConstrainToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.Work;
        var full = info.Monitor;

        // Position/size are expressed relative to the monitor's own origin, not the desktop.
        mmi.MaxPosition = new Point { X = work.Left - full.Left, Y = work.Top - full.Top };
        mmi.MaxSize = new Point { X = work.Right - work.Left, Y = work.Bottom - work.Top };

        // MaxTrackSize is deliberately left untouched: it bounds interactive resizing, and
        // clamping it to the work area would stop the operator dragging the window larger.
        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }
}
