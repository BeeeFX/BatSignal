using System.Text.RegularExpressions;
using HidSharp;

namespace BatSignal.Core.Hidpp;

/// <summary>Finds Logitech HID++ endpoints (receivers and directly connected devices).</summary>
public static partial class HidppDiscovery
{
    public const int LogitechVendorId = 0x046D;
    const uint ShortUsage = 0xFF000001; // vendor page 0xFF00, usage 1 = short HID++ collection
    const uint LongUsage = 0xFF000002;  // vendor page 0xFF00, usage 2 = long HID++ collection

    public sealed record Endpoint(string Key, string Name, int ProductId, HidDevice? Short, HidDevice? Long);

    public static IReadOnlyList<Endpoint> Find()
    {
        var groups = new Dictionary<string, (HidDevice? s, HidDevice? l, int pid, string name)>();
        foreach (var dev in DeviceList.Local.GetHidDevices(LogitechVendorId))
        {
            var usage = TopUsage(dev);
            bool isShort = usage == ShortUsage || (usage is null && dev.GetMaxOutputReportLength() == ShortLength);
            bool isLong = usage == LongUsage || (usage is null && dev.GetMaxOutputReportLength() == LongLength);
            if (!isShort && !isLong) continue;

            var key = InterfaceKey(dev.DevicePath);
            groups.TryGetValue(key, out var g);
            if (isShort) g.s = dev; else g.l = dev;
            g.pid = dev.ProductID;
            g.name = SafeName(dev);
            groups[key] = g;
        }

        return groups
            .Where(kv => kv.Value.l is not null) // HID++ 2.0 needs the long collection
            .Select(kv => new Endpoint(kv.Key, kv.Value.name, kv.Value.pid, kv.Value.s, kv.Value.l))
            .OrderBy(e => e.ProductId)
            .ToList();
    }

    public static HidppChannel? Open(Endpoint e)
    {
        var config = new OpenConfiguration();
        config.SetOption(OpenOption.Exclusive, false);
        config.SetOption(OpenOption.Interruptible, true);

        HidStream? s = null, l = null;
        e.Short?.TryOpen(config, out s);
        e.Long?.TryOpen(config, out l);
        if (l is null)
        {
            s?.Dispose();
            return null;
        }
        return new HidppChannel(e.Key, e.Name, e.ProductId, s, l);
    }

    const int ShortLength = HidppChannel.ShortLength;
    const int LongLength = HidppChannel.LongLength;

    static uint? TopUsage(HidDevice dev)
    {
        try
        {
            var item = dev.GetReportDescriptor().DeviceItems.FirstOrDefault();
            return item?.Usages.GetAllValues().FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static string SafeName(HidDevice dev)
    {
        try { return dev.GetProductName(); }
        catch { return $"Logitech 0x{dev.ProductID:X4}"; }
    }

    /// <summary>
    /// Both collections of one interface share a path except for the "&amp;colNN" part and the last
    /// segment of the instance id, e.g. ...&amp;mi_02&amp;col01#9&amp;b8a496e&amp;0&amp;0000#{guid}.
    /// </summary>
    static string InterfaceKey(string path)
    {
        var p = path.ToLowerInvariant();
        p = ColRegex().Replace(p, "");
        p = InstanceSuffixRegex().Replace(p, "#{");
        return p;
    }

    [GeneratedRegex("&col[0-9a-f]{2}")]
    private static partial Regex ColRegex();

    [GeneratedRegex("&[0-9a-f]{4}#\\{")]
    private static partial Regex InstanceSuffixRegex();
}
