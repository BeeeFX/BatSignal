namespace BatSignal.App;

/// <summary>
/// The hover tip for a device on the taskbar widget: name on the first line, level and status
/// on the second, in the panel's style. Shown after a short delay, never takes focus, and lets
/// clicks through.
/// </summary>
internal sealed class WidgetTooltip : Form
{
    readonly System.Windows.Forms.Timer _delay = new() { Interval = 400 };
    string _title = "", _body = "";
    Rectangle _anchor;

    public WidgetTooltip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        _delay.Tick += (_, _) => { _delay.Stop(); Present(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */ | 0x8 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>Shows the tip above <paramref name="anchor"/> (screen pixels) after the delay; replaces any pending one.</summary>
    public void Schedule(Rectangle anchor, string title, string body)
    {
        bool same = Visible && anchor == _anchor && title == _title && body == _body;
        _anchor = anchor;
        _title = title;
        _body = body;
        if (same) return;
        if (Visible) Present(); // already showing: move straight to the new device
        else { _delay.Stop(); _delay.Start(); }
    }

    public void HideTip()
    {
        _delay.Stop();
        if (Visible) Hide();
    }

    float S => Native.GetDpiForWindow(Handle) / 96f;

    void Present()
    {
        if (!IsHandleCreated) CreateHandle();
        var p = Theme.Current;
        int border = p.Border.R | (p.Border.G << 8) | (p.Border.B << 16);
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        float s = S;
        using var titleFont = new Font(Theme.UiSemibold, 13 * s, GraphicsUnit.Pixel);
        using var bodyFont = new Font(Theme.UiFont, 12 * s, GraphicsUnit.Pixel);
        var t = TextRenderer.MeasureText(_title, titleFont);
        var b = TextRenderer.MeasureText(_body, bodyFont);
        int pad = (int)(10 * s);
        var size = new Size(Math.Max(t.Width, b.Width) + 2 * pad, t.Height + b.Height + 2 * pad - (int)(2 * s));

        var screen = Screen.FromRectangle(_anchor).Bounds;
        int x = Math.Clamp(_anchor.Left + _anchor.Width / 2 - size.Width / 2, screen.Left + pad, screen.Right - size.Width - pad);
        int y = _anchor.Top - size.Height - (int)(8 * s);
        Bounds = new Rectangle(x, y, size.Width, size.Height);
        Invalidate();
        if (!Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        float s = S;
        e.Graphics.Clear(p.Background);
        using var titleFont = new Font(Theme.UiSemibold, 13 * s, GraphicsUnit.Pixel);
        using var bodyFont = new Font(Theme.UiFont, 12 * s, GraphicsUnit.Pixel);
        int pad = (int)(10 * s);
        var t = TextRenderer.MeasureText(_title, titleFont);
        TextRenderer.DrawText(e.Graphics, _title, titleFont, new Point(pad, pad - (int)(1 * s)), p.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _body, bodyFont, new Point(pad, pad + t.Height - (int)(3 * s)), p.Secondary, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _delay.Dispose();
        base.Dispose(disposing);
    }
}
