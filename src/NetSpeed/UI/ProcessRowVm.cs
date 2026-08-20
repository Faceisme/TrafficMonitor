using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NetSpeed.Core;

namespace NetSpeed.UI;

public sealed class ProcessRowVm : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _downNum = string.Empty;
    private string _downUnit = string.Empty;
    private string _upNum = string.Empty;
    private string _upUnit = string.Empty;
    private double _share;
    private ImageSource? _icon;
    private string _tooltip = string.Empty;

    private const double MinShare = 0.04;

    public string Key { get; }

    public ProcessRowVm(string key) => Key = key;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string DownNum { get => _downNum; set => Set(ref _downNum, value); }
    public string DownUnit { get => _downUnit; set => Set(ref _downUnit, value); }
    public string UpNum { get => _upNum; set => Set(ref _upNum, value); }
    public string UpUnit { get => _upUnit; set => Set(ref _upUnit, value); }
    public double Share { get => _share; set => Set(ref _share, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string Tooltip { get => _tooltip; set => Set(ref _tooltip, value); }

    public void Update(ProcessRateRow row, SpeedUnit unit, double maxTotal)
    {
        Name = row.Name;
        (DownNum, DownUnit) = Formatter.Speed(row.Down, unit);
        (UpNum, UpUnit) = Formatter.Speed(row.Up, unit);
        Icon ??= IconCache.Get(row.ImagePath);
        Tooltip = string.IsNullOrEmpty(row.ImagePath) ? row.Name : row.ImagePath;

        // A floor keeps a light consumer from rendering as a 1px stub; the exact rates are printed
        // on the same row, so the bar only has to convey rank at a glance.
        double share = maxTotal > 0 ? row.Total / maxTotal : 0;
        Share = MinShare + (1 - MinShare) * Math.Clamp(share, 0, 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }
}
