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
            ChargeState.Charging when d.Battery?.IsEstimate == true =>
                d.TimeToFull is { } t1 ? $"Charging · estimated, full in {ToFull(t1)}" : "Charging · estimated",
            ChargeState.Charging => d.TimeToFull is { } t2 ? $"Charging · full in {ToFull(t2)}" : "Charging",
            ChargeState.Full => "Fully charged",
            ChargeState.Error => "Battery error",
            _ when d.TimeRemaining is { } left => Remaining(left),
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

    /// <summary>"About 2 days left", "About 5 hours left", "Less than an hour left".</summary>
    public static string Remaining(TimeSpan left) => left.TotalHours switch
    {
        < 1 => "Less than an hour left",
        < 1.5 => "About an hour left",
        < 36 => $"About {Math.Round(left.TotalHours)} hours left",
        _ => $"About {Math.Round(left.TotalDays)} days left",
    };

    /// <summary>"about 25 min", "about 1 h 20 min", "about 4 hours".</summary>
    public static string ToFull(TimeSpan t)
    {
        if (t.TotalMinutes < 55) return $"about {Math.Max(5, (int)Math.Round(t.TotalMinutes / 5) * 5)} min";
        if (t.TotalHours < 3)
        {
            int total = (int)Math.Round(t.TotalMinutes / 10) * 10, h = total / 60, m = total % 60;
            return m == 0 ? $"about {h} h" : $"about {h} h {m} min";
        }
        return $"about {Math.Round(t.TotalHours)} hours";
    }

    /// <summary>"Charges fully in about 2 h", from a learned rate in percent per hour.</summary>
    public static string FullCharge(double percentPerHour) => $"Charges fully in {ToFull(TimeSpan.FromHours(100 / percentPerHour))}";

    static string When(DateTimeOffset t) =>
        t.Date == DateTimeOffset.Now.Date ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
}
