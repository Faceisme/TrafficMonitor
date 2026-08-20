namespace NetSpeed.Interop;

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>Snapshot of where the primary taskbar is and where its tray area starts.</summary>
internal sealed class TaskbarInfo
{
    public IntPtr Hwnd;
    public RECT Bounds;
    /// <summary>Rect of TrayNotifyWnd (clock + tray icons). Empty when it could not be found.</summary>
    public RECT TrayArea;
    public TaskbarEdge Edge;
    public uint Dpi = 96;
    public bool AutoHidden;
    public double Scale => Dpi / 96.0;
}

internal static class TaskbarLocator
{
    public static TaskbarInfo? Locate()
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        if (!Native.GetWindowRect(tray, out var bounds) || bounds.IsEmpty) return null;

        var info = new TaskbarInfo { Hwnd = tray, Bounds = bounds };

        // TrayNotifyWnd holds the tray icons + clock; we dock immediately to its left.
        var notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && !nr.IsEmpty)
            info.TrayArea = nr;

        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            info.Edge = (TaskbarEdge)abd.uEdge;
        else
            info.Edge = bounds.Height < bounds.Width ? TaskbarEdge.Bottom : TaskbarEdge.Left;

        var state = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        info.AutoHidden = (Native.SHAppBarMessage(Native.ABM_GETSTATE, ref state).ToInt64() & Native.ABS_AUTOHIDE) != 0;

        var dpi = Native.GetDpiForWindow(tray);
        if (dpi >= 48) info.Dpi = dpi;

        return info;
    }

    /// <summary>Where the widget should sit, in physical pixels.</summary>
    public static RECT ComputeWidgetRect(TaskbarInfo tb, int widthDip, int gapDip, int offsetYDip)
    {
        var scale = tb.Scale;
        int w = (int)Math.Round(widthDip * scale);
        int gap = (int)Math.Round(gapDip * scale);
        int dy = (int)Math.Round(offsetYDip * scale);

        bool horizontal = tb.Edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
        if (horizontal)
        {
            int right = tb.TrayArea.IsEmpty ? tb.Bounds.Right - (int)(180 * scale) : tb.TrayArea.Left;
            int left = right - gap - w;
            if (left < tb.Bounds.Left) left = tb.Bounds.Left;
            return new RECT
            {
                Left = left,
                Top = tb.Bounds.Top + dy,
                Right = left + w,
                Bottom = tb.Bounds.Bottom + dy
            };
        }

        // Vertical taskbar (Windows 10 only): stack above the tray area.
        int h = (int)Math.Round(36 * scale);
        int bottom = tb.TrayArea.IsEmpty ? tb.Bounds.Bottom - (int)(120 * scale) : tb.TrayArea.Top;
        int top = bottom - gap - h;
        return new RECT
        {
            Left = tb.Bounds.Left,
            Top = top,
            Right = tb.Bounds.Right,
            Bottom = top + h
        };
    }
}
