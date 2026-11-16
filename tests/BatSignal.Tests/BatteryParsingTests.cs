using BatSignal.Core.Gamepads;
using BatSignal.Core.Hidpp;

namespace BatSignal.Tests;

/// <summary>Battery fields as devices actually send them, decoded without any hardware.</summary>
public class BatteryParsingTests
{
    // Logitech HID++ ------------------------------------------------------------------------

    [Fact]
    public void BatteryStatus_MxKeysStepsAreApproximate()
    {
        // Real reply from an MX Keys: 50 %, next level 20 %, discharging.
        var b = HidppDevice.ParseBatteryStatus([0x32, 0x14, 0x00]);
        Assert.Equal(50, b.Percent);
        Assert.True(b.IsApproximate);
        Assert.Equal(ChargeState.Discharging, b.State);
    }

    [Fact]
    public void BatteryStatus_FineGrainedIsExact()
    {
        var b = HidppDevice.ParseBatteryStatus([0x47, 0x46, 0x01]);
        Assert.Equal(71, b.Percent);
        Assert.False(b.IsApproximate);
        Assert.Equal(ChargeState.Charging, b.State);
    }

    [Fact]
    public void UnifiedBattery_ReadsPercentWhenSupported()
    {
        // Capabilities: levels supported, flags bit 1 = state of charge. Status: 72 %, "good", discharging.
        var b = HidppDevice.ParseUnifiedBattery([0x0F, 0x02], [72, 0x04, 0x00]);
        Assert.Equal(72, b.Percent);
        Assert.Equal(BatteryLevel.Good, b.Level);
        Assert.Equal(ChargeState.Discharging, b.State);
    }

    [Fact]
    public void UnifiedBattery_LevelOnlyDevicesHaveNoPercent()
    {
        var b = HidppDevice.ParseUnifiedBattery([0x0F, 0x00], [0, 0x02, 0x00]);
        Assert.Null(b.Percent);
        Assert.Equal(BatteryLevel.Low, b.Level);
    }

    [Theory]
    [InlineData(4200, 100)]
    [InlineData(4186, 100)]
    [InlineData(3811, 50)]
    [InlineData(3835, 55)] // halfway between 3811 (50 %) and 3859 (60 %)
    [InlineData(3500, 0)]
    [InlineData(3000, 0)]
    public void Voltage_FollowsTheLiIonCurve(int mv, int expected) =>
        Assert.Equal(expected, HidppDevice.EstimatePercent(mv));

    [Fact]
    public void Voltage_ChargingAndFullFlags()
    {
        var charging = HidppDevice.ParseBatteryVoltage([0x0F, 0x00, 0x80]); // 3840 mV, charging
        Assert.Equal(ChargeState.Charging, charging.State);
        Assert.True(charging.IsEstimate);

        var full = HidppDevice.ParseBatteryVoltage([0x10, 0x5A, 0x81]);
        Assert.Equal(ChargeState.Full, full.State);
        Assert.Equal(100, full.Percent);
    }

    // Gamepads ------------------------------------------------------------------------------

    [Fact]
    public void DualSense_UsbReport()
    {
        var r = new byte[64];
        r[0] = 0x01;
        r[53] = 0x14; // charging, level 4 -> 45 %
        var b = GamepadReports.DualSense(r)!;
        Assert.Equal(45, b.Percent);
        Assert.Equal(ChargeState.Charging, b.State);
    }

    [Fact]
    public void DualSense_BluetoothFullReport()
    {
        var r = new byte[78];
        r[0] = 0x31;
        r[54] = 0x08; // discharging, level 8 -> 85 %
        Assert.Equal(85, GamepadReports.DualSense(r)!.Percent);
    }

    [Fact]
    public void DualSense_BluetoothBasicReportHasNoBattery()
    {
        var r = new byte[10];
        r[0] = 0x01;
        Assert.Null(GamepadReports.DualSense(r));
    }

    [Fact]
    public void DualShock4_OnBatteryAndCharging()
    {
        var usb = new byte[64];
        usb[0] = 0x01;
        usb[30] = 0x07; // no cable, raw 7 -> 80 %
        var b = GamepadReports.DualShock4(usb)!;
        Assert.Equal(80, b.Percent);
        Assert.Equal(ChargeState.Discharging, b.State);

        usb[30] = 0x1B; // cable, raw 11 -> full
        Assert.Equal(ChargeState.Full, GamepadReports.DualShock4(usb)!.State);
    }

    [Fact]
    public void SwitchPro_FullReport()
    {
        var r = new byte[49];
        r[0] = 0x30;
        r[2] = 0x81; // battery nibble 8 = full, not charging
        var b = GamepadReports.SwitchPro(r)!;
        Assert.Equal(BatteryLevel.Full, b.Level);
        Assert.Equal(ChargeState.Discharging, b.State);
    }

    [Theory]
    [InlineData(2, 0, BatteryLevel.Critical)]
    [InlineData(3, 1, BatteryLevel.Low)]
    [InlineData(2, 2, BatteryLevel.Good)]
    [InlineData(3, 3, BatteryLevel.Full)]
    public void XInput_Levels(byte type, byte level, BatteryLevel expected) =>
        Assert.Equal(expected, XInputProvider.FromXInput(type, level)!.Level);

    [Theory]
    [InlineData(0)]    // disconnected
    [InlineData(1)]    // wired, no battery
    [InlineData(0xFF)] // unknown
    public void XInput_NoBatteryToReport(byte type) => Assert.Null(XInputProvider.FromXInput(type, 3));
}
