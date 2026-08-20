namespace NetSpeed.Core;

public sealed class TrafficSnapshot
{
    public double Up { get; init; }
    public double Down { get; init; }
    public IReadOnlyList<ProcessRateRow> Processes { get; init; } = Array.Empty<ProcessRateRow>();
    public string AdapterName { get; init; } = "--";
    public EtwState EtwState { get; init; }
    public string? EtwError { get; init; }
    public double SessionSent { get; init; }
    public double SessionReceived { get; init; }

    public static readonly TrafficSnapshot Empty = new();
}
