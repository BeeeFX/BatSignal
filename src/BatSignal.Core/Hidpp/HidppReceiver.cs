using System.Text;

namespace BatSignal.Core.Hidpp;

public sealed record PairingInfo(int Slot, ushort WirelessPid, DeviceKind Kind, string? Name);

/// <summary>HID++ 1.0 register access on a Unifying / LIGHTSPEED receiver (device index 0xFF).</summary>
public sealed class HidppReceiver(HidppChannel channel)
{
    const byte ReadLongRegister = 0x83;
    const byte RegisterReceiverInfo = 0xB5;

    public HidppChannel Channel { get; } = channel;

    /// <summary>Reads what the receiver remembers about a paired slot (works even when the device is asleep).</summary>
    public PairingInfo? GetPairing(int slot)
    {
        try
        {
            var info = ReadInfo((byte)(0x20 + slot - 1));
            ushort wpid = (ushort)((info[3] << 8) | info[4]);
            if (wpid == 0) return null;
            var kind = (info[7] & 0x0F) switch
            {
                1 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                3 => DeviceKind.Numpad,
                4 => DeviceKind.Presenter,
                8 => DeviceKind.Trackball,
                9 => DeviceKind.Touchpad,
                _ => DeviceKind.Unknown,
            };
            return new PairingInfo(slot, wpid, kind, GetPairedName(slot));
        }
        catch (Exception e) when (e is HidppException or TimeoutException)
        {
            return null;
        }
    }

    string? GetPairedName(int slot)
    {
        try
        {
            var n = ReadInfo((byte)(0x40 + slot - 1));
            int len = Math.Min(n[1], n.Length - 2);
            return len > 0 ? Encoding.ASCII.GetString(n, 2, len).Trim() : null;
        }
        catch (Exception e) when (e is HidppException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Returns the register payload; byte 0 echoes the parameter.</summary>
    byte[] ReadInfo(byte param) =>
        Channel.Request(0xFF, ReadLongRegister, RegisterReceiverInfo, [param], useLong: false)[4..];
}
