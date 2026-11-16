using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatSignal.Core.Monitoring;
using Color = System.Drawing.Color;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using FrameworkElement = System.Windows.FrameworkElement;
using Rectangle = System.Drawing.Rectangle;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Screen = System.Windows.Forms.Screen;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BatSignal.App;

/// <summary>
/// The main window: a flyout above the taskbar with two pages, the device list and Settings
/// (device names, icons, labels, order, visibility, and general options).
/// </summary>
internal sealed partial class FlyoutWindow
{
    static readonly int[] Thresholds = [0, 10, 15, 20, 25, 30];

    readonly Settings _settings;
    readonly ObservableCollection<DeviceSettingsModel> _customize = [];
    IReadOnlyList<DeviceSnapshot> _all = [];
    DateTimeOffset? _updated;
    double _bottomDip;
    long _hiddenAt;
    bool _loadingSettings;

    public event Action? RefreshRequested;
    public event Action? ExitRequested;
    public event Action? TestAlertRequested;

    /// <summary>Learned charge speed for a device (percent per hour), shown in Settings.</summary>
    public Func<string, double?> ChargeRate { get; set; } = _ => null;
    public event Action? UpdateRequested;

    /// <summary>Shows the "update ready" banner on the devices page.</summary>
    public void SetUpdate(string version)
    {
        UpdateText.Text = $"Version {version} is ready";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>Raised after the user changed a setting; the settings object is already updated.</summary>
    public event Action? SettingsChanged;

    public FlyoutWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        CustomizeList.ItemsSource = _customize;
        foreach (var t in Thresholds)
            AlertBox.Items.Add(new ComboBoxItem { Content = t == 0 ? "Off" : $"Below {t}%" });
        VersionText.Text = $"BatSignal {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        ApplyTheme();
        SourceInitialized += (_, _) => ApplyFrame();
        Deactivated += (_, _) => { CommitEdits(); HideFlyout(); };
        SizeChanged += (_, _) => { if (IsVisible) Top = _bottomDip - ActualHeight; };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HideFlyout(); };
    }

    /// <summary>True right after the flyout closed because the user clicked the widget; that click should not reopen it.</summary>
    public bool JustClosed => Environment.TickCount64 - _hiddenAt < 300;

    /// <summary>Devices with the user's customization applied, in display order (hidden ones included).</summary>
    public void SetData(IReadOnlyList<DeviceSnapshot> all, DateTimeOffset? updated)
    {
        _all = all;
        _updated = updated;
        RefreshDevicesPage();
    }

    void RefreshDevicesPage()
    {
        var p = Theme.Current;
        var visible = _all.Where(d => !_settings.HiddenDevices.Contains(d.Id)).ToList();
        DeviceList.ItemsSource = visible.Select(d => DeviceRowModel.From(d, p, _settings.LowBatteryThreshold)).ToList();
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatedText.Text = _updated is { } u ? $"Updated {u:HH:mm}" : "";
        AlertText.Text = _settings.LowBatteryThreshold > 0 ? $"Alert below {_settings.LowBatteryThreshold}%" : "Low-battery alerts off";
    }

    // Pages ------------------------------------------------------------------------------------

    void ShowDevicesPage()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Visible;
        RefreshDevicesPage();
    }

    void ShowSettingsPage()
    {
        _loadingSettings = true;
        _customize.Clear();
        foreach (var d in _all)
        {
            var m = DeviceSettingsModel.From(d, _settings, ChargeRate(d.Id));
            m.Changed += OnDeviceSettingChanged;
            _customize.Add(m);
        }
        ModeBox.SelectedIndex = _settings.Mode == DisplayMode.Taskbar ? 0 : 1;
        AlertBox.SelectedIndex = Math.Max(0, Array.IndexOf(Thresholds, _settings.LowBatteryThreshold));
        AutostartBox.IsChecked = Settings.StartWithWindows;
        _loadingSettings = false;

        DevicesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
    }

    void OnDeviceSettingChanged(DeviceSettingsModel m)
    {
        var c = _settings.CustomizationFor(m.Id);
        c.Name = string.IsNullOrWhiteSpace(m.CustomName) ? null : m.CustomName.Trim();
        c.Icon = m.IconKey;
        c.Label = m.EffectiveLabel;
        if (c.IsEmpty) _settings.Devices.Remove(m.Id);

        if (m.Show) _settings.HiddenDevices.Remove(m.Id);
        else _settings.HiddenDevices.Add(m.Id);

        SettingsChanged?.Invoke();
    }

    void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceSettingsModel m) return;
        int i = _customize.IndexOf(m), j = i + delta;
        if (i < 0 || j < 0 || j >= _customize.Count) return;
        _customize.Move(i, j);
        _settings.Order = _customize.Select(x => x.Id).ToList();
        SettingsChanged?.Invoke();
    }

    // Event handlers (wired in XAML) -----------------------------------------------------------

    /// <summary>A rename is saved when its box loses focus; closing the panel or going back must save it too.</summary>
    void CommitEdits()
    {
        if (System.Windows.Input.FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    void Name_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape)) return;
        var box = (System.Windows.Controls.TextBox)sender;
        var binding = box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        if (e.Key == System.Windows.Input.Key.Enter) binding?.UpdateSource(); else binding?.UpdateTarget();
        System.Windows.Input.Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(this, this);
        e.Handled = true;
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();
    void Back_Click(object sender, RoutedEventArgs e) { CommitEdits(); ShowDevicesPage(); }
    void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    void TestAlert_Click(object sender, RoutedEventArgs e) => TestAlertRequested?.Invoke();
    void Update_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();
    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceSettingsModel m) m.Reset();
    }

    void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ModeBox.SelectedIndex < 0) return;
        _settings.Mode = ModeBox.SelectedIndex == 0 ? DisplayMode.Taskbar : DisplayMode.Tray;
        SettingsChanged?.Invoke();
    }

    void Alert_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || AlertBox.SelectedIndex < 0) return;
        _settings.LowBatteryThreshold = Thresholds[AlertBox.SelectedIndex];
        SettingsChanged?.Invoke();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Settings.StartWithWindows = AutostartBox.IsChecked == true; }
        catch (Exception ex) { Logger.Write($"Autostart change failed: {ex.Message}"); }
        AutostartBox.IsChecked = Settings.StartWithWindows;
    }

    // Showing / hiding -------------------------------------------------------------------------

    /// <summary>Opens above the anchor (widget or tray icon, screen pixels), right-aligned to it.</summary>
    public void ShowAt(Rectangle anchor, bool settingsPage, bool activate = true)
    {
        ApplyTheme();
        if (settingsPage) ShowSettingsPage(); else ShowDevicesPage();

        if (!IsVisible)
        {
            Left = -10000;
            Top = -10000;
            Show();
        }
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        UpdateLayout();
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        SettingsScroll.MaxHeight = Math.Max(240, wa.Height / dpi.DpiScaleY - 180); // grow to fit the screen; the wheel scrolls beyond
        double widthPx = ActualWidth * dpi.DpiScaleX;
        double xPx = Math.Clamp(anchor.Right - widthPx, wa.Left + 12 * dpi.DpiScaleX, wa.Right - widthPx - 12 * dpi.DpiScaleX);
        Left = xPx / dpi.DpiScaleX;
        _bottomDip = (wa.Bottom - 12 * dpi.DpiScaleY) / dpi.DpiScaleY;
        Top = _bottomDip - ActualHeight;

        if (!activate) return;
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        _hiddenAt = Environment.TickCount64;
    }

    // Theme ------------------------------------------------------------------------------------

    void ApplyTheme()
    {
        var p = Theme.Current;
        Resources["Bs.Background"] = Wpf.Brush(p.Background);
        Resources["Bs.Surface"] = Wpf.Brush(p.Surface);
        Resources["Bs.Hover"] = Wpf.Brush(p.Dark ? Color.FromArgb(0x48, 0x48, 0x48) : Color.FromArgb(0xE2, 0xE2, 0xE2));
        Resources["Bs.Text"] = Wpf.Brush(p.Text);
        Resources["Bs.Secondary"] = Wpf.Brush(p.Secondary);
        Resources["Bs.Muted"] = Wpf.Brush(p.Muted);
        Resources["Bs.Border"] = Wpf.Brush(p.Border);
        Resources["Bs.Track"] = Wpf.Brush(p.Track);
        Resources["Bs.Input"] = Wpf.Brush(p.Dark ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.White);
        Resources["Bs.InputBorder"] = Wpf.Brush(p.Dark ? Color.FromArgb(0x55, 0x55, 0x55) : Color.FromArgb(0xC8, 0xC8, 0xC8));
        Resources["Bs.Accent"] = Wpf.Brush(Theme.Accent(p.Dark));
        Resources["Bs.OnAccent"] = Wpf.Brush(p.Dark ? Color.Black : Color.White);
        if (IsLoaded) ApplyFrame();
    }

    /// <summary>Rounded corners and a theme-colored border, like Windows 11 flyouts.</summary>
    void ApplyFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var p = Theme.Current;
        int round = Native.DWMWCP_ROUND, dark = p.Dark ? 1 : 0, border = p.Border.R | (p.Border.G << 8) | (p.Border.B << 16);
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    // Preview ----------------------------------------------------------------------------------

    /// <summary>Renders a page to a bitmap (for --preview), with the settings page's first picker open.</summary>
    public BitmapSource Render(bool settingsPage)
    {
        ShowAt(new Rectangle(-20000, -20000, 1, 1), settingsPage, activate: false);
        if (settingsPage && _customize.Count > 0) _customize[0].PickerOpen = true;
        Left = -20000;
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle((System.Windows.Media.Brush)Resources["Bs.Background"], null, new Rect(0, 0, ActualWidth, ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        return bmp;
    }
}
