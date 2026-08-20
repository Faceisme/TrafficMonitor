namespace NetSpeed.Core;

public sealed record ProcessRateRow(
    string Key,
    string Name,
    string ExeName,
    string ImagePath,
    double Up,
    double Down)
{
    public double Total => Up + Down;
}

/// <summary>
/// Folds per-PID ETW deltas into per-executable rates and smooths them so the list does not
/// flicker between ticks.
/// </summary>
public sealed class TrafficAggregator
{
    private sealed class Bucket
    {
        public ProcessInfo Info = null!;
        public double Up;
        public double Down;
        public int IdleTicks;
    }

    private const double Alpha = 0.6;      // weight of the newest sample
    private const int MaxIdleTicks = 8;    // rows below the noise floor disappear after this many ticks
    private const double NoiseFloor = 8;   // bytes/second

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public void Update(Dictionary<int, (long Sent, long Received)> deltas, double seconds)
    {
        if (seconds <= 0.01) seconds = 0.01;

        var current = new Dictionary<string, (double up, double down, ProcessInfo info)>(StringComparer.Ordinal);

        foreach (var (pid, d) in deltas)
        {
            var info = ProcessInfoCache.Resolve(pid);
            double up = d.Sent / seconds;
            double down = d.Received / seconds;

            if (current.TryGetValue(info.Key, out var acc))
                current[info.Key] = (acc.up + up, acc.down + down, acc.info);
            else
                current[info.Key] = (up, down, info);
        }

        foreach (var (key, v) in current)
        {
            if (_buckets.TryGetValue(key, out var b))
            {
                b.Up = b.Up * (1 - Alpha) + v.up * Alpha;
                b.Down = b.Down * (1 - Alpha) + v.down * Alpha;
                b.Info = v.info;
                b.IdleTicks = 0;
            }
            else
            {
                _buckets[key] = new Bucket { Info = v.info, Up = v.up, Down = v.down };
            }
        }

        foreach (var (key, b) in _buckets.ToList())
        {
            if (current.ContainsKey(key)) continue;

            b.Up *= 1 - Alpha;
            b.Down *= 1 - Alpha;

            if (b.Up + b.Down < NoiseFloor && ++b.IdleTicks >= MaxIdleTicks)
                _buckets.Remove(key);
        }
    }

    public List<ProcessRateRow> Top(int count) =>
        _buckets.Values
            .Where(b => b.Up + b.Down >= 1)
            .OrderByDescending(b => b.Up + b.Down)
            .Take(count)
            .Select(b => new ProcessRateRow(b.Info.Key, b.Info.DisplayName, b.Info.ExeName, b.Info.ImagePath, b.Up, b.Down))
            .ToList();

    public void Clear() => _buckets.Clear();
}
