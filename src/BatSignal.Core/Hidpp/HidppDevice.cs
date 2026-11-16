using System.Text;

namespace BatSignal.Core.Hidpp;

public enum DeviceKind { Unknown, Keyboard, Mouse, Numpad, Presenter, RemoteControl, Trackball, Touchpad, Headset, Gamepad, Receiver }

public enum ChargeState { Unknown, Discharging, Charging, Full, Error }

public enum BatteryLevel { Unknown, Critical, Low, Good, Full }

/// <param name="Percent">Battery percentage, or null when the device only reports coarse levels.</param>
/// <param name="IsEstimate">True when the percentage was computed from a voltage reading.</param>
/// <param name="IsApproximate">True when the device reports coarse steps (e.g. 100/50/20/5) rather than exact values.</param>
/// <param name="Source">Which HID++ feature produced this reading (for diagnostics).</param>
public sealed record BatteryInfo(int? Percent, BatteryLevel Level, ChargeState State, int? VoltageMv, bool IsEstimate, bool IsApproximate, string Source);

/// <summary>A HID++ 2.0 device at a given index on a channel (1-6 on a receiver, 0xFF when direct).</summary>
public sealed class HidppDevice(HidppChannel channel, byte index)
{
    const byte SoftwareId = 0x0A; // any non-zero value; 0 is reserved for notifications

    public const ushort FeatureRoot = 0x0000;
    public const ushort FeatureDeviceName = 0x0005;
    public const ushort FeatureBatteryStatus = 0x1000;
    public const ushort FeatureBatteryVoltage = 0x1001;
    public const ushort FeatureUnifiedBattery = 0x1004;

    readonly Dictionary<ushort, byte> _featureIndex = new() { [FeatureRoot] = 0 };

    public HidppChannel Channel { get; } = channel;
    public byte Index { get; } = index;

    /// <summary>Feature index used by the last battery read; battery events arrive with this index.</summary>
    public byte? BatteryFeatureIndex { get; private set; }

    /// <summary>Pings the device. Returns its HID++ version, or null if it is offline or asleep.</summary>
    public Version? Ping(int timeoutMs = 800)
    {
        try
        {
            var r = Channel.Request(Index, 0x00, (byte)(0x10 | SoftwareId), [0, 0, 0x5A], useLong: true, timeoutMs);
            return new Version(r[4], r[5]);
        }
        catch (HidppException e) when (e.IsHidpp10 && e.ErrorCode == 0x01)
        {
            return new Version(1, 0); // HID++ 1.0 device: root feature call is an invalid sub-id
        }
        catch (Exception e) when (e is HidppException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Returns the index of a feature, or null if the device does not support it.</summary>
    public byte? FeatureIndex(ushort featureId)
    {
        if (!_featureIndex.TryGetValue(featureId, out var idx))
        {
            var r = CallIndex(0, 0, (byte)(featureId >> 8), (byte)featureId);
            idx = r[0];
            _featureIndex[featureId] = idx;
        }
        return featureId == FeatureRoot || idx != 0 ? idx : null;
    }

    public bool Has(ushort featureId) => FeatureIndex(featureId) is not null;

    /// <summary>Calls a function of a feature and returns the 16-byte reply payload.</summary>
    public byte[] Call(ushort featureId, int function, params byte[] args)
    {
        var idx = FeatureIndex(featureId)
                  ?? throw new NotSupportedException($"Feature 0x{featureId:X4} not supported.");
        return CallIndex(idx, function, args);
    }

    byte[] CallIndex(byte featureIndex, int function, params byte[] args) =>
        Channel.Request(Index, featureIndex, (byte)((function << 4) | SoftwareId), args, useLong: true)[4..];

    public string? GetName()
    {
        if (!Has(FeatureDeviceName)) return null;
        int length = Call(FeatureDeviceName, 0)[0];
        var bytes = new List<byte>(length);
        while (bytes.Count < length)
        {
            var chunk = Call(FeatureDeviceName, 1, (byte)bytes.Count);
            var take = chunk.Take(length - bytes.Count).TakeWhile(b => b != 0).ToArray();
            if (take.Length == 0) break;
            bytes.AddRange(take);
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    public DeviceKind GetKind()
    {
        if (!Has(FeatureDeviceName)) return DeviceKind.Unknown;
        return Call(FeatureDeviceName, 2)[0] switch
        {
            0 => DeviceKind.Keyboard,
            1 => DeviceKind.RemoteControl,
            2 => DeviceKind.Numpad,
            3 => DeviceKind.Mouse,
            4 => DeviceKind.Touchpad,
            5 => DeviceKind.Trackball,
            6 => DeviceKind.Presenter,
            7 => DeviceKind.Receiver,
            _ => DeviceKind.Unknown,
        };
    }

    /// <summary>Reads the battery using the best feature the device supports, or null if it has none.</summary>
    public BatteryInfo? ReadBattery()
    {
        if (Has(FeatureUnifiedBattery)) return ReadUnifiedBattery();
        if (Has(FeatureBatteryStatus)) return ReadBatteryStatus();
        if (Has(FeatureBatteryVoltage)) return ReadBatteryVoltage();
        return null;
    }

    BatteryInfo ReadUnifiedBattery()
    {
        var info = ParseUnifiedBattery(Call(FeatureUnifiedBattery, 0), Call(FeatureUnifiedBattery, 1));
        BatteryFeatureIndex = FeatureIndex(FeatureUnifiedBattery);
        return info;
    }

    BatteryInfo ReadBatteryStatus()
    {
        var info = ParseBatteryStatus(Call(FeatureBatteryStatus, 0));
        BatteryFeatureIndex = FeatureIndex(FeatureBatteryStatus);
        return info;
    }

    BatteryInfo ReadBatteryVoltage()
    {
        var info = ParseBatteryVoltage(Call(FeatureBatteryVoltage, 0));
        BatteryFeatureIndex = FeatureIndex(FeatureBatteryVoltage);
        return info;
    }

    // Reply parsers, kept pure so they can be unit-tested without hardware ---------------------

    /// <summary>0x1004: GetCapabilities reply + GetStatus reply.</summary>
    public static BatteryInfo ParseUnifiedBattery(ReadOnlySpan<byte> caps, ReadOnlySpan<byte> s)
    {
        bool hasPercent = (caps[1] & 0x02) != 0;
        var level = (s[1] & 0x08) != 0 ? BatteryLevel.Full
                  : (s[1] & 0x04) != 0 ? BatteryLevel.Good
                  : (s[1] & 0x02) != 0 ? BatteryLevel.Low
                  : (s[1] & 0x01) != 0 ? BatteryLevel.Critical
                  : BatteryLevel.Unknown;
        var state = s[2] switch
        {
            0 => ChargeState.Discharging,
            1 or 2 => ChargeState.Charging,
            3 => ChargeState.Full,
            4 => ChargeState.Error,
            _ => ChargeState.Unknown,
        };
        return new BatteryInfo(hasPercent ? s[0] : null, level, state, null, false, false, "0x1004 unified battery");
    }

    /// <summary>0x1000: GetBatteryLevelStatus reply (level, next level, status).</summary>
    public static BatteryInfo ParseBatteryStatus(ReadOnlySpan<byte> s)
    {
        var state = s[2] switch
        {
            0 => ChargeState.Discharging,
            1 or 2 or 4 => ChargeState.Charging,
            3 => ChargeState.Full,
            _ => ChargeState.Error,
        };
        int pct = s[0], next = s[1];
        // While charging, devices like MX Keys report level 0 = "unknown", not empty.
        if (pct == 0 && state is ChargeState.Charging or ChargeState.Full)
            return new BatteryInfo(null, BatteryLevel.Unknown, state, null, false, false, "0x1000 battery status");
        // Devices like MX Keys report 100/50/20/5: "next level" is far below the current one.
        bool stepped = next > 0 && pct - next > 10;
        return new BatteryInfo(pct, LevelFromPercent(pct), state, null, false, stepped, "0x1000 battery status");
    }

    /// <summary>0x1001: GetBatteryVoltage reply (millivolts big-endian, flags).</summary>
    public static BatteryInfo ParseBatteryVoltage(ReadOnlySpan<byte> s)
    {
        int mv = (s[0] << 8) | s[1];
        byte flags = s[2];
        var state = ChargeState.Discharging;
        if ((flags & 0x80) != 0)
        {
            state = (flags & 0x03) switch
            {
                0 => ChargeState.Charging,
                1 => ChargeState.Full,
                2 => ChargeState.Discharging, // plugged in but not charging
                _ => ChargeState.Error,
            };
        }
        int pct = state == ChargeState.Full ? 100 : EstimatePercent(mv);
        return new BatteryInfo(pct, LevelFromPercent(pct), state, mv, true, false, "0x1001 battery voltage");
    }
    static BatteryLevel LevelFromPercent(int pct) =>
        pct >= 90 ? BatteryLevel.Full : pct >= 30 ? BatteryLevel.Good : pct >= 10 ? BatteryLevel.Low : BatteryLevel.Critical;

    // Li-ion discharge curve (mV -> %) commonly used for Logitech G devices; interpolated linearly.
    static readonly (int mv, int pct)[] VoltageCurve =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];

    public static int EstimatePercent(int mv)
    {
        if (mv >= VoltageCurve[0].mv) return 100;
        for (int i = 1; i < VoltageCurve.Length; i++)
        {
            var (hiMv, hiPct) = VoltageCurve[i - 1];
            var (loMv, loPct) = VoltageCurve[i];
            if (mv >= loMv)
                return (int)Math.Round(loPct + (hiPct - loPct) * (mv - loMv) / (double)(hiMv - loMv));
        }
        return 0;
    }
}
