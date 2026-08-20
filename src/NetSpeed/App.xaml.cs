using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using NetSpeed.Core;
using NetSpeed.Interop;
using NetSpeed.UI;
using Forms = System.Windows.Forms;

namespace NetSpeed;

public partial class App : Application
{
    private const string MutexName = "NetSpeed.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private Settings _settings = null!;
    private MonitorService _monitor = null!;
    private WidgetWindow _widget = null!;
    private PopupWindow _popup = null!;
    private SettingsWindow? _settingsWindow;
    private Forms.NotifyIcon? _tray;
    private ContextMenu? _menu;

    private DispatcherTimer _hoverTimer = null!;
    private int _outsideTicks;
    private TaskbarEdge _edge = TaskbarEdge.Bottom;
    private bool _shuttingDown;
    private bool _menuOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Log.Write("DispatcherUnhandledException", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Log.Write("UnobservedTaskException", args.Exception);
        };
        Exit += (_, _) => Log.Info("exit");
        Log.Info($"start (elevated={EtwTrafficMonitor.IsElevated})");

        _settings = Settings.Load();
        Theme.Initialize();

        _monitor = new MonitorService(_settings);
        _monitor.Updated += OnSnapshot;

        _widget = new WidgetWindow(_settings);
        _widget.TogglePinRequested += OnWidgetClicked;
        _widget.ContextMenuRequested += ShowMenu;
        _widget.Show();

        _popup = new PopupWindow(_settings);
        _popup.SettingsRequested += OpenSettings;
        _popup.ElevateRequested += RestartElevated;
        _popup.ExitRequested += ExitApp;

        _settings.Changed += OnSettingsChanged;

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
        _hoverTimer.Tick += (_, _) => TrackHover();
        _hoverTimer.Start();

        ApplyTrayIcon();
        _monitor.Start();
    }

    // ---------------------------------------------------------------- data flow

    private void OnSnapshot(TrafficSnapshot s)
    {
        if (_shuttingDown) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_shuttingDown) return;
            _widget.Apply(s);
            if (_popup.IsFlyoutVisible) _popup.Apply(s);
            if (_tray != null) _tray.Text = BuildTrayText(s);
        });
    }

    private string BuildTrayText(TrafficSnapshot s) =>
        $"NetSpeed\n↓ {Formatter.SpeedText(s.Down, _settings.Unit)}   ↑ {Formatter.SpeedText(s.Up, _settings.Unit)}";

    private void OnSettingsChanged()
    {
        _widget.ApplySettings();
        ApplyTrayIcon();
        if (_popup.IsFlyoutVisible) _popup.Apply(_monitor.Latest);
    }

    // ---------------------------------------------------------------- hover flyout

    private void TrackHover()
    {
        if (_shuttingDown || _menuOpen) return;

        if (!_widget.IsShown)
        {
            if (_popup.IsFlyoutVisible && !_popup.IsPinned) _popup.HideFlyout();
            return;
        }

        var p = WindowHelper.CursorPos();
        bool overWidget = _widget.PhysicalRect.Inflate(2).Contains(p.X, p.Y);
        bool overPopup = _popup.IsFlyoutVisible && _popup.PhysicalRect.Contains(p.X, p.Y);

        if (overWidget || overPopup)
        {
            _outsideTicks = 0;
            if (!_popup.IsFlyoutVisible) ShowFlyout();
            return;
        }

        if (_popup.IsFlyoutVisible && !_popup.IsPinned && ++_outsideTicks >= 2)
            _popup.HideFlyout();
    }

    private void ShowFlyout()
    {
        _popup.Apply(_monitor.Latest);
        _popup.ShowNear(_widget.PhysicalRect, ResolveEdge());
    }

    private TaskbarEdge ResolveEdge()
    {
        if (_settings.DisplayMode == DisplayMode.Floating)
        {
            var r = _widget.PhysicalRect;
            var mi = WindowHelper.MonitorInfoFor(new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 });
            var work = mi.rcWork.IsEmpty ? mi.rcMonitor : mi.rcWork;
            // Drop the card below the widget when it sits in the upper half of the screen.
            return r.Top + r.Height / 2 < work.Top + work.Height / 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        var tb = TaskbarLocator.Locate();
        if (tb != null) _edge = tb.Edge;
        return _edge;
    }

    private void OnWidgetClicked()
    {
        if (_popup.IsPinned)
        {
            _popup.SetPinned(false);
            return;
        }

        if (!_popup.IsFlyoutVisible) ShowFlyout();
        _popup.SetPinned(true);
    }

    // ---------------------------------------------------------------- tray

    private void ApplyTrayIcon()
    {
        if (_settings.ShowTrayIcon)
        {
            if (_tray != null) return;
            _tray = new Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = "NetSpeed"
            };
            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) Dispatcher.BeginInvoke(ShowMenu);
                else if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(OnWidgetClicked);
            };
        }
        else if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = GetResourceStream(uri)?.Stream;
            if (stream != null)
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    // ---------------------------------------------------------------- menu

    private void ShowMenu()
    {
        if (_menu != null) _menu.IsOpen = false;

        // The flyout is topmost; leaving it up would bury the menu behind it.
        _popup.SetPinned(false);
        _popup.HideFlyout();

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("AppMenu"),
            PlacementTarget = _widget,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };

        menu.Items.Add(Menu("显示详情", () => { if (!_popup.IsFlyoutVisible) ShowFlyout(); _popup.SetPinned(true); }));
        menu.Items.Add(Menu("设置…", OpenSettings));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });

        var auto = Menu("开机自动启动", null);
        auto.IsCheckable = true;
        auto.IsChecked = AutoStart.IsEnabled;
        auto.Click += (_, _) => AutoStart.Set(auto.IsChecked);
        menu.Items.Add(auto);

        var floating = Menu(_settings.DisplayMode == DisplayMode.Floating ? "回到任务栏" : "改为悬浮窗", () =>
        {
            _settings.DisplayMode = _settings.DisplayMode == DisplayMode.Floating ? DisplayMode.Taskbar : DisplayMode.Floating;
            _settings.Save();
            _settings.RaiseChanged();
        });
        menu.Items.Add(floating);

        if (!EtwTrafficMonitor.IsElevated)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
            menu.Items.Add(Menu("以管理员身份重启", RestartElevated));
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
        menu.Items.Add(Menu("退出", ExitApp));

        menu.Opened += (_, _) => { _menuOpen = true; RaiseAboveTopmost(menu); };
        menu.Closed += (_, _) => { _menuOpen = false; _outsideTicks = 0; };

        _menu = menu;
        menu.IsOpen = true;
    }

    /// <summary>WPF menus are not topmost by default, so they lose to the always-on-top widget.</summary>
    private static void RaiseAboveTopmost(System.Windows.Media.Visual menu)
    {
        if (PresentationSource.FromVisual(menu) is not HwndSource src || src.Handle == IntPtr.Zero) return;
        Native.SetWindowPos(src.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private MenuItem Menu(string header, Action? action)
    {
        var item = new MenuItem { Header = header };
        if (action != null) item.Click += (_, _) => action();
        return item;
    }

    // ---------------------------------------------------------------- commands

    private void OpenSettings()
    {
        _popup.SetPinned(false);

        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _monitor);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void RestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception)
        {
            // User dismissed the UAC prompt — keep running unelevated.
            _instanceMutex = new Mutex(true, MutexName, out _);
            return;
        }

        ExitApp();
    }

    private void ExitApp()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _hoverTimer?.Stop();
        _widget?.Shutdown();
        _monitor?.Dispose();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _settings?.Save();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _monitor?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
