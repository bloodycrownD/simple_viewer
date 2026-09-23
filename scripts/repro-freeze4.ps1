# repro-freeze4.ps1 - 冻结自动探测 + 冻结期抓栈：最多 4 轮启动，每轮探 30s，Responding=False 即抓 dotnet-stack + 线程 CPU
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$libDir = Join-Path $env:TEMP 'sv-freeze-lib'
# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-frz4'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$stackFile = Join-Path $env:TEMP 'sv-freeze-stack-frozen.txt'
if (Test-Path $stackFile) { Remove-Item $stackFile -Force }
try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  $frozen = $false
  for ($round = 1; $round -le 4 -and -not $frozen; $round++) {
    taskkill /IM viewer.exe /F 2>$null | Out-Null
    Start-Sleep -Milliseconds 900
    $proc = Start-Process $exe -PassThru
    Write-Output ('ROUND ' + $round + ' pid=' + $proc.Id)
    for ($i = 0; $i -lt 15 -and -not $frozen; $i++) {
      Start-Sleep -Seconds 2
      try { $p = Get-Process -Id $proc.Id -ErrorAction Stop } catch { break }
      if (-not $p.Responding) {
        $frozen = $true
        Write-Output ('FROZEN at ~' + ($i * 2 + 2) + 's! CPU-total=' + [int]$p.TotalProcessorTime.TotalSeconds + 's WS=' + [int]($p.WorkingSet64 / 1MB) + 'MB')
        $t1 = $p.Threads | ForEach-Object { '{0}:{1}' -f $_.Id, $_.TotalProcessorTime.TotalMilliseconds }
        Start-Sleep -Seconds 3
        $p2 = Get-Process -Id $proc.Id
        $t2 = $p2.Threads | ForEach-Object { '{0}:{1}' -f $_.Id, $_.TotalProcessorTime.TotalMilliseconds }
        $map1 = @{}; foreach ($e in $t1) { $kv = $e -split ':'; $map1[$kv[0]] = [double]$kv[1] }
        $deltas = @()
        foreach ($e in $t2) { $kv = $e -split ':'; if ($map1.ContainsKey($kv[0])) { $deltas += [PSCustomObject]@{ Tid = $kv[0]; Delta = [double]$kv[1] - $map1[$kv[0]] } } }
        $deltas | Sort-Object Delta -Descending | Select-Object -First 6 | ForEach-Object { Write-Output ('  THREAD ' + $_.Tid + ' cpu+' + [int]$_.Delta + 'ms') }
        & dotnet-stack report -p $proc.Id > $stackFile 2>&1
        Write-Output ('STACK captured: ' + (Get-Item $stackFile).Length + ' bytes')
      }
    }
    if (-not $frozen) { Write-Output ('  not frozen in 30s (round ' + $round + ')') }
  }
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Write-Output ('RESULT: ' + $(if ($frozen) { 'FROZEN-AND-CAPTURED' } else { 'NOT-REPRODUCED-4-ROUNDS' }))
}
finally {
  Start-Sleep -Milliseconds 500
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
