# Draws the BatSignal icon (a spotlight with a battery where the bat would be) and writes
# assets/icon.png (256 px) and assets/icon.ico (16-256 px, PNG-compressed entries).
# Usage: powershell -File scripts/make-icon.ps1

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $night = [System.Drawing.Color]::FromArgb(255, 0x14, 0x1A, 0x2E)
    $yellow = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xD2, 0x4A)

    # Night-sky tile
    $tile = RoundedRect 0 0 $s $s ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush $night), $tile)

    $cx = $s * 0.5; $cy = $s * 0.47; $r = $s * 0.355
    if ($s -ge 48) {
        # Faint beam from the bottom edge up to the spotlight
        $beam = New-Object System.Drawing.Drawing2D.GraphicsPath
        $beam.AddPolygon([System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($s * 0.44), $s),
            (New-Object System.Drawing.PointF ($s * 0.56), $s),
            (New-Object System.Drawing.PointF ($cx + $r * 0.8), $cy),
            (New-Object System.Drawing.PointF ($cx - $r * 0.8), $cy)))
        $g.SetClip($tile)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(45, $yellow))), $beam)
        # Soft halo
        $h = $r * 1.18
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, $yellow))), $cx - $h, $cy - $h, 2 * $h, 2 * $h)
        $g.ResetClip()
    }

    # Spotlight
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $yellow), $cx - $r, $cy - $r, 2 * $r, 2 * $r)

    # Battery silhouette
    $bw = $s * 0.40; $bh = $s * 0.22; $bx = $cx - $bw / 2 - $s * 0.02; $by = $cy - $bh / 2
    $dark = New-Object System.Drawing.SolidBrush $night
    $g.FillPath($dark, (RoundedRect $bx $by $bw $bh ($s * 0.035)))
    $g.FillRectangle($dark, $bx + $bw, $cy - $bh * 0.22, $s * 0.045, $bh * 0.44)
    if ($s -ge 32) {
        # Two of three charge bars lit
        $pad = $bh * 0.2; $gap = $bw * 0.06
        $barW = ($bw - 2 * $pad - 2 * $gap) / 3; $barH = $bh - 2 * $pad
        $lit = New-Object System.Drawing.SolidBrush $yellow
        for ($i = 0; $i -lt 2; $i++) { $g.FillRectangle($lit, $bx + $pad + $i * ($barW + $gap), $by + $pad, $barW, $barH) }
    }
    $g.Dispose(); return $bmp
}

# PNG for docs / notifications
$big = Draw 256
$big.Save((Join-Path $assets 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

# ICO: classic 32-bit bitmap entries (what every Windows API reads), PNG only for 256 px
function Dib([System.Drawing.Bitmap]$b) {
    $s = $b.Width; $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $px = $b.GetPixel($x, $y); $w.Write([byte]$px.B); $w.Write([byte]$px.G); $w.Write([byte]$px.R); $w.Write([byte]$px.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s))) # AND mask unused: alpha channel decides
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $b = Draw $s
    if ($s -ge 256) { $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bytes = $ms.ToArray() }
    else { $bytes = Dib $b }
    $b.Dispose(); ,$bytes
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $out.ToArray())
Write-Host "Wrote assets/icon.png and assets/icon.ico"
