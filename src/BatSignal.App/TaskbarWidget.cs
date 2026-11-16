using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatSignal.Core.Monitoring;
using static BatSignal.App.Native;

namespace BatSignal.App;

/// <summary>
/// A small per-pixel-alpha window placed inside one Windows taskbar (child of Shell_TrayWnd or
/// Shell_SecondaryTrayWnd), just left of the tray or clock. This is not an official extension
/// point, so every tick we check it is still attached and in place. It hides itself when the
/// taskbar's app buttons would run under it (<see cref="IsBlocked"/>).
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    readonly TaskbarLayout? _layout;
    readonly WidgetTooltip? _tooltip;
    IReadOnlyList<DeviceSnapshot> _items = [];
    readonly List<(float X0, float X1, DeviceSnapshot Device)> _hits = [];
    int _threshold = 15;
    bool _hover;
    int _hoverIndex = -1;
    uint _dpi = 96;
    Rectangle _placed; // in taskbar client coordinates
    Size _size;
    long _lastAttempt = -10_000;

    public TaskbarWidget(IntPtr taskbar, bool primary, TaskbarLayout? layout, WidgetTooltip? tooltip)
    {
        Taskbar = taskbar;
        IsPrimary = primary;
        _layout = layout;
        _tooltip = tooltip;
    }

    public IntPtr Taskbar { get; }
    public bool IsPrimary { get; }

    public event Action<TaskbarWidget>? LeftClick;
    public event Action<TaskbarWidget>? RightClick;

    public bool IsAttached => Handle != IntPtr.Zero && IsWindow(Taskbar);

    /// <summary>Hidden because the taskbar's app buttons reach the widget's spot.</summary>
    public bool IsBlocked { get; private set; }

    public bool IsShowing => IsAttached && !IsBlocked;

    /// <summary>Screen rectangle of the widget, for anchoring the flyout.</summary>
    public Rectangle ScreenBounds => GetWindowRect(Handle, out var r) ? r.ToRectangle() : Rectangle.Empty;

    /// <summary>Creates the window inside the taskbar. Retries at most every 5 seconds.</summary>
    public bool TryAttach()
    {
        if (IsAttached) return true;
        if (Environment.TickCount64 - _lastAttempt < 5000) return false;
        _lastAttempt = Environment.TickCount64;
        Detach();
        try
        {
            if (!IsWindow(Taskbar) || !GetWindowRect(Taskbar, out var tb) || tb.Width < tb.Height)
                return false; // gone, or a vertical taskbar: tray icons work better there

            _dpi = GetDpiForWindow(Taskbar);
            CreateHandle(new CreateParams
            {
                Caption = "BatSignal",
                Parent = Taskbar,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
            });
            _placed = Rectangle.Empty;
            if (!Render())
            {
                Detach();
                return false;
            }
            Logger.Write($"Taskbar widget attached ({(IsPrimary ? "main" : "secondary")} taskbar)");
            return true;
        }
        catch (Exception e)
        {
            Logger.Write($"Taskbar widget attach failed: {e.Message}");
            Detach();
            return false;
        }
    }

    public void Detach()
    {
        _tooltip?.HideTip();
        if (Handle != IntPtr.Zero) DestroyHandle();
        _hover = false;
        _hoverIndex = -1;
    }

    public void SetItems(IReadOnlyList<DeviceSnapshot> items, int threshold)
    {
        _items = items;
        _threshold = threshold;
        if (IsAttached) Render();
    }

    /// <summary>Called every second: follow taskbar changes, DPI and theme, and stay above the XAML content.</summary>
    public void Tick()
    {
        if (!IsAttached) return;
        var dpi = GetDpiForWindow(Taskbar);
        if (dpi != _dpi)
        {
            _dpi = dpi;
            _placed = Rectangle.Empty;
        }
        var (place, blocked) = ComputePlacement(_size);
        if (_placed.IsEmpty || place != _placed || blocked != IsBlocked)
            Render();
        else if (!IsBlocked && GetWindow(Taskbar, GW_CHILD) != Handle)
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Refresh()
    {
        if (IsAttached) Render();
    }

    float S => _dpi / 96f;

    (Rectangle Place, bool Blocked) ComputePlacement(Size size)
    {
        GetWindowRect(Taskbar, out var tb);
        var info = _layout?.Get(Taskbar);

        // Right edge: the tray as seen by UI Automation, else the TrayNotifyWnd window (main
        // taskbar), else a clock-sized margin (secondary taskbars).
        int trayLeft = info is { TrayLeft: > 0 } i && i.TrayLeft > tb.Left ? i.TrayLeft : -1;
        if (trayLeft < 0)
        {
            var notify = FindWindowEx(Taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            trayLeft = notify != IntPtr.Zero && GetWindowRect(notify, out var n) ? n.Left : tb.Right - (int)(200 * S);
        }

        int x = trayLeft - tb.Left - size.Width - (int)(4 * S);
        int y = (tb.Height - size.Height) / 2;
        bool blocked = info is { ButtonsRight: > 0 } b && b.ButtonsRight + (int)(8 * S) > tb.Left + x;
        return (new Rectangle(x, y, size.Width, size.Height), blocked);
    }

    bool Render()
    {
        GetWindowRect(Taskbar, out var tb);
        using var bmp = Draw(S, (int)Math.Min(tb.Height - 8 * S, 40 * S));

        var (place, blocked) = ComputePlacement(_size);
        if (blocked != IsBlocked)
            Logger.Write(blocked ? "Taskbar is crowded: widget steps aside" : "Taskbar has room again: widget back");
        IsBlocked = blocked;
        if (blocked)
        {
            _tooltip?.HideTip();
            SetWindowPos(Handle, HWND_TOP, place.X, place.Y, place.Width, place.Height, SWP_NOACTIVATE | SWP_HIDEWINDOW);
            _placed = place;
            return Push(bmp);
        }
        if (place != _placed || !IsWindowVisible(Handle))
        {
            SetWindowPos(Handle, HWND_TOP, place.X, place.Y, place.Width, place.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _placed = place;
        }
        return Push(bmp);
    }

    /// <summary>Draws the widget contents. Public so --preview can save it as an image.</summary>
    public Bitmap Draw(float s, int height, bool hover = false)
    {
        var palette = Theme.Current;
        hover |= _hover;
        int pad = (int)(10 * s), gap = (int)(14 * s), iconGap = (int)(5 * s);

        using var iconFont = new Font(Theme.IconFont, 15 * s, GraphicsUnit.Pixel);
        using var textFont = new Font(Theme.UiSemibold, 13 * s, GraphicsUnit.Pixel);
        using var boltFont = new Font(Theme.IconFont, 11 * s, GraphicsUnit.Pixel);
        using var labelFont = new Font(Theme.UiSemibold, 10.5f * s, GraphicsUnit.Pixel);
        float labelPad = 3 * s;
        var fmt = StringFormat.GenericTypographic;

        // Measure
        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var layout = new List<(DeviceSnapshot d, string text, float iconW, float textW, float boltW)>();
        foreach (var d in _items)
        {
            // An estimated charging level gets its "~" on the taskbar too.
            var text = DeviceText.Percent(d, showApprox: d.Battery is { IsEstimate: true, State: BatSignal.Core.Hidpp.ChargeState.Charging });
            float iw = d.Label is { } label
                ? mg.MeasureString(label, labelFont, 1000, fmt).Width + 2 * labelPad
                : mg.MeasureString(IconCatalog.Glyph(d), iconFont, 1000, fmt).Width;
            float tw = mg.MeasureString(text, textFont, 1000, fmt).Width;
            float bw = DeviceText.IsCharging(d) ? mg.MeasureString(Theme.GlyphCharging, boltFont, 1000, fmt).Width + 2 * s : 0;
            layout.Add((d, text, iw, tw, bw));
        }
        string empty = "No devices";
        float width = layout.Count == 0
            ? mg.MeasureString(empty, textFont, 1000, fmt).Width + 2 * pad
            : layout.Sum(l => l.iconW + iconGap + l.textW + l.boltW) + gap * (layout.Count - 1) + 2 * pad;
        _size = new Size((int)Math.Ceiling(width), height);

        // Draw
        _hits.Clear();
        var bmp = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // Alpha 1 everywhere so the whole rectangle receives mouse clicks while looking transparent.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            if (hover)
            {
                using var hb = new SolidBrush(palette.Hover);
                using var path = RoundedRect(new RectangleF(0, 0, _size.Width, _size.Height), 5 * s);
                g.FillPath(hb, path);
            }

            float x = pad, cy = _size.Height / 2f;
            if (layout.Count == 0)
            {
                using var b = new SolidBrush(palette.Secondary);
                DrawCentered(g, empty, textFont, b, x, cy, fmt);
            }
            foreach (var (d, text, iw, tw, bw) in layout)
            {
                float start = x;
                int alpha = d.Online ? 255 : 150; // asleep: last reading, dimmed
                var pct = DeviceText.EffectivePercent(d);
                var color = pct is int p ? palette.LevelColor(p, _threshold) : palette.Secondary;
                using var iconBrush = new SolidBrush(Color.FromArgb(alpha, palette.Text));
                using var textBrush = new SolidBrush(Color.FromArgb(alpha, color));
                if (d.Label is { } label)
                {
                    // Text label in a small rounded outline, where the icon would be.
                    var box = new RectangleF(x, cy - 8 * s, iw, 16 * s);
                    using (var pen = new Pen(Color.FromArgb(alpha * 2 / 3, palette.Text), 1 * s))
                    using (var path = RoundedRect(box, 3 * s))
                        g.DrawPath(pen, path);
                    DrawCentered(g, label, labelFont, iconBrush, x + labelPad, cy, fmt);
                }
                else
                {
                    DrawCentered(g, IconCatalog.Glyph(d), iconFont, iconBrush, x, cy, fmt);
                }
                x += iw + iconGap;
                DrawCentered(g, text, textFont, textBrush, x, cy, fmt);
                x += tw;
                if (bw > 0)
                {
                    DrawCentered(g, Theme.GlyphCharging, boltFont, textBrush, x + 2 * s, cy, fmt);
                    x += bw;
                }
                _hits.Add((start - gap / 2f, x + gap / 2f, d));
                x += gap;
            }
        }
        return bmp;
    }

    static void DrawCentered(Graphics g, string text, Font font, Brush brush, float x, float centerY, StringFormat fmt)
    {
        var size = g.MeasureString(text, font, 1000, fmt);
        g.DrawString(text, font, brush, x, centerY - size.Height / 2f, fmt);
    }

    bool Push(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            bool ok = UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            if (!ok) Logger.Write($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return ok;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void OnHoverMove(int x)
    {
        int index = _hits.FindIndex(h => x >= h.X0 && x < h.X1);
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        if (index < 0 || _tooltip is null)
        {
            _tooltip?.HideTip();
            return;
        }
        var (x0, x1, d) = _hits[index];
        var bounds = ScreenBounds;
        var anchor = new Rectangle(bounds.Left + (int)x0, bounds.Top, (int)(x1 - x0), bounds.Height);
        _tooltip.Schedule(anchor, d.Name, $"{DeviceText.Percent(d, showApprox: true)} · {DeviceText.Status(d)}");
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_SETCURSOR:
                Cursor.Current = Cursors.Default;
                m.Result = 1;
                return;
            case WM_MOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = Handle,
                    };
                    TrackMouseEvent(ref tme);
                    Render();
                }
                OnHoverMove((short)(m.LParam.ToInt64() & 0xFFFF));
                break;
            case WM_MOUSELEAVE:
                _hover = false;
                _hoverIndex = -1;
                _tooltip?.HideTip();
                Render();
                break;
            case WM_LBUTTONUP:
                _tooltip?.HideTip();
                LeftClick?.Invoke(this);
                return;
            case WM_RBUTTONUP:
                _tooltip?.HideTip();
                RightClick?.Invoke(this);
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleChange()
    {
        base.OnHandleChange();
        if (Handle == IntPtr.Zero) _hover = false; // destroyed, e.g. Explorer restarted
    }

    public void Dispose() => Detach();
}
