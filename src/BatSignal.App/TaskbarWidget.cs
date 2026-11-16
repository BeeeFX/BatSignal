using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatSignal.Core.Monitoring;
using static BatSignal.App.Native;

namespace BatSignal.App;

/// <summary>
/// A small per-pixel-alpha window placed inside the Windows taskbar (child of Shell_TrayWnd),
/// just left of the notification area. This is not an official extension point, so every
/// tick we check it is still attached and in place; callers fall back to tray icons when
/// <see cref="IsAttached"/> is false.
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    IntPtr _taskbar;
    IReadOnlyList<DeviceSnapshot> _items = [];
    int _threshold = 15;
    bool _hover;
    uint _dpi = 96;
    Rectangle _placed; // in taskbar client coordinates
    Size _size;

    public event Action? LeftClick;
    public event Action? RightClick;

    public bool IsAttached => Handle != IntPtr.Zero && IsWindow(_taskbar);

    /// <summary>Screen rectangle of the widget, for anchoring the flyout.</summary>
    public Rectangle ScreenBounds => GetWindowRect(Handle, out var r) ? r.ToRectangle() : Rectangle.Empty;

    public bool TryAttach()
    {
        if (IsAttached) return true;
        Detach();
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var tb) || tb.Width < tb.Height)
                return false; // no taskbar, or a vertical one: tray icons work better there

            _taskbar = taskbar;
            _dpi = GetDpiForWindow(taskbar);
            CreateHandle(new CreateParams
            {
                Caption = "BatSignal",
                Parent = taskbar,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
            });
            _placed = Rectangle.Empty;
            if (!Render())
            {
                Detach();
                return false;
            }
            Logger.Write("Taskbar widget attached");
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
        if (Handle != IntPtr.Zero) DestroyHandle();
        _taskbar = IntPtr.Zero;
        _hover = false;
    }

    public void SetItems(IReadOnlyList<DeviceSnapshot> items, int threshold)
    {
        _items = items;
        _threshold = threshold;
        if (IsAttached) Render();
    }

    /// <summary>Called every second: follow taskbar moves, DPI and theme changes, and stay on top of the XAML content.</summary>
    public void Tick()
    {
        if (!IsAttached) return;
        var dpi = GetDpiForWindow(_taskbar);
        if (dpi != _dpi)
        {
            _dpi = dpi;
            _placed = Rectangle.Empty;
        }
        if (_placed.IsEmpty || ComputePlacement(_size) != _placed)
            Render();
        else if (GetWindow(_taskbar, GW_CHILD) != Handle)
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Refresh()
    {
        if (IsAttached) Render();
    }

    float S => _dpi / 96f;

    Rectangle ComputePlacement(Size size)
    {
        GetWindowRect(_taskbar, out var tb);
        var notify = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = notify != IntPtr.Zero && GetWindowRect(notify, out var n) ? n.Left - tb.Left : tb.Width;
        int x = right - size.Width - (int)(4 * S);
        int y = (tb.Height - size.Height) / 2;
        return new Rectangle(x, y, size.Width, size.Height);
    }

    bool Render()
    {
        GetWindowRect(_taskbar, out var tb);
        using var bmp = Draw(S, (int)Math.Min(tb.Height - 8 * S, 40 * S));

        // Position, then push the pixels.
        var place = ComputePlacement(_size);
        if (place != _placed)
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
            var text = DeviceText.Percent(d);
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
            case WM_MOUSEMOVE when !_hover:
                _hover = true;
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = TME_LEAVE,
                    hwndTrack = Handle,
                };
                TrackMouseEvent(ref tme);
                Render();
                break;
            case WM_MOUSELEAVE:
                _hover = false;
                Render();
                break;
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                return;
            case WM_RBUTTONUP:
                RightClick?.Invoke();
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
