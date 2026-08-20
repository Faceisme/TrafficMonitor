using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Animation;
using NetSpeed.Core;
using NetSpeed.Interop;

namespace NetSpeed.UI;

/// <summary>The hover flyout: live totals plus the processes moving the most bytes right now.</summary>
public partial class PopupWindow : Window
{
    private const double CardMarginLeft = 16;
    private const double CardMarginTop = 12;
    private const double CardMarginBottom = 16;

    private readonly Settings _settings;
    private readonly ObservableCollection<ProcessRowVm> _rows = new();

    private bool _visible;
    private bool _closing;
    private RECT _anchor;
    private TaskbarEdge _edge = TaskbarEdge.Bottom;

    public event Action? SettingsRequested;
    public event Action? ElevateRequested;
    public event Action? ExitRequested;

    public bool IsPinned { get; private set; }

    public PopupWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        ProcessList.ItemsSource = _rows;

        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        ElevateButton.Click += (_, _) => ElevateRequested?.Invoke();
        ExitButton.Click += (_, _) => ExitRequested?.Invoke();

        Deactivated += (_, _) => { if (IsPinned) SetPinned(false); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this);
    }

    // ---------------------------------------------------------------- content

    public void Apply(TrafficSnapshot s)
    {
        var (dv, du) = Formatter.Speed(s.Down, _settings.Unit);
        var (uv, uu) = Formatter.Speed(s.Up, _settings.Unit);
        DownValue.Text = dv;
        DownUnit.Text = du;
        UpValue.Text = uv;
        UpUnit.Text = uu;

        AdapterLabel.Text = s.AdapterName;
        ListTitle.Text = $"进程占用 · 前 {_settings.TopCount}";

        bool etwOk = s.EtwState == EtwState.Running;
        bool needsAdmin = s.EtwState == EtwState.NeedsAdmin;

        AdminHint.Visibility = etwOk ? Visibility.Collapsed : Visibility.Visible;
        if (!etwOk)
        {
            AdminDetail.Text = needsAdmin
                ? "Windows 只在内核事件跟踪中暴露每个进程的收发字节。"
                : s.EtwError ?? "跟踪会话未能启动。";
            ElevateButton.Visibility = needsAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncRows(etwOk ? s.Processes : Array.Empty<ProcessRateRow>());

        bool empty = etwOk && _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ProcessList.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_visible)
        {
            // The row count can change between ticks; measure before repositioning.
            UpdateLayout();
            Reposition();
        }
    }

    /// <summary>Updates rows in place so the list does not rebuild (and flicker) on every tick.</summary>
    private void SyncRows(IReadOnlyList<ProcessRateRow> rows)
    {
        double max = rows.Count > 0 ? rows.Max(r => r.Total) : 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i < _rows.Count)
            {
                if (_rows[i].Key != row.Key)
                {
                    _rows[i] = new ProcessRowVm(row.Key);
                }
            }
            else
            {
                _rows.Add(new ProcessRowVm(row.Key));
            }
            _rows[i].Update(row, _settings.Unit, max);
        }

        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    // ---------------------------------------------------------------- show / hide

    public void ShowNear(RECT anchor, TaskbarEdge edge)
    {
        _anchor = anchor;
        _edge = edge;

        if (_visible)
        {
            Reposition();
            return;
        }

        _visible = true;
        _closing = false;

        Card.Opacity = 0;
        Visibility = Visibility.Visible;
        if (!IsVisible) Show();

        UpdateLayout();
        Reposition();
        WindowHelper.BumpToTop(this);

        double from = _edge == TaskbarEdge.Top ? -8 : 8;
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }

    public void HideFlyout()
    {
        if (!_visible || _closing) return;
        _closing = true;
        IsPinned = false;
        WindowHelper.SetNoActivate(this, true);

        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (!_closing) return;
            _visible = false;
            _closing = false;
            Visibility = Visibility.Hidden;
        };
        Card.BeginAnimation(OpacityProperty, fade);
    }

    public void SetPinned(bool pinned)
    {
        if (IsPinned == pinned) return;
        IsPinned = pinned;

        if (pinned)
        {
            WindowHelper.SetNoActivate(this, false);
            Activate();
        }
        else
        {
            WindowHelper.SetNoActivate(this, true);
            HideFlyout();
        }
    }

    public bool IsFlyoutVisible => _visible && !_closing;

    public RECT PhysicalRect => WindowHelper.GetPhysicalRect(this);

    // ---------------------------------------------------------------- placement

    private void Reposition()
    {
        double scale = WindowHelper.ScaleOf(this);
        int wPix = (int)Math.Round(ActualWidth * scale);
        int hPix = (int)Math.Round(ActualHeight * scale);
        if (wPix <= 0 || hPix <= 0) return;

        int mLeft = (int)Math.Round(CardMarginLeft * scale);
        int mTop = (int)Math.Round(CardMarginTop * scale);
        int mBottom = (int)Math.Round(CardMarginBottom * scale);
        int gap = (int)Math.Round(8 * scale);
        int edgePad = (int)Math.Round(10 * scale);

        var mi = WindowHelper.MonitorInfoFor(new POINT
        {
            X = _anchor.Left + _anchor.Width / 2,
            Y = _anchor.Top + _anchor.Height / 2
        });
        var work = mi.rcWork.IsEmpty ? mi.rcMonitor : mi.rcWork;

        int left, top;

        switch (_edge)
        {
            case TaskbarEdge.Top:
                top = _anchor.Bottom + gap - mTop;
                left = _anchor.Left + _anchor.Width / 2 - wPix / 2;
                break;

            case TaskbarEdge.Left:
                left = _anchor.Right + gap - mLeft;
                top = _anchor.Bottom - hPix + mBottom;
                break;

            case TaskbarEdge.Right:
                left = _anchor.Left - gap - wPix + mLeft;
                top = _anchor.Bottom - hPix + mBottom;
                break;

            default: // Bottom
                top = _anchor.Top - gap - hPix + mBottom;
                left = _anchor.Left + _anchor.Width / 2 - wPix / 2;
                break;
        }

        // Keep the visible card (window minus its shadow margin) inside the work area.
        int minLeft = work.Left + edgePad - mLeft;
        int maxLeft = work.Right - edgePad - wPix + mLeft;
        if (maxLeft < minLeft) maxLeft = minLeft;
        left = Math.Clamp(left, minLeft, maxLeft);

        int minTop = work.Top + edgePad - mTop;
        int maxTop = work.Bottom - edgePad - hPix + mBottom;
        if (maxTop < minTop) maxTop = minTop;
        top = Math.Clamp(top, minTop, maxTop);

        WindowHelper.PlacePhysical(this, new RECT
        {
            Left = left,
            Top = top,
            Right = left + wPix,
            Bottom = top + hPix
        });
    }
}
