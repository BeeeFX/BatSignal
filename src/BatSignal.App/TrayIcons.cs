using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

/// <summary>Fallback view: one notification-area icon per device, the number drawn inside a device outline.</summary>
internal sealed class TrayIcons : IDisposable
{
    const string PlaceholderId = "__placeholder";
    readonly Dictionary<string, (NotifyIcon Icon, IntPtr HIcon)> _icons = new();

    public event Action<Point>? LeftClick;
    public event Action<Point>? RightClick;

    public bool IsShown => _icons.Count > 0;

    public NotifyIcon? IconFor(string id) => _icons.TryGetValue(id, out var e) ? e.Icon : null;

    public void Update(IReadOnlyList<DeviceSnapshot> devices, int threshold)
    {
        var palette = Theme.Current;
        var wanted = devices.Count > 0 ? devices.Select(d => d.Id).ToHashSet() : [PlaceholderId];

        foreach (var id in _icons.Keys.Where(id => !wanted.Contains(id)).ToList())
            Remove(id);

        if (devices.Count == 0)
        {
            Set(PlaceholderId, Render(DeviceKind.Unknown, "–", palette.Secondary, true), "BatSignal — no devices found");
            return;
        }
        foreach (var d in devices)
        {
            var pct = DeviceText.EffectivePercent(d);
            string text = d.Battery?.Percent is int p ? (p >= 100 ? "F" : p.ToString()) : pct is null ? "–" : DeviceText.Percent(d)[..1];
            var color = pct is int v ? palette.LevelColor(v, threshold) : palette.Secondary;
            Set(d.Id, Render(IconCatalog.Shape(d), text, color, d.Online), DeviceText.Tooltip(d));
        }
    }

    void Set(string id, Bitmap bmp, string tooltip)
    {
        IntPtr hIcon;
        using (bmp) hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);

        if (_icons.TryGetValue(id, out var existing))
        {
            existing.Icon.Icon = icon;
            existing.Icon.Text = tooltip;
            Native.DestroyIcon(existing.HIcon);
            _icons[id] = (existing.Icon, hIcon);
            return;
        }
        var ni = new NotifyIcon { Icon = icon, Text = tooltip, Visible = true };
        ni.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) LeftClick?.Invoke(Cursor.Position);
            else if (e.Button == MouseButtons.Right) RightClick?.Invoke(Cursor.Position);
        };
        _icons[id] = (ni, hIcon);
    }

    void Remove(string id)
    {
        if (!_icons.Remove(id, out var e)) return;
        e.Icon.Visible = false;
        e.Icon.Dispose();
        Native.DestroyIcon(e.HIcon);
    }

    public void Clear()
    {
        foreach (var id in _icons.Keys.ToList()) Remove(id);
    }

    public void Dispose() => Clear();

    /// <summary>Draws the A1 icon: tall rounded outline = mouse, wide box = keyboard, number inside.</summary>
    public static Bitmap Render(DeviceKind kind, string text, Color numberColor, bool online)
    {
        int size = SystemInformation.SmallIconSize.Width;
        float k = size / 16f;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(k, k);

        int alpha = online ? 255 : 140;
        using var outline = new Pen(Color.FromArgb(alpha, 0x88, 0x87, 0x80), 1.1f);
        RectangleF textBox;
        switch (kind)
        {
            case DeviceKind.Mouse or DeviceKind.Trackball or DeviceKind.Touchpad:
                using (var p = Rounded(new RectangleF(3, 0.8f, 10, 14.4f), 4.7f)) g.DrawPath(outline, p);
                g.DrawLine(outline, 8, 1, 8, 4);
                textBox = new RectangleF(0, 5, 16, 9);
                break;
            case DeviceKind.Keyboard or DeviceKind.Numpad or DeviceKind.Gamepad:
                using (var p = Rounded(new RectangleF(0.6f, 2.6f, 14.8f, 10.8f), kind == DeviceKind.Gamepad ? 4 : 1.6f)) g.DrawPath(outline, p);
                textBox = new RectangleF(0, 3, 16, 10);
                break;
            default:
                g.DrawEllipse(outline, 0.6f, 0.6f, 14.8f, 14.8f);
                textBox = new RectangleF(0, 2, 16, 12);
                break;
        }

        float fontPx = text.Length > 1 ? (kind == DeviceKind.Mouse ? 7.6f : 8.6f) : 9.5f;
        using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(alpha, numberColor));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, textBox, fmt);
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
