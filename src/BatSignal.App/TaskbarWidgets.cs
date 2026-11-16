using BatSignal.Core.Monitoring;
using static BatSignal.App.Native;

namespace BatSignal.App;

/// <summary>
/// One <see cref="TaskbarWidget"/> per taskbar: the main one, plus each secondary-monitor
/// taskbar when Windows shows the taskbar on all displays.
/// </summary>
internal sealed class TaskbarWidgets : IDisposable
{
    readonly TaskbarLayout _layout = new();
    readonly WidgetTooltip _tooltip = new();
    readonly Dictionary<IntPtr, TaskbarWidget> _widgets = new();
    IReadOnlyList<DeviceSnapshot> _items = [];
    int _threshold = 15;

    public event Action<Rectangle>? LeftClick;
    public event Action<Rectangle>? RightClick;

    /// <summary>True when the main taskbar's widget is attached and not stepping aside.</summary>
    public bool PrimaryShowing => _widgets.Values.Any(w => w.IsPrimary && w.IsShowing);

    public void SetItems(IReadOnlyList<DeviceSnapshot> items, int threshold)
    {
        _items = items;
        _threshold = threshold;
        foreach (var w in _widgets.Values) w.SetItems(items, threshold);
    }

    /// <summary>Called every second: follow taskbars appearing and disappearing, attach, and tick.</summary>
    public void Sync()
    {
        var bars = new List<(IntPtr Handle, bool Primary)>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) bars.Add((main, true));
        for (var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            if (IsWindowVisible(h)) bars.Add((h, false));

        foreach (var gone in _widgets.Keys.Where(k => bars.All(b => b.Handle != k)).ToList())
        {
            _widgets[gone].Dispose();
            _widgets.Remove(gone);
        }
        foreach (var (handle, primary) in bars)
        {
            if (!_widgets.TryGetValue(handle, out var w))
            {
                w = new TaskbarWidget(handle, primary, _layout, _tooltip);
                w.LeftClick += x => LeftClick?.Invoke(x.ScreenBounds);
                w.RightClick += x => RightClick?.Invoke(x.ScreenBounds);
                w.SetItems(_items, _threshold);
                _widgets[handle] = w;
            }
            if (w.IsAttached) w.Tick(); else w.TryAttach();
        }
        _layout.SetTaskbars(bars.Select(b => b.Handle));
    }

    public void DetachAll()
    {
        foreach (var w in _widgets.Values) w.Dispose();
        _widgets.Clear();
        _tooltip.HideTip();
    }

    public void Dispose()
    {
        DetachAll();
        _layout.Dispose();
        _tooltip.Dispose();
    }
}
