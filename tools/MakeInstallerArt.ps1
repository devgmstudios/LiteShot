# Generates the installer artwork from app.ico:
#   installer\Dialog.bmp (493x312) — welcome/finish dialogs; the left 164 px is artwork, the rest stays white for text
#   installer\Banner.bmp (493x58)  — top banner of the other dialogs; text sits on the left, the icon on the right
# Run with Windows PowerShell: powershell -File tools/MakeInstallerArt.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'installer'

# The 256 px frame of app.ico is a PNG; read it straight out of the icon directory.
$ico = [IO.File]::ReadAllBytes((Join-Path $root 'src\LiteShot\app.ico'))
$count = [BitConverter]::ToUInt16($ico, 4)
$icon = $null
for ($i = 0; $i -lt $count; $i++) {
    $e = 6 + 16 * $i
    if ($ico[$e] -eq 0) {
        $size = [BitConverter]::ToInt32($ico, $e + 8); $offset = [BitConverter]::ToInt32($ico, $e + 12)
        $icon = [Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $ico[$offset..($offset + $size - 1)])))
    }
}

function New-Canvas($w, $h) {
    $b = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::White)
    return $b, $g
}

# Dialog: orange glass panel on the left.
$dialog, $g = New-Canvas 493 312
$panel = New-Object Drawing.Rectangle 0, 0, 164, 312
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush $panel, ([Drawing.Color]::FromArgb(255, 166, 72)), ([Drawing.Color]::FromArgb(240, 92, 18)), 90
$g.FillRectangle($grad, $panel)
foreach ($c in @(@(-40, 200, 180, 40), @(90, -50, 140, 34), @(60, 240, 120, 28))) {
    $b = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($c[3], 255, 255, 255))
    $g.FillEllipse($b, $c[0], $c[1], $c[2], $c[2])
}
$g.DrawImage($icon, 34, 54, 96, 96)
$title = New-Object Drawing.Font 'Segoe UI Semibold', 17
$sub = New-Object Drawing.Font 'Segoe UI', 9
$center = New-Object Drawing.StringFormat; $center.Alignment = 'Center'
$g.DrawString('LiteShot', $title, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 162, 164, 34), $center)
$g.DrawString('by Devgm, LLC', $sub, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(225, 255, 255, 255))), (New-Object Drawing.RectangleF 0, 196, 164, 20), $center)
$dialog.Save((Join-Path $out 'Dialog.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)

# Banner: white with a soft orange wash and the icon on the right.
$banner, $g = New-Canvas 493 58
$wash = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Rectangle 290, 0, 204, 58), ([Drawing.Color]::White), ([Drawing.Color]::FromArgb(255, 236, 220)), 0
$wash.WrapMode = 'TileFlipX'
$g.FillRectangle($wash, 300, 0, 193, 58)
$g.DrawImage($icon, 437, 9, 40, 40)
$banner.Save((Join-Path $out 'Banner.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
Write-Host "Wrote installer artwork to $out"
