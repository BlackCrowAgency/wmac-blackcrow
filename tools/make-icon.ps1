# Genera src\WMac\Assets\WMac.ico (multi-tamaño, PNG) con el logotipo de WMac.
param([string]$Out = (Join-Path $PSScriptRoot "..\src\WMac\Assets\WMac.ico"))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 100.0
    function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); return $p
    }
    $inset = [Math]::Max(0.5, 2 * $s)
    $body = RR $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) (22 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x5A, 0xC8, 0xFA)), ([System.Drawing.Color]::FromArgb(255, 0xBF, 0x5A, 0xF2))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 0x5A, 0xC8, 0xFA), [System.Drawing.Color]::FromArgb(255, 0x5E, 0x5C, 0xE6), [System.Drawing.Color]::FromArgb(255, 0xBF, 0x5A, 0xF2))
    $blend.Positions = @([float]0, [float]0.55, [float]1)
    $grad.InterpolationColors = $blend
    $g.FillPath($grad, $body)
    $sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, ($size * 0.6)), ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($sheen, $body)
    $tiles = @(@(25, 25, 255), @(54, 25, 217), @(25, 54, 217), @(54, 54, 166))
    foreach ($t in $tiles) {
        $tp = RR ($t[0] * $s) ($t[1] * $s) (21 * $s) (21 * $s) ([Math]::Max(1, 6 * $s))
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($t[2], 255, 255, 255))
        $g.FillPath($b, $tp)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($sz in $sizes) { , (New-Frame $sz) }
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
"Icono generado: $Out"
