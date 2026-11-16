using System.ComponentModel;
using System.Runtime.CompilerServices;
using BatSignal.Core.Monitoring;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using FontWeight = System.Windows.FontWeight;
using FontWeights = System.Windows.FontWeights;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using Visibility = System.Windows.Visibility;

namespace BatSignal.App;

internal static class Marks
{
    public static readonly FontFamily IconFont = new($"{Theme.IconFont}, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
}

/// <summary>One row of the Devices page.</summary>
internal sealed class DeviceRowModel
{
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public required string Mark { get; init; }
    public required FontFamily MarkFont { get; init; }
    public double MarkSize { get; init; }
    public FontWeight MarkWeight { get; init; }
    public required Brush NameBrush { get; init; }
    public required Brush LevelBrush { get; init; }
    public required string PercentText { get; init; }
    public Visibility BoltVisibility { get; init; }
    public Visibility BarVisibility { get; init; }
    public GridLength Fill { get; init; }
    public GridLength Rest { get; init; }

    public static DeviceRowModel From(DeviceSnapshot d, Palette p, int threshold)
    {
        var pct = DeviceText.EffectivePercent(d);
        var level = pct is int v ? p.LevelColor(v, threshold) : p.Muted;
        if (!d.Online) level = Color.FromArgb(170, level);
        bool label = d.Label is not null;
        return new DeviceRowModel
        {
            Name = d.Name,
            Subtitle = $"{d.Connection} · {DeviceText.Status(d)}",
            Mark = d.Label ?? IconCatalog.Glyph(d),
            MarkFont = label ? Marks.UiFont : Marks.IconFont,
            MarkSize = label ? 11 : 16,
            MarkWeight = label ? FontWeights.SemiBold : FontWeights.Normal,
            NameBrush = Wpf.Brush(d.Online ? p.Text : p.Secondary),
            LevelBrush = Wpf.Brush(level),
            PercentText = DeviceText.Percent(d, showApprox: true),
            BoltVisibility = DeviceText.IsCharging(d) ? Visibility.Visible : Visibility.Collapsed,
            BarVisibility = pct is null ? Visibility.Collapsed : Visibility.Visible,
            Fill = new GridLength(Math.Clamp(pct ?? 0, 0, 100), GridUnitType.Star),
            Rest = new GridLength(100 - Math.Clamp(pct ?? 0, 0, 100), GridUnitType.Star),
        };
    }
}

/// <summary>One device card on the Settings page. Every change is written back immediately.</summary>
internal sealed class DeviceSettingsModel : INotifyPropertyChanged
{
    string _customName = "";
    string? _iconKey;
    bool _useLabel;
    string _label = "";
    bool _show = true;
    bool _pickerOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any user change, so the owner can save and refresh the widget.</summary>
    public event Action<DeviceSettingsModel>? Changed;

    public required string Id { get; init; }
    public required string OriginalName { get; init; }
    public required string Subtitle { get; init; }
    public required string DefaultIconKey { get; init; }
    public IReadOnlyList<IconChoiceModel> IconChoices { get; private set; } = [];

    public static DeviceSettingsModel From(DeviceSnapshot d, Settings settings)
    {
        settings.Devices.TryGetValue(d.Id, out var c);
        var m = new DeviceSettingsModel
        {
            Id = d.Id,
            OriginalName = d.OriginalName ?? d.Name,
            Subtitle = d.Connection,
            DefaultIconKey = IconCatalog.DefaultKey(d.Kind),
            _customName = c?.Name ?? "",
            _iconKey = c?.Icon,
            _label = c?.Label ?? "",
            _useLabel = !string.IsNullOrWhiteSpace(c?.Label),
            _show = !settings.HiddenDevices.Contains(d.Id),
        };
        m.IconChoices = IconCatalog.All.Select(e => new IconChoiceModel(m, e)).ToList();
        return m;
    }

    public string CustomName
    {
        get => _customName;
        set { if (Set(ref _customName, value)) { Notify(nameof(HintVisibility)); Changed?.Invoke(this); } }
    }

    public Visibility HintVisibility => _customName.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string EffectiveIconKey => _iconKey ?? DefaultIconKey;

    public string? IconKey
    {
        get => _iconKey;
        set
        {
            if (!Set(ref _iconKey, value == DefaultIconKey ? null : value)) return;
            foreach (var c in IconChoices) c.Refresh();
            NotifyMark();
            Changed?.Invoke(this);
        }
    }

    public bool UseLabel
    {
        get => _useLabel;
        set
        {
            if (!Set(ref _useLabel, value)) return;
            Notify(nameof(UseIcon));
            Notify(nameof(IconPanelVisibility));
            Notify(nameof(LabelPanelVisibility));
            NotifyMark();
            Changed?.Invoke(this);
        }
    }

    public bool UseIcon { get => !_useLabel; set => UseLabel = !value; }

    public string Label
    {
        get => _label;
        set { if (Set(ref _label, value.ToUpperInvariant())) { NotifyMark(); Changed?.Invoke(this); } }
    }

    /// <summary>The label to save: only when label mode is on and something was typed.</summary>
    public string? EffectiveLabel => _useLabel && !string.IsNullOrWhiteSpace(_label) ? _label.Trim() : null;

    public bool Show
    {
        get => _show;
        set { if (Set(ref _show, value)) Changed?.Invoke(this); }
    }

    public bool PickerOpen
    {
        get => _pickerOpen;
        set { if (Set(ref _pickerOpen, value)) Notify(nameof(PickerVisibility)); }
    }

    public Visibility PickerVisibility => _pickerOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconPanelVisibility => _useLabel ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LabelPanelVisibility => _useLabel ? Visibility.Visible : Visibility.Collapsed;

    public string Mark => EffectiveLabel ?? IconCatalog.Find(EffectiveIconKey)!.Glyph;
    public FontFamily MarkFont => EffectiveLabel is null ? Marks.IconFont : Marks.UiFont;
    public double MarkSize => EffectiveLabel is null ? 17 : 11;
    public FontWeight MarkWeight => EffectiveLabel is null ? FontWeights.Normal : FontWeights.SemiBold;

    public void Reset()
    {
        _customName = "";
        _iconKey = null;
        _useLabel = false;
        _label = "";
        foreach (var c in IconChoices) c.Refresh();
        Notify(null); // everything changed
        Changed?.Invoke(this);
    }

    void NotifyMark()
    {
        Notify(nameof(Mark));
        Notify(nameof(MarkFont));
        Notify(nameof(MarkSize));
        Notify(nameof(MarkWeight));
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class IconChoiceModel(DeviceSettingsModel owner, IconCatalog.Entry entry) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Glyph => entry.Glyph;
    public string Name => entry.Name;

    public bool IsSelected
    {
        get => owner.EffectiveIconKey == entry.Key;
        set { if (value) owner.IconKey = entry.Key; }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
}

/// <summary>System.Drawing → WPF conversions.</summary>
internal static class Wpf
{
    public static System.Windows.Media.Color Color(Color c) => System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

    public static System.Windows.Media.SolidColorBrush Brush(Color c)
    {
        var b = new System.Windows.Media.SolidColorBrush(Color(c));
        b.Freeze();
        return b;
    }
}
