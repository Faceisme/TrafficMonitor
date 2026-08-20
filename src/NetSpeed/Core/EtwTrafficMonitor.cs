using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetSpeed.Core;

public enum EtwState { Stopped, Starting, Running, NeedsAdmin, Failed }

/// <summary>
/// Per-process byte counters fed by the kernel TCP/IP ETW provider — the same source Task Manager
/// and Resource Monitor use. Windows exposes no per-process network performance counter, so this
/// is the only way to attribute traffic to a PID. Requires an elevated process.
/// </summary>
public sealed class EtwTrafficMonitor : IDisposable
{
    private const string SessionName = "NetSpeed-KernelNetwork";

    private readonly ConcurrentDictionary<int, long[]> _counters = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _disposed;

    public EtwState State { get; private set; } = EtwState.Stopped;
    public string? Error { get; private set; }

    public event Action? StateChanged;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public void Start()
    {
        if (_thread != null || _disposed) return;

        if (!IsElevated)
        {
            SetState(EtwState.NeedsAdmin, "需要管理员权限才能统计进程流量");
            return;
        }

        SetState(EtwState.Starting, null);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NetSpeed.Etw",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            DropStaleSession();

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 16
            };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var k = _session.Source.Kernel;
            k.TcpIpSend += e => Add(e.ProcessID, e.size, 0);
            k.TcpIpRecv += e => Add(e.ProcessID, 0, e.size);
            k.TcpIpSendIPV6 += e => Add(e.ProcessID, e.size, 0);
            k.TcpIpRecvIPV6 += e => Add(e.ProcessID, 0, e.size);
            k.UdpIpSend += e => Add(e.ProcessID, e.size, 0);
            k.UdpIpRecv += e => Add(e.ProcessID, 0, e.size);
            k.UdpIpSendIPV6 += e => Add(e.ProcessID, e.size, 0);
            k.UdpIpRecvIPV6 += e => Add(e.ProcessID, 0, e.size);

            SetState(EtwState.Running, null);
            _session.Source.Process(); // blocks until the session is stopped
        }
        catch (Exception ex)
        {
            if (!_disposed) SetState(EtwState.Failed, ex.Message);
            return;
        }

        if (!_disposed) SetState(EtwState.Stopped, null);
    }

    /// <summary>A crash can leave the kernel session registered; reclaim the name before creating ours.</summary>
    private static void DropStaleSession()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName, StringComparer.OrdinalIgnoreCase))
                new TraceEventSession(SessionName, TraceEventSessionOptions.Attach).Stop();
        }
        catch { /* nothing to reclaim */ }
    }

    private void Add(int pid, int sent, int received)
    {
        if (pid <= 0) return;
        var slot = _counters.GetOrAdd(pid, static _ => new long[2]);
        if (sent > 0) Interlocked.Add(ref slot[0], sent);
        if (received > 0) Interlocked.Add(ref slot[1], received);
    }

    /// <summary>Reads and zeroes the accumulated bytes since the previous call.</summary>
    public Dictionary<int, (long Sent, long Received)> DrainDeltas()
    {
        var result = new Dictionary<int, (long, long)>();
        var idle = new List<int>();

        foreach (var kv in _counters)
        {
            long sent = Interlocked.Exchange(ref kv.Value[0], 0);
            long recv = Interlocked.Exchange(ref kv.Value[1], 0);
            if (sent > 0 || recv > 0) result[kv.Key] = (sent, recv);
            else idle.Add(kv.Key);
        }

        // Keep the dictionary from growing without bound as PIDs churn.
        if (_counters.Count > 2048)
            foreach (var pid in idle)
                _counters.TryRemove(pid, out _);

        return result;
    }

    private void SetState(EtwState state, string? error)
    {
        State = state;
        Error = error;
        // Whether the kernel session came up is the single most useful thing to know when the
        // process list is empty on someone else's machine.
        Log.Info(error == null ? $"etw: {state}" : $"etw: {state} - {error}");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session?.Source?.StopProcessing(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
