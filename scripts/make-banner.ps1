# Draws docs/banner.png for the README: a spotlight beam with the battery "bat-signal",
# the name and tagline, and the real taskbar widget (docs/images/widget-real.png) on a taskbar.
# Usage: powershell -File scripts/make-banner.ps1   (run after --preview has refreshed docs/images)

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$W = 1280; $H = 400
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'

$night = [System.Drawing.Color]::FromArgb(255, 0x14, 0x1A, 0x2E)
$yellow = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xD2, 0x4A)
$g.Clear($night)

# A few stars
$rand = New-Object System.Random 7
for ($i = 0; $i -lt 70; $i++) {
    $a = $rand.Next(40, 140); $s = $rand.Next(1, 3)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, 255, 255, 255))), $rand.Next(0, $W), $rand.Next(0, $H - 70), $s, $s)
}

# Beam from the bottom left up to the spotlight
$cx = 1010; $cy = 150; $r = 92
$beam = New-Object System.Drawing.Drawing2D.GraphicsPath
$beam.AddPolygon([System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF 860, $H), (New-Object System.Drawing.PointF 905, $H),
    (New-Object System.Drawing.PointF ($cx + $r * 0.75), ($cy + $r * 0.55)), (New-Object System.Drawing.PointF ($cx - $r * 0.9), ($cy - $r * 0.25))))
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(38, $yellow))), $beam)

# Halo, spotlight, battery
foreach ($k in @(@(1.45, 18), @(1.22, 40))) {
    $halo = $r * $k[0]; $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($k[1], $yellow))), $cx - $halo, $cy - $halo, 2 * $halo, 2 * $halo)
}
$g.FillEllipse((New-Object System.Drawing.SolidBrush $yellow), $cx - $r, $cy - $r, 2 * $r, 2 * $r)
function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$rr) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $rr
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); $p
}
$bw = 104; $bh = 58; $bx = $cx - $bw / 2 - 5; $by = $cy - $bh / 2
$dark = New-Object System.Drawing.SolidBrush $night
$g.FillPath($dark, (RoundedRect $bx $by $bw $bh 9))
$g.FillRectangle($dark, $bx + $bw, $cy - 13, 11, 26)
$lit = New-Object System.Drawing.SolidBrush $yellow
for ($i = 0; $i -lt 2; $i++) { $g.FillRectangle($lit, $bx + 11 + $i * 30, $by + 11, 24, $bh - 22) }

# Name and tagline
$title = New-Object System.Drawing.Font 'Segoe UI Semibold', 76, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$tag = New-Object System.Drawing.Font 'Segoe UI', 27, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('BatSignal', $title, [System.Drawing.Brushes]::White, 84, 92)
$g.DrawString("Your mouse, keyboard and gamepad battery,`nright on the Windows taskbar.", $tag,
    (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xC9, 0xCE, 0xDB))), 90, 196)

# Taskbar strip with the real widget
$barTop = $H - 56
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x1C))), 0, $barTop, $W, 56)
$widget = [System.Drawing.Image]::FromFile((Join-Path $root 'docs/images/widget-real.png'))
$wx = $W - $widget.Width - 150; $wy = $barTop + [int]((56 - $widget.Height) / 2)
$g.DrawImage($widget, $wx, $wy, $widget.Width, $widget.Height)
$clock = New-Object System.Drawing.Font 'Segoe UI', 13, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$clockX = $W - 110; $clockY = $barTop + 9
$g.DrawString("16:20`n16/11/2026", $clock, [System.Drawing.Brushes]::White, $clockX, $clockY)
$widget.Dispose()

$g.Dispose()
$bmp.Save((Join-Path $root 'docs/banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host 'Wrote docs/banner.png'
