# Screenshot the running viewer window (PrintWindow) for visual inspection.
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PW3 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
}
"@
$p = Get-Process viewer -ErrorAction Stop | Select-Object -First 1
[PW3]::MoveWindow($p.MainWindowHandle, 40, 40, 1600, 900, $true) | Out-Null
Start-Sleep -Milliseconds 800
$bmp = New-Object System.Drawing.Bitmap 1600, 900
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hd = $g.GetHdc()
[PW3]::PrintWindow($p.MainWindowHandle, $hd, 2) | Out-Null
$g.ReleaseHdc($hd); $g.Dispose()
$out = "$env:TEMP\sv-direct.png"
$bmp.Save($out); $bmp.Dispose()
"saved $out"
