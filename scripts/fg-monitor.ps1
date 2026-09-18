# 前置 viewer 窗口并监控渲染效果（走查诊断用）
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@

$p = Get-Process viewer -ErrorAction SilentlyContinue
if (-not $p -or $p.MainWindowHandle -eq 0) { Write-Host NO_WINDOW; exit }

[Win32]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[Win32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Write-Host FOREGROUNDED

1..6 | ForEach-Object {
    Start-Sleep 10
    $p = Get-Process viewer -ErrorAction SilentlyContinue
    if ($p) {
        $cache = (Get-ChildItem "$env:LOCALAPPDATA\SimpleViewer\thumbcache" -File).Count
        $log = @(Get-Content "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log" -ErrorAction SilentlyContinue).Count
        Write-Host ("{0} t={1,3}s Responding={2} WS={3}MB CPU={4}s Cache={5} Log={6}" -f `
            (Get-Date -Format HH:mm:ss), ($_ * 10), $p.Responding,
            [math]::Round($p.WorkingSet64 / 1MB), [math]::Round($p.TotalProcessorTime.TotalSeconds, 1),
            $cache, $log)
    } else {
        Write-Host EXITED
        break
    }
}
