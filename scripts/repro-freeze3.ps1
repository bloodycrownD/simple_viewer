# repro-freeze3.ps1 - 冻结期抓 UI 线程托管栈 + 线程 CPU 形态
# 启动大图库 → 等 8s（应已冻结）→ dotnet-stack report + 线程 CPU 快照 → 杀进程还原
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$libDir = Join-Path $env:TEMP 'sv-freeze-lib'
# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-frz3'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$stackFile = Join-Path $env:TEMP 'sv-freeze-stack.txt'
if (Test-Path $stackFile) { Remove-Item $stackFile -Force }
try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 800
  $proc = Start-Process $exe -PassThru
  Start-Sleep -Seconds 8

  # 线程 CPU 形态（两轮采样间隔 3s）：主 UI 线程（通常线程 id 最小之一）CPU 是否在涨
  $p1 = Get-Process -Id $proc.Id
  $t1 = $p1.Threads | Sort-Object Id | ForEach-Object { '{0}:{1}' -f $_.Id, $_.TotalProcessorTime.TotalMilliseconds }
  Write-Output ('RESPONDING: ' + $p1.Responding)
  Start-Sleep -Seconds 3
  $p2 = Get-Process -Id $proc.Id
  $t2 = $p2.Threads | Sort-Object Id | ForEach-Object { '{0}:{1}' -f $_.Id, $_.TotalProcessorTime.TotalMilliseconds }
  Write-Output ('RESPONDING-2: ' + $p2.Responding + '  CPU-total: ' + [int]$p2.TotalProcessorTime.TotalSeconds + 's')

  # CPU 增量最大的线程（找忙线程）
  $map1 = @{}; foreach ($e in $t1) { $kv = $e -split ':'; $map1[$kv[0]] = [double]$kv[1] }
  $deltas = @()
  foreach ($e in $t2) { $kv = $e -split ':'; if ($map1.ContainsKey($kv[0])) { $deltas += [PSCustomObject]@{ Tid = $kv[0]; Delta = [double]$kv[1] - $map1[$kv[0]] } } }
  $deltas | Sort-Object Delta -Descending | Select-Object -First 6 | ForEach-Object { Write-Output ('THREAD ' + $_.Tid + ' cpu+' + [int]$_.Delta + 'ms') }

  # 托管栈
  & dotnet-stack report -p $proc.Id > $stackFile 2>&1
  Write-Output ('STACK-FILE: ' + $stackFile + ' size=' + (Get-Item $stackFile).Length)

  taskkill /IM viewer.exe /F 2>$null | Out-Null
}
finally {
  Start-Sleep -Milliseconds 500
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
