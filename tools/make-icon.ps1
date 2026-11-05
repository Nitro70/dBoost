# Draws the dBoost icon with WPF and writes src/dBoost/Assets/dBoost.ico (PNG frames) plus a 256 px PNG.
# Run with Windows PowerShell 5.1:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$assets = Join-Path $PSScriptRoot '..\src\dBoost\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconFrame([int]$size) {
    $s = $size / 256.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    # Rounded square with a blue to violet gradient.
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point 0, 0
    $grad.EndPoint = New-Object System.Windows.Point 1, 1
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x3D, 0x8B, 0xFF)), 0))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x7B, 0x3F, 0xF2)), 1))
    $pad = 12 * $s
    $rect = New-Object System.Windows.Rect $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $dc.DrawRoundedRectangle($grad, $null, $rect, 54 * $s, 54 * $s)

    $white = [System.Windows.Media.Brushes]::White

    # Speaker: a box and a cone.
    $geo = New-Object System.Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $ctx.BeginFigure((New-Object System.Windows.Point (54 * $s), (104 * $s)), $true, $true)
    $pts = @(
        (New-Object System.Windows.Point (88 * $s), (104 * $s)),
        (New-Object System.Windows.Point (132 * $s), (66 * $s)),
        (New-Object System.Windows.Point (132 * $s), (190 * $s)),
        (New-Object System.Windows.Point (88 * $s), (152 * $s)),
        (New-Object System.Windows.Point (54 * $s), (152 * $s))
    )
    foreach ($p in $pts) { $ctx.LineTo($p, $true, $true) }
    $ctx.Close()
    $geo.Freeze()
    $dc.DrawGeometry($white, $null, $geo)

    # Sound waves (fewer at small sizes so they stay readable).
    $pen = New-Object System.Windows.Media.Pen $white, ([Math]::Max(1.5, 15 * $s))
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $radii = if ($size -le 20) { @(40) } else { @(36, 66) }
    foreach ($r in $radii) {
        $a = 48 * [Math]::PI / 180
        $cx = 138 * $s; $cy = 128 * $s; $rr = $r * $s
        $p1 = New-Object System.Windows.Point ($cx + $rr * [Math]::Cos(-$a)), ($cy + $rr * [Math]::Sin(-$a))
        $p2 = New-Object System.Windows.Point ($cx + $rr * [Math]::Cos($a)), ($cy + $rr * [Math]::Sin($a))
        $arc = New-Object System.Windows.Media.StreamGeometry
        $c2 = $arc.Open()
        $c2.BeginFigure($p1, $false, $false)
        $c2.ArcTo($p2, (New-Object System.Windows.Size $rr, $rr), 0, $false, 'Clockwise', $true, $true)
        $c2.Close()
        $arc.Freeze()
        $dc.DrawGeometry($null, $pen, $arc)
    }
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($sz in $sizes) { $frames[$sz] = New-IconFrame $sz }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'dBoost-256.png'), $frames[256])

# ICO container: header, one directory entry per size, then the PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $data = $frames[$sz]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($sz in $sizes) { $w.Write($frames[$sz]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'dBoost.ico'), $out.ToArray())
Write-Host "Wrote dBoost.ico and dBoost-256.png to $assets"
