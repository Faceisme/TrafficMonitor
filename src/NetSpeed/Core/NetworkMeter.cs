using System.Net.NetworkInformation;

namespace NetSpeed.Core;

public sealed record AdapterChoice(string Id, string Name, string Description);

/// <summary>
/// Total up/down rate, read from the NIC counters. This is the authoritative number for the
/// widget: ETW per-process sums always fall a little short of what the adapter actually moved.
/// </summary>
public sealed class NetworkMeter
{
    private sealed class Entry
    {
        public NetworkInterface Nic = null!;
        public long LastRx;
        public long LastTx;
        public bool Primed;
        public double RecentActivity;
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private DateTime _lastEnumerate = DateTime.MinValue;
    private string? _autoId;
    private string? _autoCandidate;
    private int _autoCandidateHits;

    public string ActiveAdapterName { get; private set; } = "--";
    public double TotalReceived { get; private set; }
    public double TotalSent { get; private set; }

    public IReadOnlyList<AdapterChoice> ListAdapters()
    {
        var list = new List<AdapterChoice>();
        foreach (var nic in SafeGetAll())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            list.Add(new AdapterChoice(nic.Id, nic.Name, nic.Description));
        }
        return list;
    }

    private static NetworkInterface[] SafeGetAll()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Array.Empty<NetworkInterface>(); }
    }

    private static bool IsUsable(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up &&
        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel;

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                if (gw.Address is { } a && !a.Equals(System.Net.IPAddress.Any) && !a.Equals(System.Net.IPAddress.IPv6Any))
                    return true;
        }
        catch { }
        return false;
    }

    private void Enumerate()
    {
        if ((DateTime.UtcNow - _lastEnumerate).TotalSeconds < 5 && _entries.Count > 0) return;
        _lastEnumerate = DateTime.UtcNow;

        var seen = new HashSet<string>();
        foreach (var nic in SafeGetAll())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            seen.Add(nic.Id);
            if (_entries.TryGetValue(nic.Id, out var e)) e.Nic = nic;
            else _entries[nic.Id] = new Entry { Nic = nic };
        }
        foreach (var stale in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
            _entries.Remove(stale);
    }

    /// <summary>Samples every adapter and returns the (up, down) rate in bytes/second.</summary>
    public (double Up, double Down) Sample(double seconds, Settings settings)
    {
        Enumerate();
        if (seconds <= 0.01) seconds = 0.01;

        var deltas = new Dictionary<string, (double rx, double tx)>();

        foreach (var e in _entries.Values)
        {
            long rx = 0, tx = 0;
            try
            {
                if (!IsUsable(e.Nic)) { e.Primed = false; continue; }
                var st = e.Nic.GetIPStatistics();
                rx = st.BytesReceived;
                tx = st.BytesSent;
            }
            catch { e.Primed = false; continue; }

            if (!e.Primed)
            {
                e.LastRx = rx; e.LastTx = tx; e.Primed = true;
                continue;
            }

            // Counters can wrap or reset when an adapter reconnects.
            double drx = rx >= e.LastRx ? rx - e.LastRx : 0;
            double dtx = tx >= e.LastTx ? tx - e.LastTx : 0;
            e.LastRx = rx; e.LastTx = tx;

            deltas[e.Nic.Id] = (drx, dtx);
            e.RecentActivity = e.RecentActivity * 0.6 + (drx + dtx) * 0.4;
        }

        double up = 0, down = 0;
        string name = "--";

        switch (settings.AdapterMode)
        {
            case AdapterMode.Specific when settings.AdapterId is { } id && _entries.TryGetValue(id, out var one):
                if (deltas.TryGetValue(id, out var d1)) { down = d1.rx; up = d1.tx; }
                name = one.Nic.Name;
                break;

            case AdapterMode.All:
                foreach (var kv in deltas)
                {
                    if (!_entries.TryGetValue(kv.Key, out var e) || !HasGateway(e.Nic)) continue;
                    down += kv.Value.rx; up += kv.Value.tx;
                }
                name = "全部网卡";
                break;

            default:
                var pick = PickAuto();
                if (pick != null)
                {
                    if (deltas.TryGetValue(pick.Nic.Id, out var d2)) { down = d2.rx; up = d2.tx; }
                    name = pick.Nic.Name;
                }
                break;
        }

        TotalReceived += down;
        TotalSent += up;
        ActiveAdapterName = name;

        return (up / seconds, down / seconds);
    }

    /// <summary>
    /// Picks the busiest gateway-bearing adapter, but only switches after three consecutive
    /// samples so a brief burst on a VPN or virtual NIC does not make the readout jump around.
    /// </summary>
    private Entry? PickAuto()
    {
        var candidates = _entries.Values.Where(e => IsUsable(e.Nic) && HasGateway(e.Nic)).ToList();
        if (candidates.Count == 0)
            candidates = _entries.Values.Where(e => IsUsable(e.Nic)).ToList();
        if (candidates.Count == 0) { _autoId = null; return null; }

        var best = candidates.OrderByDescending(e => e.RecentActivity).First();

        if (_autoId == null || !_entries.TryGetValue(_autoId, out var current) || !IsUsable(current.Nic))
        {
            _autoId = best.Nic.Id;
            _autoCandidate = null;
            _autoCandidateHits = 0;
            return best;
        }

        if (best.Nic.Id == _autoId)
        {
            _autoCandidate = null;
            _autoCandidateHits = 0;
            return current;
        }

        if (_autoCandidate == best.Nic.Id) _autoCandidateHits++;
        else { _autoCandidate = best.Nic.Id; _autoCandidateHits = 1; }

        if (_autoCandidateHits >= 3 && best.RecentActivity > current.RecentActivity * 1.5)
        {
            _autoId = best.Nic.Id;
            _autoCandidate = null;
            _autoCandidateHits = 0;
            return best;
        }

        return current;
    }
}
