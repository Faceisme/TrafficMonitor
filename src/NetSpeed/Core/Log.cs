using System.IO;

namespace NetSpeed.Core;

/// <summary>Minimal crash log — the only diagnostics a tray tool with no console can offer.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Settings.Directory, "error.log");

    public static void Write(string context, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Directory);
                var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(FilePath, text);
            }
        }
        catch { }
    }

    public static void Info(string message) => Write(message, null);
}
