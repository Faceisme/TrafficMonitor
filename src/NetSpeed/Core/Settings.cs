using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetSpeed.Core;

public enum SpeedUnit { Bytes, Bits }
public enum AdapterMode { Auto, All, Specific }
public enum DisplayMode { Taskbar, Floating }

public sealed class Settings
{
    /// <summary>Bumped when a default changes in a way that should override an older saved file.</summary>
    private const int CurrentSchema = 2;

    public int SchemaVersion { get; set; }

    public int RefreshMs { get; set; } = 1000;
    public SpeedUnit Unit { get; set; } = SpeedUnit.Bytes;

    public AdapterMode AdapterMode { get; set; } = AdapterMode.Auto;
    public string? AdapterId { get; set; }

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Taskbar;
    public int WidgetWidth { get; set; } = 100;
    /// <summary>Gap between the widget and the tray area, in DIP.</summary>
    public int WidgetGap { get; set; } = 8;
    public int WidgetOffsetY { get; set; }
    public double FontSize { get; set; } = 13;

    public double FloatingX { get; set; } = double.NaN;
    public double FloatingY { get; set; } = double.NaN;

    public int TopCount { get; set; } = 5;
    public bool ShowTrayIcon { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpeed");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null) { s.Migrate(); s.Normalize(); return s; }
            }
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            SchemaVersion = CurrentSchema;
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* settings are best-effort */ }
    }

    /// <summary>Re-applies display defaults that changed since the file was written.</summary>
    private void Migrate()
    {
        if (SchemaVersion >= CurrentSchema) return;

        if (SchemaVersion < 2)
        {
            // v2 raised the type scale; carrying the old sizes forward would look cramped.
            var fresh = new Settings();
            FontSize = fresh.FontSize;
            WidgetWidth = fresh.WidgetWidth;
        }

        SchemaVersion = CurrentSchema;
    }

    private void Normalize()
    {
        RefreshMs = Math.Clamp(RefreshMs, 300, 5000);
        WidgetWidth = Math.Clamp(WidgetWidth, 60, 220);
        WidgetGap = Math.Clamp(WidgetGap, 0, 600);
        WidgetOffsetY = Math.Clamp(WidgetOffsetY, -40, 40);
        FontSize = Math.Clamp(FontSize, 8, 18);
        TopCount = Math.Clamp(TopCount, 3, 10);
    }
}
