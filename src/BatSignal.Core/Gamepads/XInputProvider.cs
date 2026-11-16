using System.Runtime.InteropServices;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.Core.Gamepads;

/// <summary>
/// Xbox controllers through XInput: the Xbox Wireless Adapter, and controllers with a battery
/// plugged in by cable. XInput only reports coarse levels (empty, low, medium, full).
/// Bluetooth Xbox controllers report battery over Bluetooth instead (see BluetoothBatteryProvider).
/// </summary>
public sealed class XInputProvider : IBatteryProvider
{
    const byte BatteryDevTypeGamepad = 0;
    const uint ErrorSuccess = 0;

    public event Action<string>? Log;

    public IReadOnlyList<DeviceSnapshot> Scan()
    {
        var result = new List<DeviceSnapshot>();
        for (uint user = 0; user < 4; user++)
        {
            try
            {
                if (XInputGetState(user, out _) != ErrorSuccess) continue; // no controller in this slot
                if (XInputGetBatteryInformation(user, BatteryDevTypeGamepad, out var info) != ErrorSuccess) continue;
                var battery = FromXInput(info.BatteryType, info.BatteryLevel);
                if (battery is null) continue; // wired-only controller, or battery unknown

                result.Add(new DeviceSnapshot
                {
                    Id = $"XINPUT/{user}",
                    Name = user == 0 ? "Xbox controller" : $"Xbox controller {user + 1}",
                    Kind = DeviceKind.Gamepad,
                    Connection = "Xbox",
                    Battery = battery,
                    Online = true,
                    LastSeen = DateTimeOffset.Now,
                });
            }
            catch (DllNotFoundException)
            {
                Log?.Invoke("XInput is not available on this system");
                break;
            }
        }
        return result;
    }

    /// <summary>Maps XInput's battery type and level; null when there's no battery to report.</summary>
    public static BatteryInfo? FromXInput(byte type, byte level)
    {
        // BATTERY_TYPE: 0 disconnected, 1 wired (no battery), 2 alkaline, 3 NiMH, 0xFF unknown
        if (type is not (2 or 3)) return null;
        var lvl = level switch
        {
            0 => BatteryLevel.Critical, // BATTERY_LEVEL_EMPTY
            1 => BatteryLevel.Low,
            2 => BatteryLevel.Good,     // MEDIUM
            _ => BatteryLevel.Full,
        };
        return new BatteryInfo(null, lvl, ChargeState.Unknown, null, false, false, "XInput battery level");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    struct XInputState
    {
        public uint PacketNumber;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out XInputBatteryInformation info);
}
