using System.Drawing.Imaging;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--preview", var dir])
        {
            ApplicationConfiguration.Initialize();
            Preview(dir);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\BatSignal.SingleInstance", out bool first);
        if (!first) return; // already running

        Application.ThreadException += (_, e) => Logger.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Write($"Fatal: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }

    /// <summary>
    /// Saves the widget, tray icons and flyout as PNGs, using the last known devices plus a few
    /// synthetic states (low, charging, asleep), to check the visuals without a visible taskbar.
    /// </summary>
    static void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        var real = Settings.Load().LastKnownDevices;
        var mouse = real.FirstOrDefault(d => d.Kind == DeviceKind.Mouse) ?? Fake("PRO X2 SUPERSTRIKE", DeviceKind.Mouse, 72);
        var keyboard = real.FirstOrDefault(d => d.Kind == DeviceKind.Keyboard) ?? Fake("MX Keys Wireless Keyboard", DeviceKind.Keyboard, 50);

        var scenarios = new Dictionary<string, List<DeviceSnapshot>>
        {
            ["real"] = [mouse, keyboard],
            ["states"] =
            [
                mouse with { Battery = mouse.Battery! with { Percent = 12, State = ChargeState.Discharging } },
                keyboard with { Battery = keyboard.Battery! with { Percent = 20, State = ChargeState.Charging } },
                Fake("Gamepad", DeviceKind.Gamepad, 88) with { Online = false, LastSeen = DateTimeOffset.Now.AddHours(-3) },
            ],
        };

        foreach (var (name, devices) in scenarios)
        {
            using var widget = new TaskbarWidget();
            widget.SetItems(devices, 15);
            foreach (var hover in new[] { false, true })
            {
                using var w = widget.Draw(1f, 40, hover);
                // Composite onto a taskbar-like background so the PNG looks like the real thing.
                using var bg = new Bitmap(w.Width + 20, 48);
                using (var g = Graphics.FromImage(bg))
                {
                    g.Clear(Theme.Current.Dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xEE, 0xEE, 0xEE));
                    g.DrawImage(w, 10, 4);
                }
                bg.Save(Path.Combine(dir, $"widget-{name}{(hover ? "-hover" : "")}.png"), ImageFormat.Png);
            }

            int i = 0;
            foreach (var d in devices)
            {
                var pct = DeviceText.EffectivePercent(d) ?? 0;
                using var icon = TrayIcons.Render(d.Kind, pct >= 100 ? "F" : pct.ToString(), Theme.Current.LevelColor(pct, 15), d.Online);
                icon.Save(Path.Combine(dir, $"tray-{name}-{i++}.png"), ImageFormat.Png);
            }

            using var flyout = new FlyoutForm();
            flyout.SetData(devices, 15, DateTimeOffset.Now);
            using var shot = flyout.Snapshot();
            shot.Save(Path.Combine(dir, $"flyout-{name}.png"), ImageFormat.Png);
        }
    }

    static DeviceSnapshot Fake(string name, DeviceKind kind, int pct) => new()
    {
        Id = name, Name = name, Kind = kind, Connection = "LIGHTSPEED", Online = true, LastSeen = DateTimeOffset.Now,
        Battery = new BatteryInfo(pct, BatteryLevel.Good, ChargeState.Discharging, null, false, false, "preview"),
    };
}
