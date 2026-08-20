using System.Diagnostics;

namespace NetSpeed.Core;

/// <summary>Owns the sampling loop and publishes an immutable snapshot on every tick.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly Settings _settings;
    private readonly NetworkMeter _meter = new();
    private readonly TrafficAggregator _aggregator = new();
    private readonly EtwTrafficMonitor _etw = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private double _lastElapsed;
    private bool _busy;
    private bool _disposed;

    /// <summary>Raised on a background thread — subscribers must marshal to the UI themselves.</summary>
    public event Action<TrafficSnapshot>? Updated;

    public TrafficSnapshot Latest { get; private set; } = TrafficSnapshot.Empty;
    public EtwTrafficMonitor Etw => _etw;
    public NetworkMeter Meter => _meter;

    public MonitorService(Settings settings)
    {
        _settings = settings;
        _etw.StateChanged += () => { /* surfaced on the next tick */ };
    }

    public void Start()
    {
        _etw.Start();
        _lastElapsed = _clock.Elapsed.TotalSeconds;
        _timer = new System.Threading.Timer(_ => Tick(), null, _settings.RefreshMs, _settings.RefreshMs);
    }

    public void ApplyInterval()
    {
        _timer?.Change(_settings.RefreshMs, _settings.RefreshMs);
    }

    /// <summary>Forgets accumulated per-process rates, e.g. after the process list is no longer meaningful.</summary>
    public void ResetProcesses()
    {
        lock (_gate) _aggregator.Clear();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_busy || _disposed) return;
            _busy = true;
        }

        try
        {
            double now = _clock.Elapsed.TotalSeconds;
            double seconds = now - _lastElapsed;
            _lastElapsed = now;
            if (seconds <= 0) seconds = _settings.RefreshMs / 1000.0;

            var (up, down) = _meter.Sample(seconds, _settings);

            List<ProcessRateRow> rows;
            if (_etw.State == EtwState.Running)
            {
                _aggregator.Update(_etw.DrainDeltas(), seconds);
                rows = _aggregator.Top(_settings.TopCount);
            }
            else
            {
                rows = new List<ProcessRateRow>();
            }

            var snapshot = new TrafficSnapshot
            {
                Up = up,
                Down = down,
                Processes = rows,
                AdapterName = _meter.ActiveAdapterName,
                EtwState = _etw.State,
                EtwError = _etw.Error,
                SessionSent = _meter.TotalSent,
                SessionReceived = _meter.TotalReceived
            };

            Latest = snapshot;
            Updated?.Invoke(snapshot);
        }
        catch
        {
            // A single bad sample must never kill the loop.
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _etw.Dispose();
    }
}
