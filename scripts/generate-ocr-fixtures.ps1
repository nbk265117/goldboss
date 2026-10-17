<#
.SYNOPSIS
  Regenerates the synthetic OCR test images in tests/DofusCompanion.Ocr.Tests/Fixtures/ocr.
  Images are text rendered with Windows system fonts on plain backgrounds: no game assets are used,
  so the dataset is freely usable. The manifest lists the exact rendered text and expected entities.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\tests\DofusCompanion.Ocr.Tests\Fixtures\ocr'
New-Item -ItemType Directory -Force $out | Out-Null
$rng = [System.Random]::new(1234) # deterministic noise

function Render([string]$name, [string[]]$lines, [int]$size, [string]$fg, [string]$bg, [string]$font = 'Segoe UI',
                [double]$downscale = 1.0, [int]$noise = 0, [switch]$blur, [int]$width = 900) {
    $lineH = [int]($size * 1.7)
    $h = 30 + $lineH * $lines.Count
    $bmp = New-Object System.Drawing.Bitmap $width, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml($bg))
    $f = New-Object System.Drawing.Font $font, $size, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($fg))
    for ($i = 0; $i -lt $lines.Count; $i++) { $g.DrawString($lines[$i], $f, $b, 16, 15 + $i * $lineH) }
    $g.Dispose()
    if ($noise -gt 0) {
        for ($y = 0; $y -lt $bmp.Height; $y++) { for ($x = 0; $x -lt $bmp.Width; $x += 1) {
            if ($rng.Next(100) -lt $noise) { $v = $rng.Next(256); $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($v, $v, $v)) } } }
    }
    if ($blur) {
        $small = New-Object System.Drawing.Bitmap $bmp, ([int]($bmp.Width / 2)), ([int]($bmp.Height / 2))
        $bmp = New-Object System.Drawing.Bitmap $small, $bmp.Width, $bmp.Height
    }
    if ($downscale -ne 1.0) { $bmp = New-Object System.Drawing.Bitmap $bmp, ([int]($bmp.Width * $downscale)), ([int]($bmp.Height * $downscale)) }
    $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$cases = @(
    @{ n = 'clean-single';        l = @('Kralamoure Géant');                              s = 28; fg = '#111111'; bg = '#FFFFFF'; e = @('boss.kralamoure-geant') },
    @{ n = 'clean-level-suffix';  l = @('Comte Harebourg (Niv. 200)');                    s = 26; fg = '#111111'; bg = '#F0F0F0'; e = @('boss.comte-harebourg') },
    @{ n = 'dark-ui';             l = @('Missiz Frizz', 'PV 12000  PA 8  PM 4');           s = 22; fg = '#E8E2C8'; bg = '#2A2620'; e = @('boss.missiz-frizz') },
    @{ n = 'multi-names';         l = @('Tal Kasha', 'Kimbo', 'Merkator');                 s = 24; fg = '#F5F5F5'; bg = '#1E2A33'; e = @('boss.tal-kasha', 'boss.kimbo', 'boss.merkator') },
    @{ n = 'small-text';          l = @('Founoroshi');                                      s = 13; fg = '#DDDDDD'; bg = '#333333'; e = @('boss.founoroshi'); w = 400 },
    @{ n = 'blurred';             l = @('Guerre');                                          s = 30; fg = '#000000'; bg = '#FFFFFF'; e = @('boss.guerre'); blur = $true; w = 500 },
    @{ n = 'noisy';               l = @('Meulou');                                          s = 30; fg = '#000000'; bg = '#E0E0E0'; e = @('boss.meulou'); noise = 6; w = 500 },
    @{ n = 'no-entity';           l = @('Tour 4 - Fin du tour dans 20 s', 'PA 11  PM 6'); s = 22; fg = '#FFFFFF'; bg = '#202020'; e = @() },
    @{ n = 'downscaled-low-res';  l = @('Kralamoure Géant');                              s = 28; fg = '#FFFFFF'; bg = '#3A3A3A'; e = @('boss.kralamoure-geant'); down = 0.45 }
)

$manifest = foreach ($c in $cases) {
    $params = @{ name = $c.n; lines = $c.l; size = $c.s; fg = $c.fg; bg = $c.bg }
    if ($c.w) { $params.width = $c.w }
    if ($c.noise) { $params.noise = $c.noise }
    if ($c.blur) { $params.blur = $true }
    if ($c.down) { $params.downscale = $c.down }
    Render @params
    [ordered]@{ file = "$($c.n).png"; text = ($c.l -join "`n"); entities = @($c.e) }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $out 'manifest.json')
"Generated $($cases.Count) fixtures in $out"
