using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetSpeed.Core;

namespace NetSpeed.UI;

public partial class SettingsWindow : Window
{
    private sealed record Item(string Text, object Value)
    {
        public override string ToString() => Text;
    }

    private readonly Settings _settings;
    private readonly MonitorService _monitor;
    private bool _loading = true;

    public SettingsWindow(Settings settings, MonitorService monitor)
    {
        _settings = settings;
        _monitor = monitor;
        InitializeComponent();

        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CloseButton.Click += (_, _) => Close();
        DoneButton.Click += (_, _) => Close();

        // Small screens must still be able to reach the bottom row.
        double maxWindow = SystemParameters.WorkArea.Height - 48;
        MaxHeight = Math.Max(360, maxWindow);
        Body.MaxHeight = Math.Max(240, maxWindow - 90);

        Populate();
        Wire();
        _loading = false;
        UpdateRowVisibility();
    }

    // ---------------------------------------------------------------- setup

    private void Populate()
    {
        ModeCombo.ItemsSource = new[]
        {
            new Item("任务栏", DisplayMode.Taskbar),
            new Item("桌面悬浮窗", DisplayMode.Floating),
        };
        ModeCombo.SelectedIndex = _settings.DisplayMode == DisplayMode.Floating ? 1 : 0;

        IntervalCombo.ItemsSource = new[]
        {
            new Item("0.5 秒", 500),
            new Item("1 秒", 1000),
            new Item("2 秒", 2000),
            new Item("3 秒", 3000),
        };
        IntervalCombo.SelectedItem = ((Item[])IntervalCombo.ItemsSource)
            .FirstOrDefault(i => (int)i.Value == _settings.RefreshMs) ?? ((Item[])IntervalCombo.ItemsSource)[1];

        UnitCombo.ItemsSource = new[]
        {
            new Item("字节 (KB/s)", SpeedUnit.Bytes),
            new Item("比特 (Kbps)", SpeedUnit.Bits),
        };
        UnitCombo.SelectedIndex = _settings.Unit == SpeedUnit.Bits ? 1 : 0;

        TopCombo.ItemsSource = new[] { 3, 5, 8, 10 }.Select(n => new Item($"{n} 个", n)).ToArray();
        TopCombo.SelectedItem = ((Item[])TopCombo.ItemsSource)
            .FirstOrDefault(i => (int)i.Value == _settings.TopCount) ?? ((Item[])TopCombo.ItemsSource)[1];

        RefreshAdapters();

        TrayToggle.IsChecked = _settings.ShowTrayIcon;
        FullscreenToggle.IsChecked = _settings.HideOnFullscreen;
        AutoStartToggle.IsChecked = AutoStart.IsEnabled;
        AutoStartHint.Text = EtwTrafficMonitor.IsElevated
            ? "以计划任务方式启动，开机不会弹出 UAC"
            : "当前非管理员，将使用普通启动项";

        PrivilegeLabel.Text = EtwTrafficMonitor.IsElevated
            ? "已获得管理员权限，进程流量可用"
            : "未以管理员运行，仅显示总网速";

        RefreshSteppers();
    }

    private void RefreshAdapters()
    {
        var items = new List<Item>
        {
            new("自动选择", "auto"),
            new("全部相加", "all"),
        };
        foreach (var a in _monitor.Meter.ListAdapters())
            items.Add(new Item(a.Name, a.Id));

        AdapterCombo.ItemsSource = items;
        AdapterCombo.SelectedItem = _settings.AdapterMode switch
        {
            AdapterMode.All => items[1],
            AdapterMode.Specific => items.FirstOrDefault(i => (string)i.Value == _settings.AdapterId) ?? items[0],
            _ => items[0],
        };
    }

    private void Wire()
    {
        ModeCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.DisplayMode = (DisplayMode)Sel(ModeCombo);
            UpdateRowVisibility();
            Commit();
        };

        IntervalCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.RefreshMs = (int)Sel(IntervalCombo);
            _monitor.ApplyInterval();
            Commit();
        };

        UnitCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.Unit = (SpeedUnit)Sel(UnitCombo);
            Commit();
        };

        TopCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.TopCount = (int)Sel(TopCombo);
            Commit();
        };

        AdapterCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var v = (string)Sel(AdapterCombo);
            switch (v)
            {
                case "auto": _settings.AdapterMode = AdapterMode.Auto; _settings.AdapterId = null; break;
                case "all": _settings.AdapterMode = AdapterMode.All; _settings.AdapterId = null; break;
                default: _settings.AdapterMode = AdapterMode.Specific; _settings.AdapterId = v; break;
            }
            Commit();
        };

        Step(WidthMinus, WidthPlus, -4, 4, () => _settings.WidgetWidth, v => _settings.WidgetWidth = Math.Clamp(v, 60, 220));
        Step(GapMinus, GapPlus, -4, 4, () => _settings.WidgetGap, v => _settings.WidgetGap = Math.Clamp(v, 0, 600));
        Step(OffsetMinus, OffsetPlus, -1, 1, () => _settings.WidgetOffsetY, v => _settings.WidgetOffsetY = Math.Clamp(v, -40, 40));

        FontMinus.Click += (_, _) => { _settings.FontSize = Math.Clamp(_settings.FontSize - 0.5, 8, 18); RefreshSteppers(); Commit(); };
        FontPlus.Click += (_, _) => { _settings.FontSize = Math.Clamp(_settings.FontSize + 0.5, 8, 18); RefreshSteppers(); Commit(); };

        TrayToggle.Click += (_, _) => { _settings.ShowTrayIcon = TrayToggle.IsChecked == true; Commit(); };
        FullscreenToggle.Click += (_, _) => { _settings.HideOnFullscreen = FullscreenToggle.IsChecked == true; Commit(); };

        AutoStartToggle.Click += (_, _) =>
        {
            bool want = AutoStartToggle.IsChecked == true;
            if (!AutoStart.Set(want))
            {
                MessageBox.Show(this, "设置开机自启失败，请检查系统权限。", "NetSpeed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            AutoStartToggle.IsChecked = AutoStart.IsEnabled;
        };
    }

    private void Step(Button minus, Button plus, int dMinus, int dPlus, Func<int> get, Action<int> set)
    {
        minus.Click += (_, _) => { set(get() + dMinus); RefreshSteppers(); Commit(); };
        plus.Click += (_, _) => { set(get() + dPlus); RefreshSteppers(); Commit(); };
    }

    private static object Sel(ComboBox box) => ((Item)box.SelectedItem).Value;

    private void RefreshSteppers()
    {
        WidthValue.Text = _settings.WidgetWidth.ToString();
        GapValue.Text = _settings.WidgetGap.ToString();
        OffsetValue.Text = _settings.WidgetOffsetY.ToString();
        FontValue.Text = _settings.FontSize.ToString("0.#");
    }

    private void UpdateRowVisibility()
    {
        bool taskbar = _settings.DisplayMode == DisplayMode.Taskbar;
        GapRow.Visibility = taskbar ? Visibility.Visible : Visibility.Collapsed;
        OffsetRow.Visibility = taskbar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Commit()
    {
        _settings.Save();
        _settings.RaiseChanged();
    }
}
