# repro-freeze2.ps1 - 复用已生成图库二次启动（缩略图缓存热）对照：冻结是否只在冷缓存（解码路径）出现
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$libDir = Join-Path $env:TEMP 'sv-freeze-lib'
if (-not (Test-Path $libDir)) { Write-Output 'NO-LIB（先跑 repro-freeze.ps1）'; exit 1 }

$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-frz2'
Copy-Item $settings $backup -Force
try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 800
  $markBefore = (Get-Item "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log").Length
  Start-Process 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
  Start-Sleep -Seconds 45
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  $fs = [IO.File]::OpenRead("$env:LOCALAPPDATA\SimpleViewer\logs\startup.log")
  $fs.Seek($markBefore, 'Begin') | Out-Null
  $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
  $newLog = $sr.ReadToEnd(); $sr.Dispose()
  $freeze = ([regex]::Matches($newLog, 'UI 无响应|UI 鏃犲搷搴')).Count
  $decodeCount = ([regex]::Matches($newLog, 'thumb:decode')).Count
  Write-Output ('FREEZE-ENTRIES: ' + $freeze + '（冷缓存首轮为 1+）')
  Write-Output ('DECODE-COUNT: ' + $decodeCount)
  foreach ($line in ($newLog -split "`r?`n")) {
    if ($line -match 'UI 无响应|UI 鏃犲搷搴|scan:end') { Write-Output ('  ' + $line.Trim()) }
  }
}
finally {
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
