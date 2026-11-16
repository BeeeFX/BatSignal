// Read-only diagnostic: lists Logitech HID++ endpoints, the devices behind them and their battery.
// Usage: BatSignal.Probe [--raw]   (--raw prints every HID++ report sent and received)

using BatSignal.Core.Hidpp;

bool raw = args.Contains("--raw");

if (args.Contains("--monitor"))
{
    // Runs the app's DeviceMonitor for 40 s, forcing a refresh every 8 s, and prints what it sees.
    using var monitor = new BatSignal.Core.Monitoring.DeviceMonitor();
    monitor.Log += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} log: {m}");
    monitor.Updated += list => Console.WriteLine($"{DateTime.Now:HH:mm:ss} " + string.Join(" | ",
        list.Select(d => $"{d.Name} {(d.Online ? "online" : "ASLEEP")} {d.Battery?.Percent}%")));
    monitor.Start();
    for (int i = 0; i < 5; i++) { Thread.Sleep(8000); monitor.RefreshNow(); }
    return;
}

if (args.Contains("--bluetooth"))
{
    var bt = new BatSignal.Core.Bluetooth.BluetoothBatteryProvider();
    bt.Log += m => Console.WriteLine($"log: {m}");
    foreach (var d in bt.Scan())
        Console.WriteLine($"{d.Name} ({d.Kind}) {(d.Online ? "connected" : "disconnected")} " +
                          $"battery={d.Battery?.Percent?.ToString() ?? "?"}% [{d.Battery?.Source ?? "none"}] id={d.Id}");
    return;
}

if (args.Contains("--features"))
{
    // Lists every HID++ 2.0 feature of every online device (feature set 0x0001), and polls
    // battery-related ones so we can see what changes while charging.
    foreach (var e in HidppDiscovery.Find())
    {
        using var c = HidppDiscovery.Open(e);
        if (c is null) continue;
        for (byte idx = 1; idx <= 6; idx++)
        {
            var d = new HidppDevice(c, idx);
            if (d.Ping() is not { Major: >= 2 }) continue;
            Console.WriteLine($"== {d.GetName()} (0x{e.ProductId:X4} slot {idx})");
            int count = d.Call(0x0001, 0)[0];
            for (int i = 1; i <= count; i++)
            {
                var f = d.Call(0x0001, 1, (byte)i);
                ushort id = (ushort)((f[0] << 8) | f[1]);
                string extra = "";
                if (id is 0x1000 or 0x1001 or 0x1004 or 0x1F20 or 0x1010)
                    try { extra = " fn0 -> " + Convert.ToHexString(d.Call(id, 0)[..8]) + (id == 0x1004 ? " fn1 -> " + Convert.ToHexString(d.Call(id, 1)[..8]) : ""); } catch (Exception ex) { extra = " " + ex.Message; }
                Console.WriteLine($"   [{i,2}] 0x{id:X4} v{f[3]}{extra}");
            }
        }
    }
    return;
}

var endpoints = HidppDiscovery.Find();
Console.WriteLine($"Found {endpoints.Count} HID++ endpoint(s).");

foreach (var ep in endpoints)
{
    Console.WriteLine();
    Console.WriteLine($"== {ep.Name} (PID 0x{ep.ProductId:X4}) short={(ep.Short is null ? "no" : "yes")} long={(ep.Long is null ? "no" : "yes")}");

    using var ch = HidppDiscovery.Open(ep);
    if (ch is null)
    {
        Console.WriteLine("   could not open");
        continue;
    }
    if (raw) ch.Trace += (dir, r) => Console.WriteLine($"   {dir} {Convert.ToHexString(r)}");

    var receiver = new HidppReceiver(ch);
    foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
    {
        var pairing = index == 0xFF ? null : receiver.GetPairing(index);
        var dev = new HidppDevice(ch, index);
        var version = dev.Ping();

        if (version is null && pairing is null) continue;
        string slot = index == 0xFF ? "direct" : $"slot {index}";

        if (version is null)
        {
            Console.WriteLine($"   [{slot}] {pairing!.Name ?? "?"} ({pairing.Kind}, wpid {pairing.WirelessPid:X4}) - offline/asleep");
            continue;
        }
        if (version.Major < 2)
        {
            if (index != 0xFF) Console.WriteLine($"   [{slot}] HID++ {version} device (no battery features)");
            continue; // 0xFF answering 1.0 = the receiver itself
        }

        try
        {
            var name = dev.GetName() ?? pairing?.Name ?? "?";
            var kind = dev.GetKind();
            var features = new[] { HidppDevice.FeatureUnifiedBattery, HidppDevice.FeatureBatteryStatus, HidppDevice.FeatureBatteryVoltage }
                .Where(dev.Has).Select(f => $"0x{f:X4}");
            Console.WriteLine($"   [{slot}] {name} ({kind}) HID++ {version}, battery features: {string.Join(", ", features).DefaultIfEmpty("none")}");

            var b = dev.ReadBattery();
            if (b is null) Console.WriteLine("       no battery reading");
            else Console.WriteLine($"       battery: {(b.Percent is int p ? $"{p}%" : "n/a")}{(b.IsEstimate ? " (estimated)" : "")}, level {b.Level}, {b.State}{(b.VoltageMv is int mv ? $", {mv} mV" : "")}  [{b.Source}]");
        }
        catch (Exception e)
        {
            Console.WriteLine($"   [{slot}] error: {e.Message}");
        }
    }
}

static class Extensions
{
    public static string DefaultIfEmpty(this string s, string fallback) => s.Length == 0 ? fallback : s;
}
