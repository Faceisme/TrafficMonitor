using System.Globalization;

namespace NetSpeed.Core;

public static class Formatter
{
    private static readonly string[] ByteUnits = { "B/s", "KB/s", "MB/s", "GB/s" };
    private static readonly string[] BitUnits = { "bps", "Kbps", "Mbps", "Gbps" };

    /// <summary>Splits a rate into a stable-width number and its unit, e.g. ("1.45", "KB/s").</summary>
    public static (string Value, string Unit) Speed(double bytesPerSecond, SpeedUnit unit)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0) bytesPerSecond = 0;

        double v;
        string[] units;
        double step;

        if (unit == SpeedUnit.Bits)
        {
            v = bytesPerSecond * 8;
            units = BitUnits;
            step = 1000;
        }
        else
        {
            v = bytesPerSecond;
            units = ByteUnits;
            step = 1024;
        }

        int i = 0;
        while (v >= step && i < units.Length - 1)
        {
            v /= step;
            i++;
        }

        string text = i == 0
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v < 10 ? v.ToString("0.00", CultureInfo.InvariantCulture)
            : v < 100 ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);

        return (text, units[i]);
    }

    public static string SpeedText(double bytesPerSecond, SpeedUnit unit)
    {
        var (v, u) = Speed(bytesPerSecond, unit);
        return v + " " + u;
    }

    /// <summary>Cumulative volume, always in byte units (bits make no sense for a total).</summary>
    public static string Volume(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        string text = i == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v < 10 ? v.ToString("0.00", CultureInfo.InvariantCulture)
            : v < 100 ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);
        return text + " " + units[i];
    }
}
