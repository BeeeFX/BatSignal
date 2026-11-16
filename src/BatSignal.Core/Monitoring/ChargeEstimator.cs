using System.Collections.Concurrent;
using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Monitoring;

/// <summary>
/// Learns how fast each device charges, and fills in the level for devices that stop reporting
/// it while charging (MX Keys).
///
/// A charge session runs from the first "charging" reading to the end of charging. It teaches us
/// the device's rate (percent per hour) when we know both ends:
/// <list type="bullet">
/// <item>Devices that report while charging: from the start level to the moment the highest level
/// was first reached, so time spent sitting at 100 % doesn't count.</item>
/// <item>Silent devices: until the device says "full", or until unplugged if the first exact
/// reading afterwards is below 100 %. Unplugged at 100 % (or a coarse step) is discarded: we
/// can't know when it got there.</item>
/// </list>
/// Rates are averaged with what's already known and are meant to be saved between runs.
/// </summary>
public sealed class ChargeEstimator(IEnumerable<KeyValuePair<string, double>>? learnedRates = null)
{
    /// <summary>Used until a device has been measured: a full charge in about 3 hours.</summary>
    public const double DefaultPercentPerHour = 100.0 / 3;

    const int MinGain = 5;
    static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(6);

    sealed class Session
    {
        public DateTimeOffset Start;
        public int StartPercent;
        public bool Silent;               // device gives no level while charging: we estimate
        public DateTimeOffset LastCharging;
        public int MaxPercent;
        public DateTimeOffset MaxReachedAt;
    }

    readonly ConcurrentDictionary<string, double> _rates = new(learnedRates ?? []);
    readonly Dictionary<string, Session> _sessions = new();

    /// <summary>Learned charge rates in percent per hour, by device id. Safe to read from any thread.</summary>
    public IReadOnlyDictionary<string, double> Rates => new Dictionary<string, double>(_rates);

    public double? LearnedRate(string id) => _rates.TryGetValue(id, out var r) ? r : null;

    /// <summary>True while a silent device's level is being estimated (worth polling faster).</summary>
    public bool AnyEstimating => _sessions.Values.Any(s => s.Silent);

    /// <summary>Time from <paramref name="percent"/> to full, at the learned (or default) rate.</summary>
    public TimeSpan? TimeToFull(string id, int percent) =>
        percent >= 100 ? null : TimeSpan.FromHours((100 - percent) / (LearnedRate(id) ?? DefaultPercentPerHour));

    /// <summary>Turns a raw reading into what we show, and learns from charge sessions. Monitor thread only.</summary>
    public BatteryInfo? Apply(string id, BatteryInfo? now, BatteryInfo? previous, DateTimeOffset time)
    {
        if (now is null) return previous;
        bool reportsLevel = now.Percent is not null || now.Level != BatteryLevel.Unknown;
        _sessions.TryGetValue(id, out var s);

        // Full: the end of a session we can measure exactly.
        if (now.State == ChargeState.Full)
        {
            if (s is not null)
            {
                var reached = !s.Silent && s.MaxPercent >= 100 ? s.MaxReachedAt : time;
                Learn(id, s.StartPercent, 100, reached - s.Start);
                _sessions.Remove(id);
            }
            return reportsLevel ? now : now with { Percent = 100, Level = BatteryLevel.Full, IsApproximate = false };
        }

        if (now.State == ChargeState.Charging)
        {
            // Real level while charging: track it, show it as is.
            if (now.Percent is int p && !now.IsApproximate)
            {
                if (s is null || s.Silent)
                    _sessions[id] = s = new Session { Start = time, StartPercent = p, MaxPercent = p, MaxReachedAt = time };
                if (p > s.MaxPercent) { s.MaxPercent = p; s.MaxReachedAt = time; }
                s.LastCharging = time;
                return now;
            }
            if (reportsLevel) return now; // coarse level while charging: show it, can't learn from it

            // Silent while charging: estimate from the last real level.
            if (s is null)
            {
                if (previous?.Percent is not int start) return now; // nothing to start from
                _sessions[id] = s = new Session { Start = time, StartPercent = start, Silent = true };
            }
            s.LastCharging = time;
            double rate = LearnedRate(id) ?? DefaultPercentPerHour;
            int pct = (int)Math.Min(99, s.StartPercent + (time - s.Start).TotalHours * rate);
            return now with
            {
                Percent = pct,
                Level = pct >= 90 ? BatteryLevel.Full : pct >= 30 ? BatteryLevel.Good : pct >= 10 ? BatteryLevel.Low : BatteryLevel.Critical,
                IsApproximate = true,
                IsEstimate = true,
                Source = "estimated while charging",
            };
        }

        // Not charging any more: close the session.
        if (s is not null)
        {
            if (!s.Silent)
                Learn(id, s.StartPercent, s.MaxPercent, s.MaxReachedAt - s.Start);
            else if (now.Percent is int after && !now.IsApproximate && after < 100)
                Learn(id, s.StartPercent, after, s.LastCharging - s.Start);
            // Silent and unplugged at 100 % or a coarse step: unknown end time, discard.
            _sessions.Remove(id);
        }
        return reportsLevel || previous is null
            ? now
            : now with { Percent = previous.Percent, Level = previous.Level, IsApproximate = previous.IsApproximate };
    }

    void Learn(string id, int from, int to, TimeSpan duration)
    {
        int gained = to - from;
        if (gained < MinGain || duration < MinDuration) return;
        double measured = gained / duration.TotalHours;
        _rates.AddOrUpdate(id, measured, (_, old) => (old + measured) / 2);
    }
}
