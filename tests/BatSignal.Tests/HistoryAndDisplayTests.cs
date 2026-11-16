using BatSignal.App;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.Tests;

public class HistoryAndDisplayTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    static DeviceSnapshot Mouse(int pct, ChargeState state = ChargeState.Discharging, bool online = true) => new()
    {
        Id = "C54D/1",
        Name = "PRO X2 SUPERSTRIKE",
        Kind = DeviceKind.Mouse,
        Connection = "LIGHTSPEED",
        Online = online,
        Battery = new BatteryInfo(pct, BatteryLevel.Good, state, null, false, false, "test"),
    };

    // Battery history ---------------------------------------------------------------------------

    [Fact]
    public void Estimate_NeedsAnHourAndThreePoints()
    {
        var h = new BatteryHistory();
        h.Record(Mouse(80), T0);
        h.Record(Mouse(79), T0.AddMinutes(30));
        Assert.Null(h.Estimate("C54D/1", 79, T0.AddMinutes(30))); // too soon, too small
        Assert.Null(h.Estimate("C54D/1", 79, T0.AddHours(3)));    // long enough, but only 1 point
    }

    [Fact]
    public void Estimate_UsesAverageRateSinceTheCycleStarted()
    {
        var h = new BatteryHistory();
        h.Record(Mouse(80), T0);
        h.Record(Mouse(70), T0.AddHours(10)); // 1 point an hour
        var left = h.Estimate("C54D/1", 70, T0.AddHours(10));
        Assert.Equal(70, left!.Value.TotalHours, precision: 1);
    }

    [Fact]
    public void Estimate_QuietSpellStretchesTheEstimate()
    {
        var h = new BatteryHistory();
        h.Record(Mouse(80), T0);
        h.Record(Mouse(70), T0.AddHours(10));
        // Still 70 % ten hours later: half the rate, twice the time left.
        Assert.Equal(140, h.Estimate("C54D/1", 70, T0.AddHours(20))!.Value.TotalHours, precision: 1);
    }

    [Fact]
    public void Charging_StartsANewCycle()
    {
        var h = new BatteryHistory();
        h.Record(Mouse(80), T0);
        h.Record(Mouse(60), T0.AddHours(10));
        Assert.True(h.Record(Mouse(60, ChargeState.Charging), T0.AddHours(11)));
        h.Record(Mouse(100), T0.AddHours(13));
        Assert.Null(h.Estimate("C54D/1", 100, T0.AddHours(13)));
    }

    [Fact]
    public void JumpUp_MeansItWasChargedWhileWeWerentWatching()
    {
        var h = new BatteryHistory();
        h.Record(Mouse(40), T0);
        h.Record(Mouse(35), T0.AddHours(5));
        h.Record(Mouse(95), T0.AddHours(30)); // charged overnight with the app closed
        Assert.Single(h.Samples["C54D/1"]);
    }

    [Fact]
    public void AsleepOrSteppedReadings_AreIgnored()
    {
        var h = new BatteryHistory();
        Assert.False(h.Record(Mouse(80, online: false), T0));
        var stepped = Mouse(50) with { Battery = Mouse(50).Battery! with { IsApproximate = true } };
        Assert.False(h.Record(stepped, T0));
    }

    // Wording ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.5, "Less than an hour left")]
    [InlineData(1.2, "About an hour left")]
    [InlineData(5, "About 5 hours left")]
    [InlineData(57.6, "About 2 days left")]
    public void Remaining_Wording(double hours, string expected) =>
        Assert.Equal(expected, DeviceText.Remaining(TimeSpan.FromHours(hours)));

    [Fact]
    public void Percent_ShowsStepsAsApproximate()
    {
        var keys = Mouse(50) with { Battery = Mouse(50).Battery! with { IsApproximate = true } };
        Assert.Equal("50%", DeviceText.Percent(keys));
        Assert.Equal("~50%", DeviceText.Percent(keys, showApprox: true));
    }

    [Fact]
    public void Status_PrefersTimeRemainingOverOnBattery()
    {
        Assert.Equal("On battery", DeviceText.Status(Mouse(70)));
        Assert.Equal("About 3 days left", DeviceText.Status(Mouse(70) with { TimeRemaining = TimeSpan.FromDays(3) }));
        Assert.Equal("Charging", DeviceText.Status(Mouse(70, ChargeState.Charging) with { TimeRemaining = TimeSpan.FromDays(3) }));
    }

    // Customization ---------------------------------------------------------------------------

    [Fact]
    public void Customization_RenamesRelabelsAndReorders()
    {
        var keyboard = Mouse(50) with { Id = "C52B/1", Name = "MX Keys Wireless Keyboard", Kind = DeviceKind.Keyboard };
        var settings = new Settings { Order = ["C52B/1", "C54D/1"] };
        settings.CustomizationFor("C54D/1").Name = "Gaming mouse";
        settings.CustomizationFor("C54D/1").Label = "g2";

        var result = Customization.Apply([Mouse(70), keyboard], settings);

        Assert.Equal(["C52B/1", "C54D/1"], result.Select(d => d.Id));
        var mouse = result[1];
        Assert.Equal("Gaming mouse", mouse.Name);
        Assert.Equal("PRO X2 SUPERSTRIKE", mouse.OriginalName);
        Assert.Equal("G2", mouse.Label);
    }

    [Fact]
    public void IconCatalog_DefaultsFollowTheKindAndChosenIconWins()
    {
        var mouse = Mouse(70);
        Assert.Equal(IconCatalog.Find("mouse")!.Glyph, IconCatalog.Glyph(mouse));
        Assert.Equal(IconCatalog.Find("star")!.Glyph, IconCatalog.Glyph(mouse with { IconKey = "star" }));
        Assert.Equal(DeviceKind.Gamepad, IconCatalog.Shape(mouse with { IconKey = "gamepad" }));
    }
}
