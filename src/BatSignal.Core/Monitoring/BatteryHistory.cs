using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Monitoring;

/// <summary>
/// Remembers how each device's battery went down since its last charge, and estimates how long
/// it has left. Time is wall-clock, sleep included, because that's what "2 days left" means.
/// </summary>
public sealed class BatteryHistory
{
    public sealed record Sample(DateTimeOffset Time, int Percent);

    static readonly TimeSpan Window = TimeSpan.FromDays(14);
    static readonly TimeSpan MinSpan = TimeSpan.FromHours(1);
    const int MinDrop = 3;

    /// <summary>Discharge samples per device id, oldest first, since the last charge. Public for JSON.</summary>
    public Dictionary<string, List<Sample>> Samples { get; set; } = new();

    /// <summary>Adds a reading. Returns true when the history changed (worth saving).</summary>
    public bool Record(DeviceSnapshot d, DateTimeOffset now)
    {
        if (!d.Online || d.Battery is not { Percent: int pct } b || b.IsApproximate) return false;
        if (!Samples.TryGetValue(d.Id, out var list)) Samples[d.Id] = list = [];

        if (b.State is ChargeState.Charging or ChargeState.Full)
        {
            // A new discharge cycle starts after charging.
            if (list.Count == 0) return false;
            list.Clear();
            return true;
        }
        if (list.Count > 0 && pct > list[^1].Percent + 2) list.Clear(); // charged while we weren't watching
        if (list.Count > 0 && list[^1].Percent == pct) return false;    // only keep changes

        list.Add(new Sample(now, pct));
        list.RemoveAll(s => now - s.Time > Window);
        return true;
    }

    /// <summary>
    /// Estimated time left, or null until there's enough history: at least an hour and a
    /// 3-point drop since the cycle started. Uses the average rate from the first sample to now,
    /// so a long quiet spell (no drop) correctly stretches the estimate.
    /// </summary>
    public TimeSpan? Estimate(string id, int currentPercent, DateTimeOffset now)
    {
        if (!Samples.TryGetValue(id, out var list) || list.Count == 0) return null;
        var first = list[0];
        int drop = first.Percent - currentPercent;
        var span = now - first.Time;
        if (drop < MinDrop || span < MinSpan) return null;

        double perHour = drop / span.TotalHours;
        var left = TimeSpan.FromHours(currentPercent / perHour);
        return left > TimeSpan.FromDays(60) ? TimeSpan.FromDays(60) : left;
    }

    public void Forget(IEnumerable<string> keepIds)
    {
        var keep = keepIds.ToHashSet();
        foreach (var id in Samples.Keys.Where(k => !keep.Contains(k)).ToList()) Samples.Remove(id);
    }
}
