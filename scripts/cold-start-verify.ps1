# Cold-start stall verification: clear thumbcache, launch viewer, watch startup.log for
# unresponsive-watchdog entries (ASCII-safe pattern "[UI ") timestamped AFTER launch.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File cold-start-verify.ps1 [rounds=3] [waitSec=60]
param([int]$Rounds = 3, [int]$WaitSec = 60)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ColdWin {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
"@
$viewer = 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$log = "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log"
$cache = "$env:LOCALAPPDATA\SimpleViewer\thumbcache"

$fail = 0
for ($round = 1; $round -le $Rounds; $round++) {
    taskkill /IM viewer.exe /F 2>$null | Out-Null
    Start-Sleep -Seconds 1
    Remove-Item "$cache\*.jpg" -Force -ErrorAction SilentlyContinue
    $launchAt = Get-Date
    $p = Start-Process -FilePath $viewer -PassThru
    # 屏幕外定位（RULE：UI 验证一律屏幕外不抢焦点）：主窗口出现即移到桌面右侧之外，
    # SWP_NOSIZE|SWP_NOACTIVATE——先前的 60s 静置窗口会占着用户前台一分钟（违反约束）。
    $dl = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 200
        $p.Refresh()
        if ($p.HasExited -or $p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
        [void][ColdWin]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, ([ColdWin]::GetSystemMetrics(0) + 2000), 200, 0, 0, 0x0011)
    }
    Start-Sleep -Seconds $WaitSec
    # "[UI " matches the unresponsive-watchdog headline (ASCII prefix of the localized line);
    # dump/stack follow-on lines are secondary and always accompany it.
    $stalls = Get-Content $log -Encoding UTF8 |
        Where-Object { $_.StartsWith('[') -and $_ -match '^\[[0-9:\-\. ]+\] \[UI ' } |
        Where-Object {
            $ts = [datetime]::ParseExact($_.Substring(1, 19), 'yyyy-MM-dd HH:mm:ss', $null)
            $ts -ge $launchAt
        }
    if ($stalls) {
        $fail++
        "round ${round}: STALL DETECTED"
        $stalls | ForEach-Object { "  $_" }
    } else {
        "round ${round}: clean (no stall since launch)"
    }
}
taskkill /IM viewer.exe /F 2>$null | Out-Null
"RESULT: $fail/$Rounds rounds stalled"
