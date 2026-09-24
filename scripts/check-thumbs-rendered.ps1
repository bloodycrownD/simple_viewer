# PrintWindow capture of the viewer window; verify waterfall card area has real image content
# (non-chrome pixel variance) — proves thumbnails applied, not stuck behind the first-frame gate.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing
$proc = Get-Process viewer -ErrorAction Stop | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
if (-not $hwnd -or $hwnd -eq [IntPtr]::Zero) { "NO WINDOW"; exit 1 }
$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
"window ${w}x${h}"
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hd = $g.GetHdc()
[WinCap]::PrintWindow($hwnd, $hd, 2) | Out-Null
$g.ReleaseHdc($hd); $g.Dispose()
# sample the card grid area (below toolbar ~90px, left of right panel): count distinct colors + non-chrome pixels
$colors = @{}; $nonChrome = 0; $samples = 0
for ($y = 200; $y -lt ($h - 60); $y += 24) {
    for ($x = 420; $x -lt ($w - 60); $x += 24) {
        $c = $bmp.GetPixel($x, $y)
        $samples++
        $key = "$($c.R),$($c.G),$($c.B)"
        if (-not $colors.ContainsKey($key)) { $colors[$key] = 0 }
        $colors[$key]++
        if (-not (($c.R -ge 30 -and $c.R -le 50) -and ($c.G -ge 30 -and $c.G -le 52) -and ($c.B -ge 38 -and $c.B -le 56))) { $nonChrome++ }
    }
}
"samples=$samples distinct=$($colors.Count) nonChrome=$nonChrome"
if ($colors.Count -ge 20 -and $nonChrome -ge ($samples * 0.25)) { "THUMBNAILS-RENDERED-OK" } else { "AREA-LOOKS-EMPTY" }
$bmp.Dispose()
