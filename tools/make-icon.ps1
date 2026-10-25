# Draws the kirimdong icon (a taped cardboard box with an orange shipping label) and writes
# src/Kirimdong.App/Assets/app.ico with PNG frames from 16 to 256 px (largest last).
#   powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root 'src\Kirimdong.App\Assets\app.ico'

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    function Fill($hex, [float[]]$pts) {
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))
        $points = for ($i = 0; $i -lt $pts.Length; $i += 2) { New-Object System.Drawing.PointF ($pts[$i] * $s), ($pts[$i + 1] * $s) }
        $g.FillPolygon($brush, [System.Drawing.PointF[]]$points)
        $brush.Dispose()
    }
    Fill '#A97A42' @(16, 58, 240, 58, 240, 96, 16, 96)          # lid
    Fill '#C69559' @(28, 96, 228, 96, 228, 234, 28, 234)        # box front
    Fill '#B5844A' @(28, 96, 228, 96, 228, 108, 28, 108)        # lid shadow step
    Fill '#E6C995' @(110, 58, 146, 58, 146, 234, 110, 234)      # tape
    Fill '#FF6A13' @(156, 156, 216, 156, 216, 214, 156, 214)    # label
    if ($size -ge 32) {
        Fill '#0D141A' @(166, 168, 172, 168, 172, 202, 166, 202)
        Fill '#0D141A' @(178, 168, 181, 168, 181, 202, 178, 202)
        Fill '#0D141A' @(187, 168, 195, 168, 195, 202, 187, 202)
        Fill '#0D141A' @(200, 168, 204, 168, 204, 202, 200, 202)
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
[IO.File]::WriteAllBytes($out, $ms.ToArray())
Write-Host "Wrote $out ($($ms.Length) bytes)"
