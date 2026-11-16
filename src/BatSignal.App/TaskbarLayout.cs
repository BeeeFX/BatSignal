using System.Collections.Concurrent;
using System.Windows.Automation;

namespace BatSignal.App;

/// <summary>
/// Where things are on each taskbar, read through UI Automation every 2 seconds on a background
/// thread: the right edge of the app buttons (so the widget can step aside when the taskbar gets
/// crowded) and the left edge of the system tray (where the widget sits). All values are screen
/// pixels. The UI thread is input-attached to Explorer's taskbar, so it must never make these
/// cross-process calls itself.
/// </summary>
internal sealed class TaskbarLayout : IDisposable
{
    public readonly record struct Info(int ButtonsRight, int TrayLeft);

    readonly ConcurrentDictionary<IntPtr, Info> _info = new();
    readonly Thread _thread;
    volatile IntPtr[] _taskbars = [];
    volatile bool _stop;

    public TaskbarLayout()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskbarLayout" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void SetTaskbars(IEnumerable<IntPtr> taskbars) => _taskbars = taskbars.ToArray();

    public Info? Get(IntPtr taskbar) => _info.TryGetValue(taskbar, out var i) ? i : null;

    void Run()
    {
        while (!_stop)
        {
            foreach (var tb in _taskbars)
            {
                try { _info[tb] = Read(tb); }
                catch { _info.TryRemove(tb, out _); } // taskbar gone or busy; the widget falls back to window rects
            }
            foreach (var gone in _info.Keys.Except(_taskbars).ToList()) _info.TryRemove(gone, out _);
            Thread.Sleep(2000);
        }
    }

    static Info Read(IntPtr taskbar)
    {
        var root = AutomationElement.FromHandle(taskbar);
        double taskbarWidth = root.Current.BoundingRectangle.Width;
        int buttonsRight = -1, trayLeft = int.MaxValue;
        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var cls = e.Current.ClassName ?? "";
            var r = e.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0) continue;
            // Containers like Taskbar.TaskbarFrameAutomationPeer span the whole bar; only buttons count.
            if (r.Width > taskbarWidth * 0.3) continue;
            if (cls.StartsWith("Taskbar.", StringComparison.Ordinal))
                buttonsRight = Math.Max(buttonsRight, (int)r.Right);
            else if (cls.StartsWith("SystemTray.", StringComparison.Ordinal))
                trayLeft = Math.Min(trayLeft, (int)r.Left);
        }
        return new Info(buttonsRight, trayLeft == int.MaxValue ? -1 : trayLeft);
    }

    public void Dispose() => _stop = true;
}
