using Microsoft.Win32;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>
/// Wires everything together: the device monitor feeds the taskbar widget (or the tray icons
/// when the widget can't attach), the main window, and low-battery alerts.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    readonly Settings _settings = Settings.Load();
    readonly Control _ui = new();
    readonly DeviceMonitor _monitor;
    readonly TaskbarWidget _widget = new();
    readonly TrayIcons _tray = new();
    readonly FlyoutWindow _flyout;
    readonly NotifyIcon _alertIcon = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly HashSet<string> _alerted = [];
    IReadOnlyList<DeviceSnapshot> _raw = [];
    IReadOnlyList<DeviceSnapshot> _devices = [];
    DateTimeOffset? _updated;
    int _ticks;

    public TrayAppContext()
    {
        _ui.CreateControl(); // handle for marshaling monitor updates onto the UI thread

        _flyout = new FlyoutWindow(_settings);
        _flyout.RefreshRequested += () => _monitor!.RefreshNow();
        _flyout.ExitRequested += ExitThread;
        _flyout.SettingsChanged += OnSettingsChanged;

        _widget.LeftClick += () => ToggleFlyout(_widget.ScreenBounds, settingsPage: false);
        _widget.RightClick += () => ToggleFlyout(_widget.ScreenBounds, settingsPage: true);
        _tray.LeftClick += pt => ToggleFlyout(new Rectangle(pt, new Size(1, 1)), settingsPage: false);
        _tray.RightClick += pt => ToggleFlyout(new Rectangle(pt, new Size(1, 1)), settingsPage: true);
        _alertIcon.MouseClick += (_, _) => ToggleFlyout(new Rectangle(Cursor.Position, new Size(1, 1)), settingsPage: false);

        _raw = _settings.LastKnownDevices.Select(d => d with { Online = false }).ToList();
        _devices = Customization.Apply(_raw, _settings);
        _monitor = new DeviceMonitor(_settings.LastKnownDevices);
        _monitor.Log += Logger.Write;
        _monitor.Updated += list => _ui.BeginInvoke(() => OnUpdated(list));

        _timer.Tick += (_, _) => OnTick();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;

        Logger.Write("Started");
        ApplyView();
        _timer.Start();
        _monitor.Start();
    }

    void OnUpdated(IReadOnlyList<DeviceSnapshot> raw)
    {
        _raw = raw;
        _updated = DateTimeOffset.Now;
        _settings.LastKnownDevices = raw.ToList();
        _settings.Save();
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

    /// <summary>Shows the devices in the widget when it's attached, otherwise in tray icons.</summary>
    void ApplyView()
    {
        _devices = Customization.Apply(_raw, _settings);
        bool useWidget = _settings.Mode == DisplayMode.Taskbar && (_widget.IsAttached || _widget.TryAttach());
        if (useWidget)
        {
            _tray.Clear();
            _widget.SetItems(Visible, _settings.LowBatteryThreshold);
        }
        else
        {
            _widget.Detach();
            _tray.Update(Visible, _settings.LowBatteryThreshold);
        }
        _flyout.SetData(_devices, _updated);
    }

    void OnTick()
    {
        _ticks++;
        if (_settings.Mode != DisplayMode.Taskbar) return;
        if (_widget.IsAttached)
        {
            _widget.Tick();
        }
        else if (_ticks % 5 == 0 && _widget.TryAttach())
        {
            Logger.Write("Taskbar widget (re)attached, hiding tray icons");
            ApplyView();
        }
        else if (!_tray.IsShown)
        {
            Logger.Write("Taskbar widget lost, falling back to tray icons");
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
                ShowAlert(d);
        }
        UpdateAlertIcon();
    }

    void ShowAlert(DeviceSnapshot d)
    {
        Logger.Write($"Low battery alert: {d.Name} {DeviceText.Percent(d)}");
        // In tray mode use the device's own icon; in taskbar mode a temporary alert icon carries the toast.
        var icon = _tray.IconFor(d.Id);
        if (icon is null)
        {
            UpdateAlertIcon(force: d);
            icon = _alertIcon;
        }
        icon.ShowBalloonTip(10_000, $"{d.Name} is at {DeviceText.Percent(d, showApprox: true)}", "Plug it in soon.", ToolTipIcon.Warning);
    }

    /// <summary>While a device is low and the widget is in use, a red tray icon stays as a reminder.</summary>
    void UpdateAlertIcon(DeviceSnapshot? force = null)
    {
        var low = force ?? _devices.FirstOrDefault(d => _alerted.Contains(d.Id));
        if (low is null || _tray.IsShown)
        {
            _alertIcon.Visible = false;
            return;
        }
        var palette = Theme.Current;
        using var bmp = TrayIcons.Render(IconCatalog.Shape(low), DeviceText.EffectivePercent(low)?.ToString() ?? "!", palette.Low, true);
        var old = _alertIcon.Icon;
        _alertIcon.Icon = Icon.FromHandle(bmp.GetHicon());
        if (old is not null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
        _alertIcon.Text = DeviceText.Tooltip(low);
        _alertIcon.Visible = true;
    }

    protected override void ExitThreadCore()
    {
        Logger.Write("Exiting");
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _monitor.Dispose();
        _widget.Dispose();
        _tray.Dispose();
        _alertIcon.Visible = false;
        _alertIcon.Dispose();
        _flyout.Close();
        _settings.Save();
        base.ExitThreadCore();
    }
}
