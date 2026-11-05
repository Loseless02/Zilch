# Draws Zilch's icon at every Windows size and packs them into src/Zilch/zilch.ico.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File assets/make-icon.ps1
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square, violet to cyan
    $m = [Math]::Max(1, [int]($size * 0.04))
    $r = [int]($size * 0.22)
    $rect = New-Object System.Drawing.Rectangle $m, $m, ($size - 2 * $m), ($size - 2 * $m)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(109, 40, 217)), ([System.Drawing.Color]::FromArgb(14, 165, 233)), 45.0
    $g.FillPath($brush, $path)

    # white Z, thicker at small sizes so it stays readable
    $w = if ($size -le 20) { $size * 0.17 } elseif ($size -le 32) { $size * 0.14 } else { $size * 0.12 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $a = $size * 0.30; $b = $size * 0.70
    $pts = @(
        (New-Object System.Drawing.PointF $a, $a),
        (New-Object System.Drawing.PointF $b, $a),
        (New-Object System.Drawing.PointF $a, $b),
        (New-Object System.Drawing.PointF $b, $b)
    )
    $g.DrawLines($pen, $pts)

    # small orange dot: the "nothing left" spark
    if ($size -ge 24) {
        $dot = $size * 0.13
        $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(251, 146, 60))
        $g.FillEllipse($orange, $size * 0.74, $size * 0.14, $dot, $dot)
    }
    $g.Dispose()
    return $bmp
}

# 64 px and up are stored as PNG, the small sizes as classic 32-bit bitmaps (every Windows API reads those)
function Dib($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))   # all-zero AND mask: alpha channel decides
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($s -ge 64) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else { $data = Dib $bmp }
    $bmp.Dispose()
    , $data
}

$out = Join-Path $PSScriptRoot "..\src\Zilch\zilch.ico"
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "wrote $out"
