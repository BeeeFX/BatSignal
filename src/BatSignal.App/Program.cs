using System.Drawing.Imaging;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Installer hooks (Velopack). Must run first: during install/update/uninstall it handles
        // the event and exits before any UI starts.
        Velopack.VelopackApp.Build()
            .OnFirstRun(_ => { try { Settings.StartWithWindows = true; } catch { } })
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { Settings.StartWithWindows = false; } catch { }
                Toasts.Unregister();
            })
            .Run();

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
        var settings = Settings.Load();
        var real = Customization.Apply(settings.LastKnownDevices, settings)
            .Where(d => !settings.HiddenDevices.Contains(d.Id)).ToList();
        var mouse = real.FirstOrDefault(d => d.Kind == DeviceKind.Mouse && d.Battery is not null) ?? Fake("PRO X2 SUPERSTRIKE", DeviceKind.Mouse, 72);
        var keyboard = real.FirstOrDefault(d => d.Kind == DeviceKind.Keyboard && d.Battery is not null) ?? Fake("MX Keys Wireless Keyboard", DeviceKind.Keyboard, 50);

        var scenarios = new Dictionary<string, List<DeviceSnapshot>>
        {
            ["real"] = real.Count > 0 ? real.ToList() : [mouse, keyboard],
            ["remaining"] = [mouse with { TimeRemaining = TimeSpan.FromDays(2.4) }, keyboard],
            ["states"] =
            [
                mouse with { Battery = mouse.Battery! with { Percent = 12, State = ChargeState.Discharging } },
                keyboard with { Battery = keyboard.Battery! with { Percent = 55, State = ChargeState.Charging, IsApproximate = true, IsEstimate = true }, TimeToFull = TimeSpan.FromMinutes(80) },
                Fake("Gamepad", DeviceKind.Gamepad, 88) with { Online = false, LastSeen = DateTimeOffset.Now.AddHours(-3) },
                Fake("DualSense", DeviceKind.Gamepad, 45) with { Connection = "USB", TimeRemaining = TimeSpan.FromHours(5) },
                Fake("Work mouse", DeviceKind.Mouse, 64) with { Connection = "Bluetooth", OriginalName = "LOGI M240", Label = "M240", Battery = new BatteryInfo(64, BatteryLevel.Good, ChargeState.Unknown, null, false, false, "preview") },
            ],
        };

        foreach (var (name, devices) in scenarios)
        {
            using var widget = new TaskbarWidget(IntPtr.Zero, true, null, null);
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
                using var icon = TrayIcons.Render(IconCatalog.Shape(d), pct >= 100 ? "F" : pct.ToString(), Theme.Current.LevelColor(pct, 15), d.Online);
                icon.Save(Path.Combine(dir, $"tray-{name}-{i++}.png"), ImageFormat.Png);
            }

            var flyout = new FlyoutWindow(name == "real" ? settings : new Settings());
            flyout.SetData(devices, DateTimeOffset.Now);
            SavePng(flyout.Render(settingsPage: false), Path.Combine(dir, $"flyout-{name}.png"));
            if (name == "real") SavePng(flyout.Render(settingsPage: true), Path.Combine(dir, "settings.png"));
            flyout.Close();
        }
    }

    static void SavePng(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    static DeviceSnapshot Fake(string name, DeviceKind kind, int pct) => new()
    {
        Id = name, Name = name, Kind = kind, Connection = "LIGHTSPEED", Online = true, LastSeen = DateTimeOffset.Now,
        Battery = new BatteryInfo(pct, BatteryLevel.Good, ChargeState.Discharging, null, false, false, "preview"),
    };
}
