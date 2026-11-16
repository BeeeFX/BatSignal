using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;
using HidSharp;

namespace BatSignal.Core.Gamepads;

/// <summary>
/// PlayStation and Nintendo controllers, read from the battery field of their HID input reports
/// (USB, or Bluetooth when the controller already sends full reports).
/// The device is opened shared and only read: we never switch a controller's report mode,
/// because that would change what games using the basic mode receive.
/// </summary>
public sealed class HidGamepadProvider : IBatteryProvider
{
    enum Pad { DualSense, DualShock4, SwitchPro }

    static readonly (int Vid, int Pid, Pad Pad, string Name)[] Known =
    [
        (0x054C, 0x0CE6, Pad.DualSense, "DualSense"),
        (0x054C, 0x0DF2, Pad.DualSense, "DualSense Edge"),
        (0x054C, 0x05C4, Pad.DualShock4, "DualShock 4"),
        (0x054C, 0x09CC, Pad.DualShock4, "DualShock 4"),
        (0x054C, 0x0BA0, Pad.DualShock4, "DualShock 4"), // Sony wireless adapter
        (0x057E, 0x2009, Pad.SwitchPro, "Switch Pro Controller"),
    ];

    const string BluetoothHidService = "00001124-0000-1000-8000-00805f9b34fb";

    public event Action<string>? Log;

    public IReadOnlyList<DeviceSnapshot> Scan()
    {
        var result = new List<DeviceSnapshot>();
        foreach (var (vid, pid, pad, name) in Known)
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(vid, pid))
            {
                try
                {
                    var snap = Read(dev, pad, name);
                    if (snap is not null) result.Add(snap);
                }
                catch (Exception e)
                {
                    Log?.Invoke($"{name}: {e.Message}");
                }
            }
        }
        return result;
    }

    static DeviceSnapshot? Read(HidDevice dev, Pad pad, string name)
    {
        if (dev.GetMaxInputReportLength() < 10) return null; // not the gamepad collection

        bool bluetooth = dev.DevicePath.Contains(BluetoothHidService, StringComparison.OrdinalIgnoreCase);
        var config = new OpenConfiguration();
        config.SetOption(OpenOption.Exclusive, false);
        if (!dev.TryOpen(config, out var stream)) return null;

        BatteryInfo? battery = null;
        using (stream)
        {
            stream.ReadTimeout = 500;
            var buffer = new byte[dev.GetMaxInputReportLength()];
            // Controllers stream reports continuously; a few are enough to find one with battery data.
            for (int i = 0; i < 8 && battery is null; i++)
            {
                int n;
                try { n = stream.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { break; }
                var report = buffer.AsSpan(0, n);
                battery = pad switch
                {
                    Pad.DualSense => GamepadReports.DualSense(report),
                    Pad.DualShock4 => GamepadReports.DualShock4(report),
                    _ => GamepadReports.SwitchPro(report),
                };
            }
        }

        string serial = "";
        try { serial = dev.GetSerialNumber(); } catch { }
        string key = serial.Length > 0 ? serial : dev.DevicePath.GetHashCode().ToString("X8");
        return new DeviceSnapshot
        {
            Id = $"HID/{dev.VendorID:X4}:{dev.ProductID:X4}/{key}",
            Name = name,
            Kind = DeviceKind.Gamepad,
            Connection = bluetooth ? "Bluetooth" : "USB",
            Battery = battery, // null keeps the last reading (e.g. Bluetooth controller in basic mode)
            Online = true,
            LastSeen = DateTimeOffset.Now,
        };
    }
}
