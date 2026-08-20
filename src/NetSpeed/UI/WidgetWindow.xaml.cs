using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NetSpeed.Core;
using NetSpeed.Interop;

namespace NetSpeed.UI;

/// <summary>
/// The taskbar readout. It is a top-level always-on-top tool window parked over the empty strip
/// left of the tray area rather than a child of Shell_TrayWnd, so an explorer.exe restart cannot
/// take it down with the taskbar.
/// </summary>
public partial class WidgetWindow : Window
{
    /// <summary>
    /// A layered window hit-tests by pixel alpha, so a fully transparent background would let
    /// clicks fall straight through to the taskbar. Alpha 1 is invisible but still clickable.
    /// </summary>
    private static readonly Brush HitTestBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private readonly Settings _settings;
    private readonly DispatcherTimer _placement;

    private RECT _lastRect;
    private RECT _candidateRect;
    private int _candidateHits;
    private int _fullscreenHits;
    private bool _hidden;
    private bool _dragging;
    private POINT _dragOrigin;
    private RECT _dragStartRect;
    private bool _dragMoved;

    public event Action? TogglePinRequested;
    public event Action? ContextMenuRequested;

    public WidgetWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        _placement = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _placement.Tick += (_, _) => UpdatePlacement();

        Root.MouseEnter += (_, _) => SetHover(true);
        Root.MouseLeave += (_, _) => SetHover(false);
        Root.MouseLeftButtonDown += OnLeftDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnLeftUp;
        Root.MouseRightButtonUp += (_, e) => { e.Handled = true; ContextMenuRequested?.Invoke(); };

        Theme.Changed += ApplyChrome;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this);
        ApplySettings();
        UpdatePlacement();
        _placement.Start();
    }

    // ---------------------------------------------------------------- appearance

    public void ApplySettings()
    {
        double fs = _settings.FontSize;
        double unitFs = Math.Max(8, fs - 2);
        UpValue.FontSize = fs;
        DownValue.FontSize = fs;
        UpArrow.FontSize = fs;
        DownArrow.FontSize = fs;
        UpUnit.FontSize = unitFs;
        DownUnit.FontSize = unitFs;

        // Reserve enough room for the widest unit string so the numbers never shift sideways.
        double unitWidth = Math.Round(unitFs * (_settings.Unit == SpeedUnit.Bits ? 3.2 : 2.95));
        UpUnit.MinWidth = unitWidth;
        DownUnit.MinWidth = unitWidth;

        ApplyChrome();
        _lastRect = default;   // force a reposition on the next tick
        UpdatePlacement();
    }

    private void ApplyChrome()
    {
        if (_settings.DisplayMode == DisplayMode.Floating)
        {
            Root.Background = TryBrush("CardBrush", Brushes.Transparent);
            Root.BorderBrush = TryBrush("CardBorderBrush", Brushes.Transparent);
            Root.BorderThickness = new Thickness(1);
            Root.CornerRadius = new CornerRadius(9);
            Root.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.28, Color = Colors.Black };
            SetTextBrush(TryBrush("TextPrimaryBrush", Brushes.White));
            UpArrow.SetResourceReference(ForegroundProperty, "UpBrush");
            DownArrow.SetResourceReference(ForegroundProperty, "DownBrush");
        }
        else
        {
            Root.Background = HitTestBrush;
            Root.BorderThickness = new Thickness(0);
            Root.CornerRadius = new CornerRadius(6);
            Root.Effect = null;
            UpValue.SetResourceReference(ForegroundProperty, "WidgetTextBrush");
            DownValue.SetResourceReference(ForegroundProperty, "WidgetTextBrush");
            UpUnit.SetResourceReference(ForegroundProperty, "WidgetTextBrush");
            DownUnit.SetResourceReference(ForegroundProperty, "WidgetTextBrush");
            UpArrow.SetResourceReference(ForegroundProperty, "WidgetUpBrush");
            DownArrow.SetResourceReference(ForegroundProperty, "WidgetDownBrush");
        }
    }

    private void SetTextBrush(Brush b)
    {
        UpValue.Foreground = b;
        DownValue.Foreground = b;
        UpUnit.Foreground = b;
        DownUnit.Foreground = b;
    }

    private Brush TryBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private void SetHover(bool on)
    {
        if (_settings.DisplayMode == DisplayMode.Floating) return;
        Root.Background = on ? TryBrush("WidgetHoverBrush", HitTestBrush) : HitTestBrush;
    }

    public void Apply(TrafficSnapshot s)
    {
        var (uv, uu) = Formatter.Speed(s.Up, _settings.Unit);
        var (dv, du) = Formatter.Speed(s.Down, _settings.Unit);
        UpValue.Text = uv;
        UpUnit.Text = uu;
        DownValue.Text = dv;
        DownUnit.Text = du;
    }

    // ---------------------------------------------------------------- placement

    private void UpdatePlacement()
    {
        if (_settings.HideOnFullscreen && WindowHelper.IsFullscreenAppActive())
        {
            // Two consecutive detections before hiding: a taskbar flyout that momentarily reports a
            // monitor-sized rect must not blink the widget out and straight back in.
            if (++_fullscreenHits >= 2)
            {
                SetHidden(true);
                return;
            }
        }
        else
        {
            _fullscreenHits = 0;
        }

        if (_settings.DisplayMode == DisplayMode.Floating)
        {
            SetHidden(false);
            PlaceFloating();
            WindowHelper.BumpToTop(this);
            return;
        }

        var tb = TaskbarLocator.Locate();
        if (tb == null || !Native.IsWindowVisible(tb.Hwnd) || tb.Bounds.IsEmpty)
        {
            SetHidden(true);
            return;
        }

        // An auto-hidden taskbar slides off-screen; follow it instead of floating over content.
        var work = WindowHelper.MonitorInfoFor(new POINT { X = tb.Bounds.Left + 1, Y = tb.Bounds.Top + 1 });
        if (tb.AutoHidden && !work.rcMonitor.Contains(tb.Bounds.Left + 2, tb.Bounds.Top + tb.Bounds.Height / 2))
        {
            SetHidden(true);
            return;
        }

        SetHidden(false);

        var r = TaskbarLocator.ComputeWidgetRect(tb, _settings.WidgetWidth, _settings.WidgetGap, _settings.WidgetOffsetY);
        ApplyTaskbarRect(r);
        WindowHelper.BumpToTop(this);
    }

    /// <summary>
    /// Opening a taskbar flyout reflows the tray area for a moment. Moving on the first sample would
    /// make the widget jump away and back, so a new position has to hold for two samples.
    /// </summary>
    private void ApplyTaskbarRect(RECT r)
    {
        if (SameRect(r, _lastRect))
        {
            _candidateHits = 0;
            return;
        }

        if (_lastRect.IsEmpty)
        {
            WindowHelper.PlacePhysical(this, r);
            _lastRect = r;
            return;
        }

        if (SameRect(r, _candidateRect)) _candidateHits++;
        else { _candidateRect = r; _candidateHits = 1; }

        if (_candidateHits < 2) return;

        WindowHelper.PlacePhysical(this, r);
        _lastRect = r;
        _candidateHits = 0;
    }

    private void PlaceFloating()
    {
        double scale = WindowHelper.ScaleOf(this);
        int w = (int)Math.Round(_settings.WidgetWidth * scale);
        int h = (int)Math.Round(46 * scale);

        if (double.IsNaN(_settings.FloatingX) || double.IsNaN(_settings.FloatingY))
        {
            var mi = WindowHelper.MonitorInfoFor(WindowHelper.CursorPos());
            _settings.FloatingX = mi.rcWork.Right - w - (int)(24 * scale);
            _settings.FloatingY = mi.rcWork.Top + (int)(24 * scale);
        }

        var r = new RECT
        {
            Left = (int)_settings.FloatingX,
            Top = (int)_settings.FloatingY,
            Right = (int)_settings.FloatingX + w,
            Bottom = (int)_settings.FloatingY + h
        };

        if (!SameRect(r, _lastRect))
        {
            WindowHelper.PlacePhysical(this, r);
            _lastRect = r;
        }
    }

    private void SetHidden(bool hide)
    {
        if (hide == _hidden) return;
        _hidden = hide;
        Visibility = hide ? Visibility.Hidden : Visibility.Visible;
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public RECT PhysicalRect => WindowHelper.GetPhysicalRect(this);

    public bool IsShown => !_hidden && Visibility == Visibility.Visible;

    // ---------------------------------------------------------------- input

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragMoved = false;
        if (_settings.DisplayMode != DisplayMode.Floating) return;

        _dragging = true;
        _dragOrigin = WindowHelper.CursorPos();
        _dragStartRect = PhysicalRect;
        Root.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var now = WindowHelper.CursorPos();
        int dx = now.X - _dragOrigin.X;
        int dy = now.Y - _dragOrigin.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;

        _dragMoved = true;
        var r = new RECT
        {
            Left = _dragStartRect.Left + dx,
            Top = _dragStartRect.Top + dy,
            Right = _dragStartRect.Right + dx,
            Bottom = _dragStartRect.Bottom + dy
        };
        WindowHelper.PlacePhysical(this, r);
        _lastRect = r;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();

            if (_dragMoved)
            {
                var r = PhysicalRect;
                _settings.FloatingX = r.Left;
                _settings.FloatingY = r.Top;
                _settings.Save();
                return;
            }
        }

        if (!_dragMoved) TogglePinRequested?.Invoke();
    }

    public void Shutdown()
    {
        _placement.Stop();
        Theme.Changed -= ApplyChrome;
    }
}
