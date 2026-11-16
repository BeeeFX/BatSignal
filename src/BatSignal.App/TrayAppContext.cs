using System.Text.Json;
using Microsoft.Win32;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>
/// Wires everything together: the device monitor feeds the taskbar widgets (or tray icons when
/// the main taskbar's widget can't show), the main panel, battery history and low-battery alerts.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    static string HistoryPath => Path.Combine(Settings.Folder, "history.json");

    readonly Settings _settings = Settings.Load();
    readonly BatteryHistory _history = LoadHistory();
    readonly Control _ui = new();
    readonly DeviceMonitor _monitor;
    readonly TaskbarWidgets _widgets = new();
    readonly TrayIcons _tray = new();
    readonly FlyoutWindow _flyout;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly HashSet<string> _alerted = [];
    readonly Updater _updater = new();
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    IReadOnlyList<DeviceSnapshot> _raw = [];
    IReadOnlyList<DeviceSnapshot> _devices = [];
    DateTimeOffset? _updated;

    public TrayAppContext()
    {
        _ui.CreateControl(); // handle for marshaling monitor updates onto the UI thread

        _flyout = new FlyoutWindow(_settings);
        _flyout.RefreshRequested += () => _monitor!.RefreshNow();
        _flyout.ExitRequested += ExitThread;
        _flyout.SettingsChanged += OnSettingsChanged;
        _flyout.TestAlertRequested += () => Notify("Test alert", "Low-battery alerts look like this.", null);
        _flyout.UpdateRequested += ApplyUpdate;
        _updater.UpdateReady += version =>
        {
            _flyout.SetUpdate(version);
            Notify($"BatSignal {version} is ready", "Open BatSignal and choose Restart to update.", null);
        };

        _widgets.LeftClick += anchor => ToggleFlyout(anchor, settingsPage: false);
        _widgets.RightClick += anchor => ToggleFlyout(anchor, settingsPage: true);
        _tray.LeftClick += pt => ToggleFlyout(new Rectangle(pt, new Size(1, 1)), settingsPage: false);
        _tray.RightClick += pt => ToggleFlyout(new Rectangle(pt, new Size(1, 1)), settingsPage: true);

        _raw = _settings.LastKnownDevices.Select(d => d with { Online = false }).ToList();
        _monitor = new DeviceMonitor(_settings.LastKnownDevices, _history.ChargeRates);
        _flyout.ChargeRate = id => _monitor.Charging.LearnedRate(id);
        _monitor.Log += Logger.Write;
        _monitor.Updated += list => _ui.BeginInvoke(() => OnUpdated(list));

        _timer.Tick += (_, _) => OnTick();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;

        Logger.Write($"Started {Application.ProductVersion}");
        ApplyView();
        _timer.Start();
        _monitor.Start();

        if (_updater.IsInstalled)
        {
            // Keep "Start with Windows" pointing at this installed copy.
            try { if (Settings.StartWithWindows) Settings.StartWithWindows = true; } catch { }
            _updateTimer.Tick += async (_, _) => await _updater.CheckAsync();
            _updateTimer.Start();
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _ui.BeginInvoke(async () => await _updater.CheckAsync()));
        }
    }

    void OnUpdated(IReadOnlyList<DeviceSnapshot> raw)
    {
        _raw = raw;
        _updated = DateTimeOffset.Now;
        _settings.LastKnownDevices = raw.ToList();
        _settings.Save();

        bool changed = false;
        foreach (var d in raw) changed |= _history.Record(d, _updated.Value);
        var rates = _monitor.Charging.Rates;
        if (rates.Count != _history.ChargeRates.Count || rates.Any(r => !_history.ChargeRates.TryGetValue(r.Key, out var v) || v != r.Value))
        {
            _history.ChargeRates = rates.ToDictionary(r => r.Key, r => r.Value);
            Logger.Write("Charge speeds: " + string.Join(", ", rates.Select(r => $"{r.Key} {r.Value:0.#} %/h")));
            changed = true;
        }
        if (changed) SaveHistory();

        ApplyView();
        CheckAlerts();
    }

    void OnSettingsChanged()
    {
        _settings.Save();
        _alerted.RemoveWhere(id => _settings.HiddenDevices.Contains(id));
        ApplyView();
        CheckAlerts();
    }

    IReadOnlyList<DeviceSnapshot> Visible => _devices.Where(d => !_settings.HiddenDevices.Contains(d.Id)).ToList();

    /// <summary>Recomputes what to show, then shows it in the widgets, or in tray icons when the main widget can't show.</summary>
    void ApplyView()
    {
        var now = DateTimeOffset.Now;
        _devices = Customization.Apply(_raw, _settings)
            .Select(d => d with
            {
                TimeRemaining = Remaining(d, now),
                TimeToFull = d.Battery is { State: ChargeState.Charging, Percent: int p } ? _monitor.Charging.TimeToFull(d.Id, p) : null,
            })
            .ToList();

        bool useTray = true;
        if (_settings.Mode == DisplayMode.Taskbar)
        {
            _widgets.SetItems(Visible, _settings.LowBatteryThreshold);
            _widgets.Sync();
            useTray = !_widgets.PrimaryShowing;
        }
        else
        {
            _widgets.DetachAll();
        }

        if (useTray) _tray.Update(Visible, _settings.LowBatteryThreshold);
        else _tray.Clear();
        _flyout.SetData(_devices, _updated);
    }

    TimeSpan? Remaining(DeviceSnapshot d, DateTimeOffset now) =>
        d.Battery is { Percent: int p, State: ChargeState.Discharging or ChargeState.Unknown, IsApproximate: false }
            ? _history.Estimate(d.Id, p, now)
            : null;

    void OnTick()
    {
        if (_settings.Mode != DisplayMode.Taskbar) return;
        _widgets.Sync();
        // The main widget appeared, disappeared or stepped aside: switch between widget and tray icons.
        if (_widgets.PrimaryShowing == _tray.IsShown)
        {
            Logger.Write(_widgets.PrimaryShowing ? "Taskbar widget showing, hiding tray icons" : "Taskbar widget unavailable, using tray icons");
            ApplyView();
        }
    }

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _ui.BeginInvoke(ApplyView);
    }

    void ToggleFlyout(Rectangle anchor, bool settingsPage)
    {
        if (_flyout.IsVisible) { _flyout.HideFlyout(); return; }
        if (_flyout.JustClosed) return; // this click is what closed it
        _flyout.SetData(_devices, _updated);
        _flyout.ShowAt(anchor, settingsPage);
    }

    // Alerts ---------------------------------------------------------------------------------

    void CheckAlerts()
    {
        int threshold = _settings.LowBatteryThreshold;
        foreach (var d in _devices)
        {
            var pct = DeviceText.EffectivePercent(d);
            bool charging = d.Battery?.State is ChargeState.Charging or ChargeState.Full;
            bool low = threshold > 0 && pct is int p && p <= threshold && !charging;

            if (!low && (charging || pct is null || pct > threshold + 5))
                _alerted.Remove(d.Id); // re-arm once it has been charged
            else if (low && d.Online && !_settings.HiddenDevices.Contains(d.Id) && _alerted.Add(d.Id))
            {
                Logger.Write($"Low battery alert: {d.Name} {DeviceText.Percent(d)}");
                var left = d.TimeRemaining is { } t ? $"{DeviceText.Remaining(t)}. " : "";
                Notify($"{d.Name} is at {DeviceText.Percent(d, showApprox: true)}", $"{left}Plug it in soon.", d.Id);
            }
        }
    }

    /// <summary>A Windows notification; falls back to a tray balloon if notifications are unavailable.</summary>
    void Notify(string title, string body, string? deviceId)
    {
        if (Toasts.Show(title, body)) return;
        var icon = deviceId is null ? null : _tray.IconFor(deviceId);
        icon?.ShowBalloonTip(10_000, title, body, ToolTipIcon.Warning);
    }

    // History persistence --------------------------------------------------------------------

    static BatteryHistory LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<BatteryHistory>(File.ReadAllText(HistoryPath)) ?? new BatteryHistory();
        }
        catch (Exception e)
        {
            Logger.Write($"Could not read battery history: {e.Message}");
        }
        return new BatteryHistory();
    }

    void SaveHistory()
    {
        try
        {
            _history.Forget(_raw.Select(d => d.Id));
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history));
        }
        catch (Exception e)
        {
            Logger.Write($"Could not save battery history: {e.Message}");
        }
    }

    void ApplyUpdate()
    {
        Logger.Write("Restarting to update");
        Shutdown();
        _updater.ApplyAndRestart();
    }

    void Shutdown()
    {
        _timer.Stop();
        _updateTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _monitor.Dispose();
        _widgets.Dispose();
        _tray.Dispose();
        _flyout.Close();
        _settings.Save();
    }

    protected override void ExitThreadCore()
    {
        Logger.Write("Exiting");
        Shutdown();
        base.ExitThreadCore();
    }
}
