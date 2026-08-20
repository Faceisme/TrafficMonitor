using System.Windows.Threading;

namespace NetSpeed.Interop;

/// <summary>
/// Signals when the shell reshuffles the top of the z-order.
///
/// Opening a taskbar flyout (the tray overflow, for instance) raises Shell_TrayWnd within the
/// topmost band, which buries the always-on-top readout. Restoring z-order only on a polling timer
/// leaves the readout covered for up to a full tick, which reads as a flicker; a WinEvent hook cuts
/// that to a few milliseconds.
/// </summary>
internal sealed class ZOrderWatcher : IDisposable
{
    private readonly Native.WinEventProc _callback;   // must outlive the hook
    private readonly List<IntPtr> _hooks = new();
    private readonly Action _onChanged;
    private readonly DispatcherTimer _settle;

    private DateTime _lastRaised = DateTime.MinValue;
    private bool _disposed;

    public ZOrderWatcher(Action onChanged)
    {
        _onChanged = onChanged;
        _callback = OnWinEvent;

        // A second nudge after the shell finishes its own repositioning.
        _settle = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(30) };
        _settle.Tick += (_, _) => { _settle.Stop(); if (!_disposed) _onChanged(); };

        uint shellPid = 0;
        var shell = Native.GetShellWindow();
        if (shell != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(shell, out int pid);
            if (pid > 0) shellPid = (uint)pid;
        }

        // Scoped to explorer: the taskbar raising itself is the case that matters, and a global
        // reorder hook would fire constantly. SHOW/HIDE are in the range because a flyout closing
        // reshuffles the band without always emitting a reorder first.
        Hook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_REORDER, shellPid);

        // Any app coming to the foreground can also land above us.
        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0);
    }

    private void Hook(uint min, uint max, uint pid)
    {
        var h = Native.SetWinEventHook(min, max, IntPtr.Zero, _callback, pid, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_disposed) return;

        // Reorder events arrive in bursts; one restore per burst is enough.
        var now = DateTime.UtcNow;
        if ((now - _lastRaised).TotalMilliseconds < 4) return;
        _lastRaised = now;

        _onChanged();

        _settle.Stop();
        _settle.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settle.Stop();
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
