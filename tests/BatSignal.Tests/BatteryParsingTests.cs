using BatSignal.Core.Gamepads;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

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
    public void BatteryStatus_ZeroWhileChargingMeansUnknown()
    {
        // Real reply from an MX Keys on charge: level 0, next 0, recharging.
        var b = HidppDevice.ParseBatteryStatus([0x00, 0x00, 0x01]);
        Assert.Null(b.Percent);
        Assert.Equal(BatteryLevel.Unknown, b.Level);
        Assert.Equal(ChargeState.Charging, b.State);
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);
    static BatteryInfo OnBattery50 => HidppDevice.ParseBatteryStatus([0x32, 0x14, 0x00]);
    static BatteryInfo ChargingNoLevel => HidppDevice.ParseBatteryStatus([0x00, 0x00, 0x01]);
    static BatteryInfo FullNoLevel => HidppDevice.ParseBatteryStatus([0x00, 0x00, 0x03]);

    static ChargeEstimator NewEstimator(params (string Id, double Rate)[] rates) =>
        new(rates.Select(r => KeyValuePair.Create(r.Id, r.Rate)));
    static BatteryInfo Mouse(int pct, ChargeState state) =>
        HidppDevice.ParseUnifiedBattery([0x0F, 0x02], [(byte)pct, 0x04, state switch { ChargeState.Charging => 1, ChargeState.Full => 3, _ => 0 }]);

    // Silent devices (MX Keys): estimated level --------------------------------------------

    [Fact]
    public void Silent_ClimbsFromTheLastLevel()
    {
        var est = NewEstimator();
        var first = est.Apply("kb", ChargingNoLevel, OnBattery50, T0)!;
        Assert.Equal(50, first.Percent);
        Assert.True(first.IsApproximate && first.IsEstimate);
        Assert.Equal(ChargeState.Charging, first.State);

        var later = est.Apply("kb", ChargingNoLevel, first, T0.AddMinutes(45))!; // default 3 h for 100 % -> +25
        Assert.Equal(75, later.Percent);
        Assert.True(est.AnyEstimating);
    }

    [Fact]
    public void Silent_StaysBelow100UntilTheDeviceSaysFull()
    {
        var est = NewEstimator();
        est.Apply("kb", ChargingNoLevel, OnBattery50, T0);
        Assert.Equal(99, est.Apply("kb", ChargingNoLevel, null, T0.AddHours(5))!.Percent);

        var full = est.Apply("kb", FullNoLevel, null, T0.AddHours(5))!;
        Assert.Equal(100, full.Percent);
        Assert.False(full.IsApproximate);
        Assert.False(est.AnyEstimating);
    }

    [Fact]
    public void Silent_LearnsFromFullAndUsesItNextTime()
    {
        var est = NewEstimator();
        est.Apply("kb", ChargingNoLevel, OnBattery50, T0);
        est.Apply("kb", FullNoLevel, null, T0.AddHours(1)); // 50 -> 100 in 1 h
        Assert.Equal(50, est.LearnedRate("kb")!.Value, precision: 1);

        est.Apply("kb", ChargingNoLevel, OnBattery50, T0.AddDays(3));
        Assert.Equal(75, est.Apply("kb", ChargingNoLevel, null, T0.AddDays(3).AddMinutes(30))!.Percent);
    }

    [Fact]
    public void Silent_UnpluggedBelow100_LearnsFromTheFirstExactReading()
    {
        var est = NewEstimator();
        var exact40 = HidppDevice.ParseBatteryStatus([0x28, 0x27, 0x00]); // exact device, 40 %
        est.Apply("kb", ChargingNoLevel, exact40, T0);
        est.Apply("kb", ChargingNoLevel, null, T0.AddHours(1));                         // last charging reading
        est.Apply("kb", HidppDevice.ParseBatteryStatus([0x50, 0x4F, 0x00]), null, T0.AddHours(1.05)); // unplugged, 80 %
        Assert.Equal(40, est.LearnedRate("kb")!.Value, precision: 1);                  // +40 in 1 h
    }

    [Fact]
    public void Silent_UnpluggedAt100OrACoarseStep_IsDiscarded()
    {
        var est = NewEstimator();
        est.Apply("kb", ChargingNoLevel, OnBattery50, T0);
        est.Apply("kb", HidppDevice.ParseBatteryStatus([0x64, 0x32, 0x00]), null, T0.AddHours(4)); // 100 % on unplug
        Assert.Null(est.LearnedRate("kb"));

        est.Apply("kb", ChargingNoLevel, OnBattery50, T0.AddDays(1));
        est.Apply("kb", OnBattery50, null, T0.AddDays(1).AddHours(1)); // "50" is a coarse step
        Assert.Null(est.LearnedRate("kb"));
    }

    // Devices that report while charging ---------------------------------------------------

    [Fact]
    public void Reporting_LevelsArePassedThroughAndMeasured()
    {
        var est = NewEstimator();
        var charging = Mouse(40, ChargeState.Charging);
        Assert.Same(charging, est.Apply("mouse", charging, null, T0));
        est.Apply("mouse", Mouse(70, ChargeState.Charging), null, T0.AddHours(0.5));
        est.Apply("mouse", Mouse(70, ChargeState.Discharging), null, T0.AddHours(0.6)); // unplugged
        Assert.Equal(60, est.LearnedRate("mouse")!.Value, precision: 1);              // +30 in 30 min
        Assert.False(est.AnyEstimating);
    }

    [Fact]
    public void Reporting_TimeSpentAt100IsIgnored()
    {
        var est = NewEstimator();
        est.Apply("mouse", Mouse(50, ChargeState.Charging), null, T0);
        est.Apply("mouse", Mouse(100, ChargeState.Charging), null, T0.AddHours(1));   // reached 100 after 1 h
        est.Apply("mouse", Mouse(100, ChargeState.Charging), null, T0.AddHours(8));   // left plugged overnight
        est.Apply("mouse", Mouse(100, ChargeState.Discharging), null, T0.AddHours(9));
        Assert.Equal(50, est.LearnedRate("mouse")!.Value, precision: 1);
    }

    [Fact]
    public void Rates_AreAveragedAndSeededFromSavedValues()
    {
        var est = NewEstimator(("mouse", 30));
        est.Apply("mouse", Mouse(40, ChargeState.Charging), null, T0);
        est.Apply("mouse", Mouse(100, ChargeState.Full), null, T0.AddHours(1)); // measured 60 %/h
        Assert.Equal(45, est.LearnedRate("mouse")!.Value, precision: 1);       // (30 + 60) / 2
        Assert.Equal(1.5, est.TimeToFull("other", 50)!.Value.TotalHours, precision: 2); // default: 50 % at 33 %/h
        Assert.Equal(1.0, est.TimeToFull("mouse", 55)!.Value.TotalHours, precision: 2); // learned: 45 % at 45 %/h
    }

    [Theory]
    [InlineData(12, "about 10 min")]
    [InlineData(50, "about 50 min")]
    [InlineData(80, "about 1 h 20 min")]
    [InlineData(120, "about 2 h")]
    [InlineData(250, "about 4 hours")]
    public void ToFull_Wording(int minutes, string expected) =>
        Assert.Equal(expected, BatSignal.App.DeviceText.ToFull(TimeSpan.FromMinutes(minutes)));

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
