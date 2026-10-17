<#
Generates src/DofusCompanion.App/Assets/app.ico: an ORIGINAL black dragon egg ("dofus") with gold speckles and
a thin gold outline (so it stays visible on a dark taskbar). Drawn from code, no game asset is used.
Usage: .\scripts\generate-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path $PSScriptRoot '..\src\DofusCompanion.App\Assets\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-EggPath([float]$s) {
    # Egg: wide rounded bottom, narrower top, built from two Bezier halves.
    $cx = $s / 2; $top = $s * 0.04; $bottom = $s * 0.97; $w = $s * 0.37; $mid = $s * 0.60
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier($cx, $top, $cx + $w * 0.58, $top, $cx + $w, $s * 0.36, $cx + $w, $mid)
    $p.AddBezier($cx + $w, $mid, $cx + $w, $bottom, $cx - $w, $bottom, $cx - $w, $mid)
    $p.AddBezier($cx - $w, $mid, $cx - $w, $s * 0.36, $cx - $w * 0.58, $top, $cx, $top)
    $p.CloseFigure()
    return $p
}

$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $egg = New-EggPath $s

    # Black shell with a subtle radial sheen.
    $body = New-Object System.Drawing.Drawing2D.PathGradientBrush $egg
    $body.CenterPoint = New-Object System.Drawing.PointF ($s * 0.40), ($s * 0.38)
    $body.CenterColor = [System.Drawing.Color]::FromArgb(255, 58, 58, 64)
    $body.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 6, 6, 8))
    $g.FillPath($body, $egg)

    # Gold speckles (classic dofus look); skipped on the tiniest sizes where they would be noise.
    if ($s -ge 24) {
        $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 222, 176, 66))
        $g.SetClip($egg)
        foreach ($d in @(@(0.60, 0.30, 0.08), @(0.40, 0.50, 0.10), @(0.64, 0.56, 0.07), @(0.52, 0.76, 0.11), @(0.30, 0.70, 0.06), @(0.70, 0.78, 0.05))) {
            $r = $s * $d[2]
            $g.FillEllipse($gold, [float]($s * $d[0] - $r / 2), [float]($s * $d[1] - $r / 2), [float]$r, [float]$r)
        }
        $g.ResetClip()
    }

    # Glossy highlight.
    $hl = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
    $g.FillEllipse($hl, [float]($s * 0.31), [float]($s * 0.16), [float]($s * 0.11), [float]($s * 0.20))

    # Thin gold outline.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 222, 176, 66)), ([Math]::Max(1.0, $s / 28.0))
    $g.DrawPath($pen, $egg)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $target)).Path + '\app.ico', $out.ToArray())
Write-Host "Wrote $target ($($sizes -join ', ') px)" -ForegroundColor Green

# Preview for review.
$preview = Join-Path ([System.IO.Path]::GetTempPath()) 'dgb-icon-preview.png'
[System.IO.File]::WriteAllBytes($preview, ($images | Where-Object Size -eq 256).Bytes)
Write-Host "Preview: $preview"
