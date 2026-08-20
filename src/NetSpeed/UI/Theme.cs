using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetSpeed.UI;

/// <summary>
/// Publishes theme brushes as application resources so XAML can DynamicResource them.
/// The widget follows the taskbar theme; the flyout follows the app theme, matching what
/// Windows itself does.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsLight { get; private set; }
    public static bool TaskbarLight { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply(force: true);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(force: false));
        };
    }

    public static void Poll() => Apply(force: false);

    private static bool ReadFlag(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(name) is int v) return v != 0;
        }
        catch { }
        return fallback;
    }

    private static void Apply(bool force)
    {
        bool apps = ReadFlag("AppsUseLightTheme", true);
        bool taskbar = ReadFlag("SystemUsesLightTheme", false);

        if (!force && apps == AppsLight && taskbar == TaskbarLight) return;

        AppsLight = apps;
        TaskbarLight = taskbar;

        var res = Application.Current?.Resources;
        if (res == null) return;

        // ---- flyout surface ----
        if (apps)
        {
            Set(res, "CardBrush", Color.FromArgb(0xFA, 0xFB, 0xFB, 0xFD));
            Set(res, "CardBorderBrush", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set(res, "TextPrimaryBrush", Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1E));
            Set(res, "TextSecondaryBrush", Color.FromArgb(0xA8, 0x1A, 0x1A, 0x1E));
            Set(res, "TextTertiaryBrush", Color.FromArgb(0x70, 0x1A, 0x1A, 0x1E));
            Set(res, "DividerBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            Set(res, "HoverBrush", Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
            Set(res, "DownBrush", Color.FromArgb(0xFF, 0x0B, 0x6C, 0xC4));
            Set(res, "UpBrush", Color.FromArgb(0xFF, 0xC2, 0x6A, 0x11));
            Set(res, "BarFillBrush", Color.FromArgb(0xCC, 0x0B, 0x6C, 0xC4));
            Set(res, "RowTrackBrush", Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            Set(res, "ControlBrush", Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
            Set(res, "ControlBorderBrush", Color.FromArgb(0x28, 0x00, 0x00, 0x00));
            Set(res, "ShadowColorRes", Color.FromArgb(0xFF, 0x00, 0x00, 0x00), asColor: true);
        }
        else
        {
            Set(res, "CardBrush", Color.FromArgb(0xFA, 0x25, 0x26, 0x2B));
            Set(res, "CardBorderBrush", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            Set(res, "TextPrimaryBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            Set(res, "TextSecondaryBrush", Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF));
            Set(res, "TextTertiaryBrush", Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            Set(res, "DividerBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            Set(res, "HoverBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set(res, "DownBrush", Color.FromArgb(0xFF, 0x5C, 0xC8, 0xFF));
            Set(res, "UpBrush", Color.FromArgb(0xFF, 0xFF, 0xB4, 0x54));
            Set(res, "BarFillBrush", Color.FromArgb(0xD8, 0x5C, 0xC8, 0xFF));
            Set(res, "RowTrackBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Set(res, "ControlBrush", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            Set(res, "ControlBorderBrush", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            Set(res, "ShadowColorRes", Color.FromArgb(0xFF, 0x00, 0x00, 0x00), asColor: true);
        }

        // ---- widget text sits directly on the taskbar ----
        if (taskbar)
        {
            Set(res, "WidgetTextBrush", Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1E));
            Set(res, "WidgetUpBrush", Color.FromArgb(0xFF, 0xB3, 0x5E, 0x0C));
            Set(res, "WidgetDownBrush", Color.FromArgb(0xFF, 0x0A, 0x63, 0xB8));
            Set(res, "WidgetHoverBrush", Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        }
        else
        {
            Set(res, "WidgetTextBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            Set(res, "WidgetUpBrush", Color.FromArgb(0xFF, 0xFF, 0xB4, 0x54));
            Set(res, "WidgetDownBrush", Color.FromArgb(0xFF, 0x5C, 0xC8, 0xFF));
            Set(res, "WidgetHoverBrush", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }

        Changed?.Invoke();
    }

    private static void Set(ResourceDictionary res, string key, Color c, bool asColor = false)
    {
        if (asColor) { res[key] = c; return; }

        if (res[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = c;
            return;
        }

        var brush = new SolidColorBrush(c);
        res[key] = brush;
    }
}
