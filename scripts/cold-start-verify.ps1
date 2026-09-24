# Cold-start stall verification: clear thumbcache, launch viewer, watch startup.log for
# unresponsive-watchdog entries (ASCII-safe pattern "[UI ") timestamped AFTER launch.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File cold-start-verify.ps1 [rounds=3] [waitSec=60]
param([int]$Rounds = 3, [int]$WaitSec = 60)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$viewer = 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$log = "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log"
$cache = "$env:LOCALAPPDATA\SimpleViewer\thumbcache"

$fail = 0
for ($round = 1; $round -le $Rounds; $round++) {
    taskkill /IM viewer.exe /F 2>$null | Out-Null
    Start-Sleep -Seconds 1
    Remove-Item "$cache\*.jpg" -Force -ErrorAction SilentlyContinue
    $launchAt = Get-Date
    Start-Process -FilePath $viewer | Out-Null
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
