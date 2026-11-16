using System.Text.Json.Serialization;
using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Monitoring;

/// <summary>What we know about one peripheral, including its last reading while it is asleep.</summary>
public sealed record DeviceSnapshot
{
    /// <summary>Stable id: receiver product id + slot ("C54D/1"), or "BT/&lt;address&gt;" for Bluetooth.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public string Connection { get; init; } = "";
    public BatteryInfo? Battery { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset? LastSeen { get; init; }

    // Set by the UI from the user's customization; never persisted with the readings.

    /// <summary>The device's own name, when the user renamed it.</summary>
    [JsonIgnore] public string? OriginalName { get; init; }

    /// <summary>Icon chosen by the user (a key of the app's icon catalog), or null for the default by kind.</summary>
    [JsonIgnore] public string? IconKey { get; init; }

    /// <summary>Short text (up to 4 characters) shown instead of an icon.</summary>
    [JsonIgnore] public string? Label { get; init; }

    /// <summary>Estimated time until empty, from the battery history.</summary>
    [JsonIgnore] public TimeSpan? TimeRemaining { get; init; }
}
