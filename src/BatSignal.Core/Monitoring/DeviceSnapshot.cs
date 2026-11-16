using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Monitoring;

/// <summary>What we know about one peripheral, including its last reading while it is asleep.</summary>
public sealed record DeviceSnapshot
{
    /// <summary>Stable id: receiver product id + slot (or "direct").</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public string Connection { get; init; } = "";
    public BatteryInfo? Battery { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
}
