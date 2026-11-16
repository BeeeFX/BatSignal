using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>Icons the user can pick for a device (Segoe Fluent Icons glyphs).</summary>
internal static class IconCatalog
{
    public sealed record Entry(string Key, string Glyph, string Name, DeviceKind Shape);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("mouse", "", "Mouse", DeviceKind.Mouse),
        new("keyboard", "", "Keyboard", DeviceKind.Keyboard),
        new("gamepad", "", "Gamepad", DeviceKind.Gamepad),
        new("headphones", "", "Headphones", DeviceKind.Headset),
        new("headset", "", "Headset", DeviceKind.Headset),
        new("speaker", "", "Speaker", DeviceKind.Unknown),
        new("microphone", "", "Microphone", DeviceKind.Unknown),
        new("phone", "", "Phone", DeviceKind.Unknown),
        new("bluetooth", "", "Bluetooth", DeviceKind.Unknown),
        new("device", "", "Device", DeviceKind.Unknown),
        new("star", "", "Star", DeviceKind.Unknown),
        new("heart", "", "Heart", DeviceKind.Unknown),
        new("bolt", "", "Bolt", DeviceKind.Unknown),
    ];

    public static Entry? Find(string? key) => key is null ? null : All.FirstOrDefault(e => e.Key == key);

    /// <summary>Default icon for a kind of device.</summary>
    public static string DefaultKey(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse or DeviceKind.Trackball or DeviceKind.Touchpad => "mouse",
        DeviceKind.Keyboard or DeviceKind.Numpad => "keyboard",
        DeviceKind.Gamepad => "gamepad",
        DeviceKind.Headset => "headphones",
        _ => "device",
    };

    public static string Glyph(DeviceSnapshot d) => (Find(d.IconKey) ?? Find(DefaultKey(d.Kind))!).Glyph;

    /// <summary>Outline used by the tray icon for this device.</summary>
    public static DeviceKind Shape(DeviceSnapshot d) => Find(d.IconKey)?.Shape ?? d.Kind;
}

/// <summary>Applies the user's names, icons, labels and order to the monitor's raw readings.</summary>
internal static class Customization
{
    public static IReadOnlyList<DeviceSnapshot> Apply(IReadOnlyList<DeviceSnapshot> raw, Settings settings)
    {
        return raw
            .Select((d, i) => (Device: Customize(d, settings), Index: i))
            .OrderBy(x => settings.Order.IndexOf(x.Device.Id) is var o && o >= 0 ? o : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Device)
            .ToList();
    }

    static DeviceSnapshot Customize(DeviceSnapshot d, Settings settings)
    {
        if (!settings.Devices.TryGetValue(d.Id, out var c)) return d;
        return d with
        {
            Name = string.IsNullOrWhiteSpace(c.Name) ? d.Name : c.Name.Trim(),
            OriginalName = d.Name,
            IconKey = c.Icon,
            Label = string.IsNullOrWhiteSpace(c.Label) ? null : c.Label.Trim().ToUpperInvariant(),
        };
    }
}
