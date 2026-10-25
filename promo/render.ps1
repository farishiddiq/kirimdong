# Renders promo/poster.html to promo/out/*.png with headless Edge.
#   powershell -ExecutionPolicy Bypass -File promo/render.ps1
$ErrorActionPreference = 'Continue' # Edge logs harmless noise to stderr
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $dir 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$profileDir = Join-Path $env:TEMP 'kirimdong-poster-edge'
$url = 'file:///' + ($dir -replace '\\', '/') + '/poster.html'
$formats = @(@('portrait', 1080, 1350, 1), @('square', 1080, 1080, 1), @('story', 1080, 1920, 1), @('portrait', 1080, 1350, 2))
foreach ($f in $formats) {
    $name = if ($f[3] -eq 2) { "kirimdong-$($f[0])@2x.png" } else { "kirimdong-$($f[0]).png" }
    & $edge --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profileDir" `
        --virtual-time-budget=4000 --window-size="$($f[1]),$($f[2])" --force-device-scale-factor=$($f[3]) `
        --screenshot="$out\$name" "$url`?f=$($f[0])" 2>$null | Out-Null
    $img = [System.Drawing.Image]::FromFile("$out\$name")
    Write-Host "$name $($img.Width)x$($img.Height)"
    $img.Dispose()
}
