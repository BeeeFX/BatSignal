using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Gamepads;

/// <summary>
/// Battery fields in the standard input reports of popular gamepads (offsets include the report
/// id at index 0). Layouts follow the Linux hid-playstation / hid-sony / hid-nintendo drivers.
/// Each parser returns null when the report doesn't carry battery data — for example a
/// Bluetooth controller still in its basic report mode.
/// </summary>
public static class GamepadReports
{
    /// <summary>DualSense / DualSense Edge: USB report 0x01 (64 bytes) or Bluetooth report 0x31.</summary>
    public static BatteryInfo? DualSense(ReadOnlySpan<byte> r)
    {
        int at = r.Length >= 54 && r[0] == 0x01 ? 53
               : r.Length >= 55 && r[0] == 0x31 ? 54
               : -1;
        if (at < 0) return null;

        int level = r[at] & 0x0F, status = r[at] >> 4;
        int pct = Math.Min(level * 10 + 5, 100);
        return status switch
        {
            0x0 => Info(pct, ChargeState.Discharging),
            0x1 => Info(pct, ChargeState.Charging),
            0x2 => Info(100, ChargeState.Full),
            0xA or 0xB => Info(null, ChargeState.Error),
            _ => null,
        };
    }

    /// <summary>DualShock 4: USB report 0x01 (64 bytes) or Bluetooth report 0x11.</summary>
    public static BatteryInfo? DualShock4(ReadOnlySpan<byte> r)
    {
        int at = r.Length >= 31 && r[0] == 0x01 ? 30
               : r.Length >= 33 && r[0] == 0x11 ? 32
               : -1;
        if (at < 0) return null;

        bool cable = (r[at] & 0x10) != 0;
        int raw = r[at] & 0x0F;
        if (cable && raw > 10) return Info(100, ChargeState.Full);
        int level = Math.Min(cable ? raw : raw + 1, 10);
        return Info(level * 10, cable ? ChargeState.Charging : ChargeState.Discharging);
    }

    /// <summary>Switch Pro Controller: full report 0x30 (or subcommand replies 0x21), battery in byte 2's high nibble.</summary>
    public static BatteryInfo? SwitchPro(ReadOnlySpan<byte> r)
    {
        if (r.Length < 3 || r[0] is not (0x30 or 0x21 or 0x31)) return null;
        int nibble = r[2] >> 4;
        bool charging = (nibble & 1) != 0;
        var level = (nibble >> 1) switch
        {
            4 => BatteryLevel.Full,
            3 => BatteryLevel.Good,
            2 => BatteryLevel.Low,
            _ => BatteryLevel.Critical,
        };
        return new BatteryInfo(null, level, charging ? ChargeState.Charging : ChargeState.Discharging, null, false, false, "Switch Pro report");
    }

    static BatteryInfo Info(int? pct, ChargeState state) => new(
        pct,
        pct is null ? BatteryLevel.Unknown : pct >= 90 ? BatteryLevel.Full : pct >= 30 ? BatteryLevel.Good : pct >= 10 ? BatteryLevel.Low : BatteryLevel.Critical,
        state, null, false, IsApproximate: true, "Gamepad input report"); // these controllers report in 10% steps
}
