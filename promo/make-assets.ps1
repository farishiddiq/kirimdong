# Prepares poster assets in promo/assets:
#   app-icon.png   the 256px frame of the app icon
#   offer-card.png the incoming-parcel card cut out of the real screenshot kirimdong-offer.png
#   powershell -ExecutionPolicy Bypass -File promo/make-assets.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'promo\assets'

$ico = [IO.File]::ReadAllBytes((Join-Path $root 'src\Kirimdong.App\Assets\app.ico'))
$count = [BitConverter]::ToUInt16($ico, 4); $entry = 6 + 16 * ($count - 1)
$size = [BitConverter]::ToInt32($ico, $entry + 8); $offset = [BitConverter]::ToInt32($ico, $entry + 12)
[IO.File]::WriteAllBytes((Join-Path $assets 'app-icon.png'), $ico[$offset..($offset + $size - 1)])

# The offer card: find the paper-coloured block in the middle of the screenshot and cut it out.
$shot = [System.Drawing.Bitmap]::FromFile((Join-Path $assets 'kirimdong-offer.png'))
function IsPaper($c) { $c.R -ge 240 -and $c.G -ge 236 -and $c.B -ge 228 }
$midY = 400; $left = 0; $right = 0
for ($x = 200; $x -lt 600; $x++) { if (IsPaper $shot.GetPixel($x, $midY)) { $left = $x; break } }
for ($x = 940; $x -gt 600; $x--) { if (IsPaper $shot.GetPixel($x, $midY)) { $right = $x; break } }
$midX = [int](($left + $right) / 2); $top = 0; $bottom = 0
for ($y = 120; $y -lt 400; $y++) { if (IsPaper $shot.GetPixel($midX - 120, $y)) { $top = $y; break } }
for ($y = 700; $y -gt 400; $y--) { if (IsPaper $shot.GetPixel($midX, $y)) { $bottom = $y; break } }
$rect = New-Object System.Drawing.Rectangle $left, $top, ($right - $left + 1), ($bottom - $top + 1)
$card = $shot.Clone($rect, $shot.PixelFormat)
$card.Save((Join-Path $assets 'offer-card.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "offer card: $rect"
$card.Dispose(); $shot.Dispose()
