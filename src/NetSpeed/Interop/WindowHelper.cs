using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetSpeed.Interop;

internal static class WindowHelper
{
    public static IntPtr HandleOf(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>Tool window + never takes focus, so clicking the widget does not steal activation.</summary>
    public static void MakeNonActivating(Window w)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    public static void SetToolWindow(Window w)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
    }

    public static void SetNoActivate(Window w, bool on)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_NOACTIVATE : ex & ~Native.WS_EX_NOACTIVATE;
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
    }

    /// <summary>Move/resize using physical pixels and force the window back to the top of the z-order.</summary>
    public static void PlacePhysical(Window w, RECT r, bool topMost = true)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        Native.SetWindowPos(h, topMost ? Native.HWND_TOPMOST : Native.HWND_TOP,
            r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public static void BumpToTop(Window w)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public static RECT GetPhysicalRect(Window w)
    {
        var h = HandleOf(w);
        if (h != IntPtr.Zero && Native.GetWindowRect(h, out var r)) return r;
        return default;
    }

    public static double ScaleOf(Window w)
    {
        var h = HandleOf(w);
        if (h != IntPtr.Zero)
        {
            var dpi = Native.GetDpiForWindow(h);
            if (dpi >= 48) return dpi / 96.0;
        }
        var src = PresentationSource.FromVisual(w);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    public static POINT CursorPos()
    {
        Native.GetCursorPos(out var p);
        return p;
    }

    public static MONITORINFO MonitorInfoFor(POINT pt)
    {
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        return mi;
    }

    /// <summary>True when a real app is running borderless-fullscreen on the taskbar's monitor.</summary>
    public static bool IsFullscreenAppActive()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(fg, out int pid);
        if (pid == Environment.ProcessId) return false;

        // Taskbar flyouts (tray overflow, start menu, action centre) are XAML island hosts owned by
        // explorer, and several of them are sized to the whole monitor. Treating one as a fullscreen
        // app would hide the widget for a tick and read as a flicker.
        var shell = Native.GetShellWindow();
        if (shell != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(shell, out int shellPid);
            if (shellPid != 0 && pid == shellPid) return false;
        }

        var cls = Native.GetWindowClass(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow"
            or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow")
            return false;

        if (!Native.GetWindowRect(fg, out var r) || r.IsEmpty) return false;

        var mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
