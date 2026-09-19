# 冻结压力测试：N 轮"清缓存全新解码风暴"启动，验证 UI 闸门修复
$exe = 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$work = 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0'
$cache = "$env:LOCALAPPDATA\SimpleViewer\thumbcache"
$log = "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log"

1..3 | ForEach-Object {
    $round = $_
    Get-ChildItem $cache -File -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe -WorkingDirectory $work
    Start-Sleep 20

    $p = Get-Process viewer -ErrorAction SilentlyContinue
    if (-not $p) { Write-Host "ROUND ${round}: EXITED"; return }

    $logText = if (Test-Path $log) { [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) } else { '' }
    $frozen = $logText -match 'UI 无响应'
    $cacheCount = (Get-ChildItem $cache -File -ErrorAction SilentlyContinue).Count
    Write-Host ("ROUND {0}: Alive Responding={1} CPU={2}s Cache={3} FROZEN={4}" -f `
        $round, $p.Responding, [math]::Round($p.TotalProcessorTime.TotalSeconds, 1), $cacheCount, $frozen)

    Stop-Process -Id $p.Id -Force
    Start-Sleep 2
}
