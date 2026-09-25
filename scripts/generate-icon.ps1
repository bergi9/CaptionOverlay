# Draws the app icon (rounded caption box with two text bars) and writes a multi-size .ico.
# Usage: powershell -NoProfile -File scripts/generate-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\CaptionOverlay.App\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(1, $s * 0.06)
    $w = $s - 2 * $m
    $h = $s * 0.72
    $y = ($s - $h) / 2
    $r = $s * 0.18
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 36, 48))
    $g.FillPath($bg, $path)

    $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 196, 0))
    $bh = [Math]::Max(1.5, $s * 0.11)
    $x0 = $m + $s * 0.14
    $g.FillRectangle($bar, [single]$x0, [single]($y + $h * 0.30), [single]($w * 0.72), [single]$bh)
    $g.FillRectangle($accent, [single]$x0, [single]($y + $h * 0.58), [single]($w * 0.48), [single]$bh)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$pngs[$i].Length); $bw.Write([int]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
Write-Host "wrote $out"
