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

    /// <summary>The whole monitor the window is on, in device-independent units, or empty.</summary>
    public static System.Windows.Rect MonitorBounds(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        var monitor = handle == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return System.Windows.Rect.Empty;
        }

        var pixels = new System.Windows.Rect(
            info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        return fromDevice is { } transform ? System.Windows.Rect.Transform(pixels, transform) : pixels;
    }

    /// <summary>
    /// Tells the taskbar that <paramref name="window"/> is (or no longer is) a full-screen app, so
    /// it drops behind the window while it is active. Call after the window has its final size.
    /// </summary>
    /// <remarks>
    /// The shell's own guess does not re-run when an open window changes style and size, and the
    /// taskbar stayed drawn over the app's bottom bar.
    /// </remarks>
    public static void MarkTaskbarFullScreen(Window window, bool fullScreen)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var taskbar = (ITaskbarList2)new TaskbarList();
            taskbar.HrInit();
            taskbar.MarkFullscreenWindow(handle, fullScreen);
            Marshal.ReleaseComObject(taskbar);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No shell taskbar (kiosk, remote session): the window still covers the monitor.
        }
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

        // Enforce MinTrackSize based on Window.MinWidth and MinHeight, scaled to physical device pixels.
        // Handled = true bypasses DefWindowProc, so MinTrackSize must be explicitly populated here.
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window window)
        {
            var dpiX = 1.0;
            var dpiY = 1.0;
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            else
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    dpiX = dpi / 96.0;
                    dpiY = dpi / 96.0;
                }
            }

            if (window.MinWidth > 0)
            {
                mmi.MinTrackSize.X = (int)Math.Ceiling(window.MinWidth * dpiX);
            }
            if (window.MinHeight > 0)
            {
                mmi.MinTrackSize.Y = (int)Math.Ceiling(window.MinHeight * dpiY);
            }
        }

        // MaxTrackSize is deliberately left untouched: it bounds interactive resizing, and
        // clamping it to the work area would stop the operator dragging the window larger.
        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        return true;
    }

    [ComImport]
    [Guid("602D4995-B13A-429b-A66E-1935E44F4317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
