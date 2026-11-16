using System.Drawing.Drawing2D;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>The panel that opens on left-click: every device with its bar, percentage and status.</summary>
internal sealed class FlyoutForm : Form
{
    IReadOnlyList<DeviceSnapshot> _devices = [];
    int _threshold;
    DateTimeOffset? _updated;
    Rectangle _refreshButton, _settingsButton;
    long _hiddenAt;

    public event Action? RefreshClicked;
    public event Action<Point>? SettingsClicked;

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Text = "BatSignal";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ApplyDarkFrame();
    }

    void ApplyDarkFrame()
    {
        int dark = Theme.Current.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>True right after the flyout closed because the user clicked the widget; that click should not reopen it.</summary>
    public bool JustClosed => Environment.TickCount64 - _hiddenAt < 300;

    public void SetData(IReadOnlyList<DeviceSnapshot> devices, int threshold, DateTimeOffset? updated)
    {
        _devices = devices;
        _threshold = threshold;
        _updated = updated;
        if (Visible)
        {
            Size = Measure();
            Invalidate();
        }
    }

    float S => DeviceDpi / 96f;
    int P(float v) => (int)Math.Round(v * S);

    Size Measure() => new(P(360), P(16 + 34) + Math.Max(_devices.Count, 1) * P(62) + P(48));

    /// <summary>Renders the flyout off-screen (for --preview).</summary>
    public Bitmap Snapshot()
    {
        if (!IsHandleCreated) CreateHandle();
        Size = Measure();
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }

    /// <summary>Opens above the anchor (widget or tray icon), right-aligned to it, inside the work area.</summary>
    public void ShowAt(Rectangle anchor)
    {
        var screen = Screen.FromRectangle(anchor);
        if (!IsHandleCreated) CreateHandle();
        ApplyDarkFrame();
        Size = Measure();
        var wa = screen.WorkingArea;
        int x = Math.Clamp(anchor.Right - Width, wa.Left + P(12), wa.Right - Width - P(12));
        int y = wa.Bottom - Height - P(12);
        Location = new Point(x, y);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
        _hiddenAt = Environment.TickCount64;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_refreshButton.Contains(e.Location)) RefreshClicked?.Invoke();
        else if (_settingsButton.Contains(e.Location)) SettingsClicked?.Invoke(PointToScreen(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(p.Background);

        using var titleFont = new Font(Theme.UiSemibold, 14 * S, GraphicsUnit.Pixel);
        using var nameFont = new Font(Theme.UiFont, 14 * S, GraphicsUnit.Pixel);
        using var smallFont = new Font(Theme.UiFont, 12 * S, GraphicsUnit.Pixel);
        using var pctFont = new Font(Theme.UiSemibold, 15 * S, GraphicsUnit.Pixel);
        using var iconFont = new Font(Theme.IconFont, 16 * S, GraphicsUnit.Pixel);
        using var buttonFont = new Font(Theme.IconFont, 14 * S, GraphicsUnit.Pixel);
        using var line = new Pen(p.Border, 1);
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        int pad = P(16), w = ClientSize.Width;

        // Header
        var header = new Rectangle(pad, pad, w - 2 * pad, P(30));
        TextRenderer.DrawText(g, "Peripherals", titleFont, header, p.Text, left);
        if (_updated is { } u)
            TextRenderer.DrawText(g, $"Updated {u:HH:mm}", smallFont, header, p.Muted, right);

        int y = pad + P(34);
        if (_devices.Count == 0)
        {
            g.DrawLine(line, pad, y, w - pad, y);
            TextRenderer.DrawText(g, "No Logitech devices found yet. Plug in a receiver or wake a device.",
                smallFont, new Rectangle(pad, y, w - 2 * pad, P(62)), p.Secondary, left | TextFormatFlags.WordBreak);
            y += P(62);
        }

        foreach (var d in _devices)
        {
            g.DrawLine(line, pad, y, w - pad, y);
            var text = d.Online ? p.Text : p.Secondary;

            var box = new Rectangle(pad, y + P(15), P(32), P(32));
            using (var surface = new SolidBrush(p.Surface))
            using (var path = Rounded(box, P(8)))
                g.FillPath(surface, path);
            TextRenderer.DrawText(g, Theme.Glyph(d.Kind), iconFont, box, p.Secondary, center);

            int tx = pad + P(44), pctW = P(56), tw = w - pad - pctW - tx;
            TextRenderer.DrawText(g, d.Name, nameFont, new Rectangle(tx, y + P(8), tw, P(20)), text, left);
            TextRenderer.DrawText(g, $"{d.Connection} · {DeviceText.Status(d)}", smallFont,
                new Rectangle(tx, y + P(27), tw, P(17)), p.Muted, left);

            var pct = DeviceText.EffectivePercent(d);
            var color = pct is int v ? p.LevelColor(v, _threshold) : p.Muted;
            if (!d.Online) color = Color.FromArgb(160, color);
            if (pct is int barPct)
            {
                var track = new Rectangle(tx, y + P(48), tw, P(6));
                using var trackBrush = new SolidBrush(p.Track);
                using var fillBrush = new SolidBrush(color);
                using (var tp = Rounded(track, P(3))) g.FillPath(trackBrush, tp);
                var fill = track with { Width = Math.Max(P(6), track.Width * Math.Clamp(barPct, 0, 100) / 100) };
                using (var fp = Rounded(fill, P(3))) g.FillPath(fillBrush, fp);
            }
            var pctText = DeviceText.Percent(d, showApprox: true);
            var pctRect = new Rectangle(w - pad - pctW, y, pctW, P(62));
            if (DeviceText.IsCharging(d))
            {
                // Number in UI font, bolt in icon font, right-aligned together.
                var bolt = TextRenderer.MeasureText(g, Theme.GlyphCharging, buttonFont, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Theme.GlyphCharging, buttonFont, pctRect, color, right);
                TextRenderer.DrawText(g, pctText, pctFont,
                    pctRect with { Width = pctRect.Width - bolt.Width - P(2) }, color, right);
            }
            else
            {
                TextRenderer.DrawText(g, pctText, pctFont, pctRect, color, right);
            }
            y += P(62);
        }

        // Footer
        g.DrawLine(line, pad, y, w - pad, y);
        var footer = new Rectangle(pad, y + P(8), w - 2 * pad, P(30));
        TextRenderer.DrawText(g, _threshold > 0 ? $"Alert below {_threshold}%" : "Low-battery alerts off",
            smallFont, footer, p.Secondary, left);
        _settingsButton = new Rectangle(w - pad - P(30), footer.Y, P(30), P(30));
        _refreshButton = _settingsButton with { X = _settingsButton.X - P(34) };
        foreach (var (rect, glyph) in new[] { (_refreshButton, Theme.GlyphRefresh), (_settingsButton, Theme.GlyphSettings) })
        {
            using (var b = new SolidBrush(p.Surface))
            using (var path = Rounded(rect, P(5)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, glyph, buttonFont, rect, p.Text, center);
        }
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
