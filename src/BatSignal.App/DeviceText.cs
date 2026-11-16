using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>Shared wording for the widget, tray tooltips and flyout.</summary>
internal static class DeviceText
{
    /// <summary>"72%", "~50%" for stepped devices, or a level word when the device gives no percentage.</summary>
    public static string Percent(DeviceSnapshot d, bool showApprox = false)
    {
        var b = d.Battery;
        if (b is null) return "–";
        if (b.Percent is int p) return (showApprox && b.IsApproximate ? "~" : "") + $"{p}%";
        return b.Level switch
        {
            BatteryLevel.Full => "Full",
            BatteryLevel.Good => "Good",
            BatteryLevel.Low => "Low",
            BatteryLevel.Critical => "Critical",
            _ => "–",
        };
    }

    /// <summary>Number used for colors and alerts; maps coarse levels to a representative value.</summary>
    public static int? EffectivePercent(DeviceSnapshot d) => d.Battery switch
    {
        null => null,
        { Percent: int p } => p,
        { Level: BatteryLevel.Full } => 100,
        { Level: BatteryLevel.Good } => 60,
        { Level: BatteryLevel.Low } => 15,
        { Level: BatteryLevel.Critical } => 5,
        _ => null,
    };

    public static bool IsCharging(DeviceSnapshot d) => d.Battery?.State is ChargeState.Charging;

    public static string Status(DeviceSnapshot d)
    {
        if (!d.Online)
            return d.LastSeen is { } seen ? $"Asleep · seen {When(seen)}" : "Asleep";
        return d.Battery?.State switch
        {
            ChargeState.Charging => "Charging",
            ChargeState.Full => "Fully charged",
            ChargeState.Error => "Battery error",
            _ when d.Battery?.IsApproximate == true => "Reports in steps",
            _ when d.Battery?.IsEstimate == true => "Estimated from voltage",
            ChargeState.Discharging => "On battery",
            _ => "Connected",
        };
    }

    public static string Tooltip(DeviceSnapshot d)
    {
        var text = $"{d.Name} — {Percent(d, showApprox: true)}\n{Status(d)}";
        return text.Length <= 127 ? text : text[..127];
    }

    static string When(DateTimeOffset t) =>
        t.Date == DateTimeOffset.Now.Date ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
}
