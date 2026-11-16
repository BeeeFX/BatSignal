using Microsoft.Win32;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>
/// Wires everything together: the device monitor feeds the taskbar widget (or the tray icons
/// when the widget can't attach), the flyout, the right-click menu and low-battery alerts.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    readonly Settings _settings = Settings.Load();
    readonly Control _ui = new();
    readonly DeviceMonitor _monitor;
    readonly TaskbarWidget _widget = new();
    readonly TrayIcons _tray;
    readonly FlyoutForm _flyout = new();
    readonly ContextMenuStrip _menu = new();
    readonly NotifyIcon _alertIcon = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly HashSet<string> _alerted = [];
    IReadOnlyList<DeviceSnapshot> _devices = [];
    DateTimeOffset? _updated;
    int _ticks;

    public TrayAppContext()
    {
        _ui.CreateControl(); // handle for marshaling monitor updates onto the UI thread
        _tray = new TrayIcons(_menu);
        _menu.Opening += (_, _) => BuildMenu();

        _widget.LeftClick += () => ToggleFlyout(_widget.ScreenBounds);
        _widget.RightClick += () => ShowMenu(Cursor.Position);
        _tray.LeftClick += pt => ToggleFlyout(new Rectangle(pt, new Size(1, 1)));
        _flyout.RefreshClicked += () => _monitor!.RefreshNow();
        _flyout.SettingsClicked += pt => { _flyout.Hide(); ShowMenu(pt); };
        _alertIcon.MouseClick += (_, _) => ToggleFlyout(new Rectangle(Cursor.Position, new Size(1, 1)));

        _devices = _settings.LastKnownDevices.Select(d => d with { Online = false }).ToList();
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

    void OnUpdated(IReadOnlyList<DeviceSnapshot> devices)
    {
        _devices = devices;
        _updated = DateTimeOffset.Now;
        _settings.LastKnownDevices = devices.ToList();
        _settings.Save();
        ApplyView();
        CheckAlerts();
    }

    IReadOnlyList<DeviceSnapshot> Visible => _devices.Where(d => !_settings.HiddenDevices.Contains(d.Id)).ToList();

    /// <summary>Shows the devices in the widget when it's attached, otherwise in tray icons.</summary>
    void ApplyView()
    {
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
        _flyout.SetData(_devices, _settings.LowBatteryThreshold, _updated);
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

    // Flyout and menu ------------------------------------------------------------------------

    void ToggleFlyout(Rectangle anchor)
    {
        if (_flyout.Visible) { _flyout.Hide(); return; }
        if (_flyout.JustClosed) return; // this click is what closed it
        _flyout.SetData(_devices, _settings.LowBatteryThreshold, _updated);
        _flyout.ShowAt(anchor);
    }

    void ShowMenu(Point screenPoint)
    {
        _menu.Show(screenPoint);
        // Make the menu foreground so clicking elsewhere closes it (we were clicked inside Explorer's taskbar).
        Native.SetForegroundWindow(_menu.Handle);
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Refresh now", null, (_, _) => _monitor.RefreshNow());
        _menu.Items.Add(new ToolStripSeparator());

        var show = new ToolStripMenuItem("Show in");
        show.DropDownItems.Add(Radio("Taskbar", _settings.Mode == DisplayMode.Taskbar, () => SetMode(DisplayMode.Taskbar)));
        show.DropDownItems.Add(Radio("Notification area", _settings.Mode == DisplayMode.Tray, () => SetMode(DisplayMode.Tray)));
        _menu.Items.Add(show);

        var alert = new ToolStripMenuItem("Low-battery alert");
        foreach (var t in new[] { 0, 10, 15, 20, 25, 30 })
            alert.DropDownItems.Add(Radio(t == 0 ? "Off" : $"{t}%", _settings.LowBatteryThreshold == t, () =>
            {
                _settings.LowBatteryThreshold = t;
                _alerted.Clear();
                Save();
            }));
        _menu.Items.Add(alert);

        var devices = new ToolStripMenuItem("Shown devices") { Enabled = _devices.Count > 0 };
        foreach (var d in _devices)
        {
            var id = d.Id;
            devices.DropDownItems.Add(new ToolStripMenuItem(d.Name, null, (_, _) =>
            {
                if (!_settings.HiddenDevices.Remove(id)) _settings.HiddenDevices.Add(id);
                Save();
            }) { Checked = !_settings.HiddenDevices.Contains(id) });
        }
        _menu.Items.Add(devices);

        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try { Settings.StartWithWindows = !Settings.StartWithWindows; }
            catch (Exception e) { Logger.Write($"Autostart change failed: {e.Message}"); }
        }) { Checked = Settings.StartWithWindows });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    static ToolStripMenuItem Radio(string text, bool selected, Action onClick) =>
        new(text, null, (_, _) => onClick()) { Checked = selected };

    void SetMode(DisplayMode mode)
    {
        _settings.Mode = mode;
        Save();
    }

    void Save()
    {
        _settings.Save();
        ApplyView();
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
        using var bmp = TrayIcons.Render(low.Kind, DeviceText.EffectivePercent(low)?.ToString() ?? "!", palette.Low, true);
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
        _flyout.Dispose();
        _settings.Save();
        base.ExitThreadCore();
    }
}
