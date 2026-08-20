using System.Globalization;
using System.Windows.Data;

namespace NetSpeed.UI;

/// <summary>
/// Turns (share, trackWidth) into the fill width of a row's mini bar, so the bar can live in a
/// star-sized column without hard-coding its track width.
/// </summary>
public sealed class ShareWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double share || values[1] is not double track)
            return 0d;

        if (double.IsNaN(track) || track <= 0) return 0d;
        return Math.Clamp(share, 0, 1) * track;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
