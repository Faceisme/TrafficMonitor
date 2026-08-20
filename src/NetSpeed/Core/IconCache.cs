using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetSpeed.Interop;

namespace NetSpeed.Core;

/// <summary>Executable path to a frozen 32px icon. Missing/denied paths fall back to a generic glyph.</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        return Cache.GetOrAdd(imagePath, Extract);
    }

    private static ImageSource? Extract(string path)
    {
        var shinfo = new SHFILEINFO();
        IntPtr result = Native.SHGetFileInfo(
            path, Native.FILE_ATTRIBUTE_NORMAL, ref shinfo,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
            Native.SHGFI_ICON | Native.SHGFI_LARGEICON);

        if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { Native.DestroyIcon(shinfo.hIcon); }
    }
}
