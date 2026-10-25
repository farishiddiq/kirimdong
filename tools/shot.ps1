# Captures a process's main window to a PNG with PrintWindow, so it works even when other windows cover it.
#   powershell -File tools/shot.ps1 -ProcessId 1234 -Out shot.png
param([int]$ProcessId, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[Win]::SetProcessDPIAware() | Out-Null
$h = (Get-Process -Id $ProcessId).MainWindowHandle
$win = New-Object Win+RECT; $vis = New-Object Win+RECT
[Win]::GetWindowRect($h, [ref]$win) | Out-Null
[Win]::DwmGetWindowAttribute($h, 9, [ref]$vis, 16) | Out-Null   # visible bounds, without the resize border
$w = $win.R - $win.L; $hgt = $win.B - $win.T
$full = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($full)
$dc = $g.GetHdc()
[Win]::PrintWindow($h, $dc, 2) | Out-Null                         # PW_RENDERFULLCONTENT
$g.ReleaseHdc($dc); $g.Dispose()
$crop = New-Object System.Drawing.Rectangle ($vis.L - $win.L), ($vis.T - $win.T), ($vis.R - $vis.L), ($vis.B - $vis.T)
$bmp = $full.Clone($crop, $full.PixelFormat)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$full.Dispose(); $bmp.Dispose()
Write-Host "$Out $($crop.Width)x$($crop.Height)"
